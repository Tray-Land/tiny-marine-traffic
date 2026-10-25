using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using TinyMarineTraffic.Core;
using TinyMarineTraffic.Services;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using WinUIEx.Maps;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapControl = WinUIEx.Maps.MapControl;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapElementTappedEventArgs = WinUIEx.Maps.MapElementTappedEventArgs;
using MapPolyline = WinUIEx.Maps.MapPolyline;

namespace TinyMarineTraffic.Views;

public sealed partial class FlyoutPage : Page, IDisposable
{
    private readonly OpenWatersClient _client = new();
    private readonly VesselStore _store = new();
    private readonly ForegroundPoller _connection;
    private readonly DispatcherQueueTimer _renderTimer;
    private readonly MapElementsLayer _ships = new();
    private readonly MapElementsLayer _coverage = new();
    private readonly MapVisuals _visuals;
    private readonly Dictionary<int, MapIcon> _icons = [];
    private readonly Dictionary<MapIcon, int> _identities = [];
    private readonly ConcurrentQueue<(int Generation, VesselUpdate Update)> _pending = new();
    private readonly Dictionary<string, string> _attribution = [];
    private readonly List<Harbor> _harbors = [];
    private readonly TileLayer _basemap;
    private readonly long _centerCallback;
    private readonly long _zoomCallback;
    private Harbor _harbor = SettingsService.CurrentHarbor;
    private DateTimeOffset _lastSnapshot = DateTimeOffset.MinValue;
    private int _generation;
    private int? _selected;
    private bool _visible;
    private bool _connected;
    private bool _loadingPicker;
    private bool _disposed;
    private bool _mapRevealed;
    private bool _pickingArea;

    public ObservableCollection<VesselRow> Rows { get; } = [];

    public FlyoutPage()
    {
        InitializeComponent();
        _visuals = new MapVisuals();
        _basemap = new TileLayer(new TileLayerOptions
        {
            TileUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
            TileSize = 256,
            MaxSourceZoom = 19,
            RequestHeaders = new Dictionary<string, string> { ["User-Agent"] = "TinyMarineTraffic/1.0" },
        })
        {
            Attribution = "© OpenStreetMap contributors",
            AttributionLink = new Uri("https://www.openstreetmap.org/copyright"),
        };
        Map.Layers.Add(_coverage);
        Map.Layers.Add(_ships);
        _ships.Tapped += Ship_Tapped;
        Map.AddHandler(UIElement.TappedEvent, new TappedEventHandler(Map_Tapped), true);
        _centerCallback = Map.RegisterPropertyChangedCallback(MapControl.CenterProperty, MapView_Changed);
        _zoomCallback = Map.RegisterPropertyChangedCallback(MapControl.ZoomLevelProperty, MapView_Changed);
        Root.ActualThemeChanged += Theme_Changed;
        _connection = new ForegroundPoller(DispatcherQueue, TimeSpan.FromSeconds(5), ConnectAsync, TimeSpan.Zero);
        _connection.Failed += Connection_Failed;
        _renderTimer = DispatcherQueue.CreateTimer();
        _renderTimer.Interval = TimeSpan.FromSeconds(1);
        _renderTimer.Tick += Render_Tick;
        LoadPicker();
        CenterOnHarbor();
        DrawCoverage();
        AreaText.Text = "© OpenStreetMap · AIS: Open Waters";
    }

    public void OnShown()
    {
        if (_visible || _disposed)
        {
            return;
        }
        _visible = true;
        Harbor saved = SettingsService.CurrentHarbor;
        if (saved != _harbor)
        {
            ChangeHarbor(saved);
        }
        LoadPicker();
        if (!Map.Layers.Contains(_basemap))
        {
            Map.Layers.Insert(0, _basemap);
        }
        _renderTimer.Start();
        _connection.Start();
        Render();
        RevealMapAsync();
        ChangePlaceButton.Focus(FocusState.Programmatic);
    }

