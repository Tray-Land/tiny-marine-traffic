using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyMarineTraffic.Core;
using TinyMarineTraffic.Services;
using Windows.ApplicationModel;

namespace TinyMarineTraffic.Views;

public sealed partial class SettingsPage : Page
{
    private readonly Action _goBack;
    private AreaCatalog _catalog = SettingsService.Areas;
    private Harbor? _editingArea;
    private bool _loading = true;

    public bool IsDialogOpen { get; private set; }

    public SettingsPage(Action goBack)
    {
        _goBack = goBack;
        InitializeComponent();
        PackageVersion v = Package.Current.Id.Version;
        VersionText.Text = $"{App.DisplayName} {v.Major}.{v.Minor}.{v.Build}";
    }

    public async void OnShown()
    {
        _catalog = SettingsService.Areas;
        LoadAreas(_catalog.SelectedName);
        ShowStartupState(await StartupService.GetStateAsync());
        BackButton.Focus(FocusState.Programmatic);
    }

    private void LoadAreas(string selectedName)
    {
        _loading = true;
        SavedAreaPicker.ItemsSource = _catalog.Areas.Select(a => a.Name).ToArray();
        SavedAreaPicker.SelectedIndex = Array.FindIndex(_catalog.Areas, a => a.Name == selectedName);
        HomeAreaText.Text = $"Home: {_catalog.HomeName}";
        AddAreaButton.IsEnabled = _catalog.Areas.Length < AreaCatalog.MaximumAreas;
        _loading = false;
        LoadEditor(_catalog.Areas[SavedAreaPicker.SelectedIndex]);
    }

    private void LoadEditor(Harbor? area)
    {
        _editingArea = area;
        Harbor values = area ?? _catalog.Selected;
        AreaNameBox.Text = area?.Name ?? "";
        LatitudeBox.Value = values.Latitude;
        LongitudeBox.Value = values.Longitude;
        RadiusBox.Value = values.RadiusNm;
        DeleteAreaButton.IsEnabled = area is not null && _catalog.Areas.Length > 1;
        MakeHomeButton.IsEnabled = area is not null && area.Name != _catalog.HomeName;
        InputStatus.IsOpen = false;
    }

    private void SavedArea_Changed(object sender, SelectionChangedEventArgs e)
    {
        int index = SavedAreaPicker.SelectedIndex;
        if (!_loading && index >= 0)
        {
            LoadEditor(_catalog.Areas[index]);
        }
    }

    private void AddArea_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        SavedAreaPicker.SelectedIndex = -1;
        _loading = false;
        LoadEditor(null);
        AreaNameBox.Focus(FocusState.Programmatic);
    }

    private void SaveAreaChanges_Click(object sender, RoutedEventArgs e)
    {
        SaveAreaChangesButton.Focus(FocusState.Programmatic);
        Harbor area = new(AreaNameBox.Text, LatitudeBox.Value, LongitudeBox.Value, RadiusBox.Value);
        if (_editingArea is { } original && area.Latitude == original.Latitude &&
            area.Longitude == original.Longitude && area.RadiusNm == original.RadiusNm)
        {
            area = area with { ZoomLevel = original.ZoomLevel };
        }
        try
        {
            Persist(_catalog.Save(area, _editingArea?.Name));
            ShowMessage("Saved area.", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowSaveError(ex);
        }
    }

    private void MakeHome_Click(object sender, RoutedEventArgs e)
    {
        if (_editingArea is not { } area)
        {
            ShowMessage("Save this area before making it home.", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            Persist(_catalog.MakeHome(area.Name), area.Name);
            ShowMessage("Home area updated.", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSaveError(ex);
        }
    }

    private async void DeleteArea_Click(object sender, RoutedEventArgs e)
    {
        if (_editingArea is not { } area || IsDialogOpen)
        {
            return;
        }
        AreaCatalog updated = _catalog.Delete(area.Name);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Delete {area.Name}?",
            Content = area.Name == _catalog.HomeName
                ? $"Remove this area from the dropdown? Your home area will become {updated.HomeName}."
                : "Remove this area from the dropdown?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        IsDialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Persist(updated);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSaveError(ex);
        }
        finally
        {
            IsDialogOpen = false;
        }
    }

    private void Persist(AreaCatalog catalog, string? editorName = null)
    {
        SettingsService.Areas = catalog;
        _catalog = catalog;
        LoadAreas(editorName ?? catalog.SelectedName);
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        InputStatus.Severity = severity;
        InputStatus.Message = message;
        InputStatus.IsOpen = true;
    }

    private void ShowSaveError(Exception error)
    {
        System.Diagnostics.Debug.WriteLine($"Could not save areas: {error.Message}");
        ShowMessage(error is ArgumentException ? error.Message :
            "Could not save your areas. Check available disk space and app-data permissions, then try again.",
            InfoBarSeverity.Warning);
    }

    private void ShowStartupState(StartupTaskState? state)
    {
        _loading = true;
        StartupToggle.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupDescription.Text = state switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup. Turn it on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            null => "Startup settings are not available.",
            _ => "Keep the tray icon ready after you sign in.",
        };
        _loading = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            ShowStartupState(await StartupService.SetEnabledAsync(StartupToggle.IsOn));
        }
    }
    private void Back_Click(object sender, RoutedEventArgs e) => _goBack();
}
