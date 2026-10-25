using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyMarineTraffic.Core;

public sealed class AisServiceException(string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

public sealed class OpenWatersClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string UserAgent = "TinyMarineTraffic/1.0";

    public async Task<TrafficSnapshot> GetSnapshotAsync(Harbor harbor, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        string boxes = string.Join("&", harbor.Boxes.Select(box => $"bbox={box}"));
        using HttpRequestMessage request = new(HttpMethod.Get,
            $"https://ais.openwaters.io/v1/vessels?{boxes}&kind=vessel&max_age=30m&max_age_moving=30m");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            DateTimeOffset retry = response.Headers.RetryAfter?.Date ??
                DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            throw new AisServiceException("Open Waters has rate-limited this connection.", retry);
        }
        response.EnsureSuccessStatusCode();
        byte[] json = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        return AisParser.ParseSnapshot(json);
    }

    public async Task StreamAsync(Harbor harbor, Action<VesselUpdate> received, Action connected, CancellationToken cancellationToken)
    {
        using ClientWebSocket socket = new();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        using (CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri("wss://ais.openwaters.io/v1/stream"), connectTimeout.Token).ConfigureAwait(false);
        }
        StreamSubscription subscription = new(harbor.Boxes.Select(b => new[] { b.South, b.West, b.North, b.East }).ToArray());
        byte[] subscribe = JsonSerializer.SerializeToUtf8Bytes(subscription, AisJsonContext.Default.StreamSubscription);
        await socket.SendAsync(subscribe, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[16384];
        while (!cancellationToken.IsCancellationRequested)
        {
            using MemoryStream frame = new();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new AisServiceException("The live connection closed. Reconnecting shortly.");
                }
                if (frame.Length + result.Count > 262144)
                {
                    throw new AisServiceException("Open Waters sent a message larger than the supported limit.");
                }
                frame.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }
            using JsonDocument document = JsonDocument.Parse(frame.GetBuffer().AsMemory(0, (int)frame.Length));
            JsonElement root = document.RootElement;
            switch (AisParser.Text(root, "type"))
            {
                case "welcome":
                    connected();
                    break;
                case "error":
                    throw new AisServiceException(AisParser.Text(root, "error") ?? "Open Waters refused the subscription.");
                case "event":
                    if (AisParser.ParseEvent(root) is { } update)
                    {
                        received(update);
                    }
                    break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}

public sealed record StreamSubscription([property: JsonPropertyName("bbox")] double[][] Boxes)
{
    [JsonPropertyName("type")] public string Type => "subscribe";
    [JsonPropertyName("snapshot")] public bool Snapshot => true;
}

[JsonSerializable(typeof(StreamSubscription))]
[JsonSerializable(typeof(Harbor))]
[JsonSerializable(typeof(AreaCatalog))]
public partial class AisJsonContext : JsonSerializerContext;

public static class RetryPolicy
{
    public static TimeSpan NextInterval(TimeSpan configured, int failures) =>
        TimeSpan.FromMilliseconds(Math.Min(TimeSpan.FromMinutes(10).TotalMilliseconds,
            configured.TotalMilliseconds * Math.Pow(2, Math.Clamp(failures, 0, 10))));
}
