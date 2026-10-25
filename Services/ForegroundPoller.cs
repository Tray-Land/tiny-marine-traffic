using Microsoft.UI.Dispatching;
using TinyMarineTraffic.Core;
using System.Diagnostics;

namespace TinyMarineTraffic.Services;

/// <summary>
/// Runs a refresh on the UI thread on an interval while something is on screen, and not at all
/// otherwise. One refresh at a time, exponential backoff on failure, an immediate re-check when
/// the network comes back, and in-flight work is cancelled on <see cref="Stop"/>.
/// </summary>
internal sealed class ForegroundPoller
{
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(10);

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _staleAfter;
    private readonly Func<CancellationToken, Task> _refresh;
    private CancellationTokenSource? _inFlight;
    private int _consecutiveFailures;
    private DateTimeOffset _lastSuccess = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    /// <param name="staleAfter">How old data may be when <see cref="Start"/> skips the immediate
    /// refresh. Defaults to half the interval, so a quick close-and-reopen doesn't refetch.</param>
    public ForegroundPoller(DispatcherQueue dispatcher, TimeSpan interval, Func<CancellationToken, Task> refresh, TimeSpan? staleAfter = null)
    {
        _dispatcher = dispatcher;
        _interval = interval;
        _refresh = refresh;
        _staleAfter = staleAfter ?? interval / 2;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false; // Re-armed after each refresh, so refreshes never overlap.
        _timer.Tick += (_, _) => _ = RefreshNowAsync();
    }

    /// <summary>Raised on the UI thread when a refresh throws (other than by being cancelled).</summary>
    public event EventHandler<Exception>? Failed;

    public bool IsRunning { get; private set; }

    public void MarkSuccess()
    {
        _consecutiveFailures = 0;
        _lastSuccess = DateTimeOffset.UtcNow;
    }

    public void Restart()
    {
        Stop();
        _lastSuccess = DateTimeOffset.MinValue;
        Start();
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        ConnectivityService.NetworkStatusChanged += OnNetworkStatusChanged;
        if (DateTimeOffset.UtcNow - _lastSuccess > _staleAfter)
        {
            _ = RefreshNowAsync();
        }
        else
        {
            Rearm();
        }
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        ConnectivityService.NetworkStatusChanged -= OnNetworkStatusChanged;
        _timer.Stop();
        _inFlight?.Cancel();
    }

    /// <summary>Refreshes now (e.g. a refresh button), unless a refresh is already running.</summary>
    public async Task RefreshNowAsync()
    {
        if (_inFlight is not null)
        {
            return;
        }
        if (DateTimeOffset.UtcNow < _retryAt)
        {
            Rearm();
            return;
        }

        _timer.Stop();
        using CancellationTokenSource cts = new();
        _inFlight = cts;
        try
        {
            await _refresh(cts.Token);
            _consecutiveFailures = 0;
            _lastSuccess = DateTimeOffset.UtcNow;
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            // Stopped mid-refresh; nothing to report.
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            if (ex is AisServiceException { RetryAt: { } retryAt })
            {
                _retryAt = retryAt;
            }
            Debug.WriteLine($"Foreground connection failed: {ex}");
            Failed?.Invoke(this, ex);
        }
        finally
        {
            _inFlight = null;
            if (IsRunning && cts.IsCancellationRequested)
            {
                // Hidden and reshown while this refresh was unwinding: the data is stale, go again.
                _dispatcher.TryEnqueue(() => _ = RefreshNowAsync());
            }
            else if (IsRunning)
            {
                Rearm();
            }
        }
    }

    /// <summary>
    /// Doubles the interval per consecutive failure, capped at <see cref="MaxInterval"/>, so a
    /// service that is down gets a few polite retries and then one call every ten minutes.
    /// </summary>
    public static TimeSpan NextInterval(TimeSpan configured, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return configured;
        }

        return RetryPolicy.NextInterval(configured, consecutiveFailures);
    }

    private void Rearm()
    {
        _timer.Stop();
        _timer.Interval = NextInterval(_interval, _consecutiveFailures);
        if (_retryAt - DateTimeOffset.UtcNow > _timer.Interval)
        {
            _timer.Interval = _retryAt - DateTimeOffset.UtcNow;
        }
        _timer.Start();
    }

    // Raised on a background thread; a network change usually means the answer just changed.
    private void OnNetworkStatusChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        if (IsRunning && ConnectivityService.IsInternetAvailable)
        {
            _consecutiveFailures = 0;
            _ = RefreshNowAsync();
        }
    });
}