    public void OnHidden()
    {
        _visible = false;
        PlacesFlyout.Hide();
        if (_pickingArea)
        {
            EndPickingArea();
        }
        _connected = false;
        _generation++;
        _connection.Stop();
        _renderTimer.Stop();
        _pending.Clear();
        Map.Layers.Remove(_basemap);
        MapLoadingRing.IsActive = false;
        ConnectionText.Text = "Paused while hidden";
        App.Current.SetTrayStatus(null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        OnHidden();
        _connection.Failed -= Connection_Failed;
        _renderTimer.Tick -= Render_Tick;
        _ships.Tapped -= Ship_Tapped;
        Map.RemoveHandler(UIElement.TappedEvent, new TappedEventHandler(Map_Tapped));
        Map.UnregisterPropertyChangedCallback(MapControl.CenterProperty, _centerCallback);
        Map.UnregisterPropertyChangedCallback(MapControl.ZoomLevelProperty, _zoomCallback);
        Root.ActualThemeChanged -= Theme_Changed;
        Map.Layers.Clear();
        _ships.MapElements.Clear();
        _coverage.MapElements.Clear();
        _icons.Clear();
        _identities.Clear();
        _store.Clear();
        Rows.Clear();
        Root.Children.Remove(Map);
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        int generation = _generation;
        Harbor harbor = _harbor;
        _connected = false;
        ConnectionText.Text = "Connecting to Open Waters";
        RefreshButton.IsEnabled = false;
        try
        {
            if (DateTimeOffset.UtcNow - _lastSnapshot > TimeSpan.FromSeconds(30))
            {
                TrafficSnapshot snapshot = await _client.GetSnapshotAsync(harbor, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _store.Replace(snapshot);
                _attribution.Clear();
                foreach (var pair in snapshot.Attribution)
                {
                    _attribution[pair.Key] = pair.Value;
                }
                _lastSnapshot = DateTimeOffset.UtcNow;
                Render();
                if (snapshot.Truncated)
                {
                    StatusBar.Title = "Partial snapshot";
                    StatusBar.Message = "Open Waters limited this response. Zoom into a smaller area for more detail.";
                    StatusBar.IsOpen = true;
                }
            }
            RefreshButton.IsEnabled = true;
            await _client.StreamAsync(harbor, update =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_pending.Count > 5000)
                {
                    throw new AisServiceException("Too much traffic for this area. Choose a smaller range in Settings.");
                }
                _pending.Enqueue((generation, update));
            }, () => DispatcherQueue.TryEnqueue(() =>
            {
                if (_visible && generation == _generation)
                {
                    _connected = true;
                    _connection.MarkSuccess();
                    StatusBar.IsOpen = false;
                    Render();
                }
            }), cancellationToken);
        }
        finally
        {
            if (generation == _generation)
            {
                RefreshButton.IsEnabled = !_pickingArea;
            }
        }
    }

    private void Render_Tick(DispatcherQueueTimer sender, object args)
    {
        while (_pending.TryDequeue(out var item))
        {
            if (item.Generation == _generation && _harbor.Contains(item.Update.Vessel.Latitude, item.Update.Vessel.Longitude))
            {
                _store.Apply(item.Update);
                if (item.Update.Attribution is { } attribution)
                {
                    _attribution[item.Update.Vessel.Source?.Split(':')[0] ?? "station"] = attribution;
                }
            }
        }
        _store.Prune(_harbor, DateTimeOffset.UtcNow);
        Render();
    }

    private void Render()
    {
        if (_pickingArea)
        {
            return;
        }
        Vessel[] all = _store.Vessels.Where(v => _harbor.Contains(v.Latitude, v.Longitude))
            .OrderByDescending(v => v.IsMoving).ThenBy(v => _harbor.DistanceNm(v.Latitude, v.Longitude)).ToArray();
        Vessel[] shown = all.Where(v => MovingToggle.IsChecked != true || v.IsMoving).Take(800).ToArray();
        HashSet<int> visible = shown.Select(v => v.Mmsi).ToHashSet();
        foreach (int mmsi in _icons.Keys.Where(id => !visible.Contains(id)).ToArray())
        {
            MapIcon icon = _icons[mmsi];
            _ships.MapElements.Remove(icon);
            _identities.Remove(icon);
            _icons.Remove(mmsi);
        }
        foreach (Vessel vessel in shown)
        {
            if (!_icons.TryGetValue(vessel.Mmsi, out MapIcon? icon))
            {
                icon = new MapIcon(_visuals.Ship(vessel, _selected == vessel.Mmsi), Point(vessel.Latitude, vessel.Longitude))
                {
                    NormalizedAnchorPoint = new Point(0.5, 0.5),
                };
                _icons[vessel.Mmsi] = icon;
                _identities[icon] = vessel.Mmsi;
                _ships.MapElements.Add(icon);
            }
            else
            {
                icon.Location = Point(vessel.Latitude, vessel.Longitude);
                icon.IconElement = _visuals.Ship(vessel, _selected == vessel.Mmsi);
            }
            icon.ZIndex = _selected == vessel.Mmsi ? 10 : vessel.IsMoving ? 2 : 1;
        }
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!visible.Contains(Rows[i].Vessel.Mmsi))
            {
                Rows.RemoveAt(i);
            }
        }
        for (int i = 0; i < shown.Length; i++)
        {
            int existing = -1;
            for (int j = i; j < Rows.Count; j++)
            {
                if (Rows[j].Vessel.Mmsi == shown[i].Mmsi)
                {
                    existing = j;
                    break;
                }
            }
            if (existing < 0)
            {
                Rows.Insert(i, new VesselRow(shown[i]));
            }
            else
            {
                if (existing != i)
                {
                    Rows.Move(existing, i);
                }
                Rows[i].Update(shown[i]);
            }
            if (VesselList.ContainerFromItem(Rows[i]) is ListViewItem container)
            {
                LabelContainer(container, Rows[i]);
            }
        }
        CountText.Text = shown.Length == 800 ? $"800 of {all.Length} vessels" : $"{shown.Length} vessels";
        EmptyText.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _lastSnapshot == DateTimeOffset.MinValue ? "Waiting for vessel reports" :
            MovingToggle.IsChecked == true ? "No moving vessels reported in this area." :
            "No vessels reported in the last 30 minutes. Try another waterway or a larger range.";
        if (_connected)
        {
            ConnectionText.Text = $"Live · {all.Count(v => v.IsMoving)} moving · {_harbor.RadiusNm:0} nm area";
            App.Current.SetTrayStatus($"{_harbor.Name}: {all.Length} vessels");
        }
        UpdateDetails();
    }

    private void Connection_Failed(object? sender, Exception error)
    {
        _connected = false;
        bool online = ConnectivityService.IsInternetAvailable;
        ConnectionText.Text = _lastSnapshot == DateTimeOffset.MinValue ? "Waiting to reconnect" : $"Last snapshot {_lastSnapshot.LocalDateTime:t}";
        StatusBar.Title = online ? "Live traffic interrupted" : "No internet connection";
        StatusBar.Message = error is AisServiceException { RetryAt: { } retryAt }
            ? $"Rate limited. Retrying after {retryAt.LocalDateTime:t}."
            : error is AisServiceException ? error.Message :
            "Keeping the last reports. Reconnecting automatically.";
        StatusBar.IsOpen = true;
        App.Current.SetTrayStatus("Traffic paused · open to retry");
    }

    private void LoadPicker()
    {
        _loadingPicker = true;
        _harbors.Clear();
        _harbors.AddRange(SettingsService.Areas.Areas);
        int index = _harbors.IndexOf(_harbor);
        HarborPicker.ItemsSource = _harbors.Select(h => h.Name).ToArray();
        HarborPicker.SelectedIndex = index;
        string place = $"Change place ({_harbor.Name})";
        AutomationProperties.SetName(ChangePlaceButton, place);
        ToolTipService.SetToolTip(ChangePlaceButton, place);
        string home = $"Go to home area ({SettingsService.Areas.HomeName})";
        AutomationProperties.SetName(HomeButton, home);
        ToolTipService.SetToolTip(HomeButton, home);
        _loadingPicker = false;
    }

    private void ChangeHarbor(Harbor harbor)
    {
        SettingsService.CurrentHarbor = harbor;
        _connection.Stop();
        _generation++;
        _connected = false;
        _harbor = harbor;
        _store.Clear();
        _pending.Clear();
        _attribution.Clear();
        _lastSnapshot = DateTimeOffset.MinValue;
        _selected = null;
        StatusBar.IsOpen = false;
        LoadPicker();
        DrawCoverage();
        CenterOnHarbor();
        Render();
        if (_visible)
        {
            _connection.Restart();
        }
    }

    private void Harbor_Changed(object sender, SelectionChangedEventArgs e)
    {
        int index = HarborPicker.SelectedIndex;
        if (!_loadingPicker && index >= 0 && index < _harbors.Count)
        {
            try
            {
                ChangeHarbor(_harbors[index]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowAreaError(ex);
                LoadPicker();
            }
            PlacesFlyout.Hide();
            ChangePlaceButton.Focus(FocusState.Programmatic);
        }
    }

    private void CenterOnHarbor()
    {
        Map.Center = Point(_harbor.Latitude, _harbor.Longitude);
        Map.ZoomLevel = _harbor.ZoomLevel ?? Math.Clamp(Math.Log2(156543.03392 * Math.Cos(_harbor.Latitude * Math.PI / 180) *
            390 / (_harbor.RadiusNm * 1852 * 2.2)), 3, 16);
    }

    private void DrawCoverage(Harbor? area = null)
    {
        _coverage.MapElements.Clear();
        foreach (GeoBox box in (area ?? _harbor).Boxes)
        {
            _coverage.MapElements.Add(new MapPolyline
            {
                Path = new Geopath(new BasicGeoposition[] {
                    Position(box.South, box.West), Position(box.North, box.West),
                    Position(box.North, box.East), Position(box.South, box.East), Position(box.South, box.West)}),
                StrokeColor = _visuals.Color("MapVesselColor"),
                StrokeThickness = 1.5,
                StrokeDashed = true,
                IsEnabled = false,
            });
        }
    }

    private async void RevealMapAsync()
    {
        if (_mapRevealed)
        {
            return;
        }
        _mapRevealed = true;
        MapLoadingRing.IsActive = true;
        await Task.Delay(1500);
        if (!_disposed)
        {
            MapCover.Visibility = Visibility.Collapsed;
            MapLoadingRing.IsActive = false;
        }
    }

    private void SelectVessel(int mmsi)
    {
        _selected = mmsi;
        Render();
    }

    private void UpdateDetails()
    {
        Vessel? vessel = _selected is { } id ? _store.Find(id) : null;
        bool detail = vessel is not null;
        DetailPanel.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        ListHeader.Visibility = TrafficListPanel.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        if (vessel is null)
        {
            _selected = null;
            return;
        }
        VesselName.Text = vessel.DisplayName;
        VesselType.Text = $"{vessel.TypeText} · {vessel.StatusText}";
        VesselMotion.Text = $"{vessel.SpeedText} · Course {(vessel.Course is { } course ? $"{course:0}°" : "unknown")}";
        VesselPosition.Text = string.Create(CultureInfo.CurrentCulture,
            $"{vessel.Latitude:F5}, {vessel.Longitude:F5} · {_harbor.DistanceNm(vessel.Latitude, vessel.Longitude):0.0} nm from center");
        VesselMeta.Text = $"MMSI {vessel.Mmsi:D9} · {vessel.SeenText(DateTimeOffset.UtcNow)}\nSource: {vessel.SourceText}";
        VesselDestination.Text = vessel.Destination is { } destination ? $"Reported destination: {destination}" : "Destination not reported";
    }

    public bool TryGoBack()
    {
        if (PlacesFlyout.IsOpen)
        {
            PlacesFlyout.Hide();
            return true;
        }
        if (_pickingArea)
        {
            CancelPickingArea();
            return true;
        }
        if (_selected is null)
        {
            return false;
        }
        _selected = null;
        Render();
        return true;
    }

    private void Ship_Tapped(object? sender, MapElementTappedEventArgs e)
    {
        if (!_pickingArea && e.MapElement is MapIcon icon && _identities.TryGetValue(icon, out int id))
        {
            SelectVessel(id);
            e.Handled = true;
        }
    }
    private void Vessel_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VesselRow row)
        {
            SelectVessel(row.Vessel.Mmsi);
            Map.Center = Point(row.Vessel.Latitude, row.Vessel.Longitude);
        }
    }
    private void Vessel_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is VesselRow row)
        {
            LabelContainer(args.ItemContainer, row);
        }
    }
    private static void LabelContainer(DependencyObject container, VesselRow row)
    {
        AutomationProperties.SetName(container, row.AccessibleName);
        AutomationProperties.SetAutomationId(container, $"Vessel-{row.Vessel.Mmsi}");
    }
    private void Theme_Changed(FrameworkElement sender, object args)
    {
        _visuals.Clear();
        if (_pickingArea)
        {
            UpdateAreaPreview();
            return;
        }
        DrawCoverage();
        Render();
    }
    private void Home_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_pickingArea)
            {
                EndPickingArea();
                _renderTimer.Start();
            }
            Harbor home = SettingsService.Areas.Home;
            if (home != _harbor)
            {
                ChangeHarbor(home);
            }
            else
            {
                CenterOnHarbor();
                _connection.Start();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowAreaError(ex);
        }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
    private void Moving_Click(object sender, RoutedEventArgs e) => Render();
    private void CloseDetail_Click(object sender, RoutedEventArgs e) => TryGoBack();
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _lastSnapshot = DateTimeOffset.MinValue;
        _generation++;
        _pending.Clear();
        _connection.Restart();
    }
    private void BeginPickArea_Click(object sender, RoutedEventArgs e)
    {
        PlacesFlyout.Hide();
        _pickingArea = true;
        _connected = false;
        _connection.Stop();
        _renderTimer.Stop();
        _generation++;
        _pending.Clear();
        Map.Layers.Remove(_ships);
        _selected = null;
        DetailPanel.Visibility = ListHeader.Visibility = TrafficListPanel.Visibility = Visibility.Collapsed;
        PickAreaPanel.Visibility = AreaCrosshair.Visibility = Visibility.Visible;
        ChangePlaceButton.IsEnabled = RefreshButton.IsEnabled = false;
        StatusBar.IsOpen = false;
        ConnectionText.Text = "Choose an area on the map";
        App.Current.SetTrayStatus(null);
        string name = "My home area";
        for (int suffix = 2; SettingsService.Areas.Areas.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            name = $"My home area {suffix}";
        }
        PickedAreaNameBox.Text = name;
        MakeHomeCheck.IsChecked = true;
        UpdateAreaPreview();
        PickedAreaNameBox.Focus(FocusState.Programmatic);
        PickedAreaNameBox.SelectAll();
    }

    private void Map_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_pickingArea && Map.TryGetLocationFromOffset(e.GetPosition(Map), out Geopoint location))
        {
            Map.Center = location;
            e.Handled = true;
        }
    }

    private void MapView_Changed(DependencyObject sender, DependencyProperty property)
    {
        if (_pickingArea)
        {
            UpdateAreaPreview();
        }
    }

    private void UpdateAreaPreview()
    {
        BasicGeoposition? center = Map.Center?.Position;
        bool valid = center is { } point && Math.Abs(point.Latitude) <= 80 && Map.ActualWidth > 0;
        ConfirmAreaButton.IsEnabled = valid;
        if (!valid)
        {
            AreaPreviewText.Text = "Choose a center between 80 degrees south and north.";
            _coverage.MapElements.Clear();
            return;
        }
        Harbor preview = PickedArea("Preview");
        AreaPreviewText.Text = $"{preview.Latitude:F4}, {preview.Longitude:F4} · {preview.RadiusNm:0.0} nm half-width";
        DrawCoverage(preview);
    }

    private Harbor PickedArea(string name)
    {
        if (Map.Center is not { } mapCenter)
        {
            throw new ArgumentException("Wait for the map to finish opening.");
        }
        BasicGeoposition center = mapCenter.Position;
        return Harbor.FromMapView(name, center.Latitude, center.Longitude, Map.ZoomLevel, Map.ActualWidth);
    }

    private void SavePickedArea_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Harbor area = PickedArea(PickedAreaNameBox.Text);
            SettingsService.Areas = SettingsService.Areas.Save(area, makeHome: MakeHomeCheck.IsChecked == true);
            EndPickingArea();
            ChangeHarbor(area);
            _renderTimer.Start();
            ChangePlaceButton.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowAreaError(ex);
        }
    }

    private void EndPickingArea()
    {
        _pickingArea = false;
        PickAreaPanel.Visibility = AreaCrosshair.Visibility = Visibility.Collapsed;
        Map.Layers.Add(_ships);
        ChangePlaceButton.IsEnabled = RefreshButton.IsEnabled = true;
        DrawCoverage();
        CenterOnHarbor();
        Render();
    }

    private void CancelPickingArea()
    {
        EndPickingArea();
        StatusBar.IsOpen = false;
        _renderTimer.Start();
        _connection.Start();
        ChangePlaceButton.Focus(FocusState.Programmatic);
    }

    private void CancelPickArea_Click(object sender, RoutedEventArgs e) => CancelPickingArea();

    private void ShowAreaError(Exception error)
    {
        System.Diagnostics.Debug.WriteLine($"Could not save or select area: {error.Message}");
        StatusBar.Title = "Area not saved";
        StatusBar.Message = error is ArgumentException ? error.Message :
            "Could not save your areas. Check available disk space and app-data permissions, then try again.";
        StatusBar.IsOpen = true;
    }
    private void Credits_Click(object sender, RoutedEventArgs e)
    {
        AttributionText.Text = "Map: © OpenStreetMap contributors (openstreetmap.org/copyright)\n\n" +
            "Open Waters AIS (https://openwaters.io/ais/)\n\n" +
            string.Join("\n\n", _attribution.Values.Distinct().Order());
    }
    private static BasicGeoposition Position(double lat, double lon) => new() { Latitude = lat, Longitude = lon };
    private static Geopoint Point(double lat, double lon) => new(Position(lat, lon));
}
