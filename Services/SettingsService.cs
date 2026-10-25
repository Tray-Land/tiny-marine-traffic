using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Windows.Storage;
using System.Diagnostics;
using TinyMarineTraffic.Core;

namespace TinyMarineTraffic.Services;

/// <summary>
/// Typed wrapper over <see cref="ApplicationData.LocalSettings"/>. Every read falls back to a
/// default so a missing or corrupt value never crashes the app. Not for secrets: see
/// <see cref="Secrets"/> (build-time app keys) and <see cref="CredentialStore"/> (user tokens).
/// </summary>
/// <remarks>
/// LocalSettings values must be small (8 KB each) WinRT primitives. Store enums as int, and lists
/// or records as JSON through a source-generated <c>JsonSerializerContext</c> (trim-safe) with
/// <see cref="GetJson"/> / <see cref="SetJson"/>. Anything larger belongs in a file under
/// <c>ApplicationData.Current.LocalFolder</c>.
/// </remarks>
internal static class SettingsService
{
    private static AreaCatalog? _areas;
    private static string AreasPath => Path.Combine(ApplicationData.Current.LocalFolder.Path, "areas.json");

    public static AreaCatalog Areas
    {
        get => _areas ??= LoadAreas();
        set
        {
            if (!value.IsValid)
            {
                throw new ArgumentException("The saved area list is invalid.", nameof(value));
            }
            string path = AreasPath;
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, AisJsonContext.Default.AreaCatalog));
            File.Move(temporary, path, overwrite: true);
            _areas = value;
        }
    }

    public static void BeginSession() => _areas = Areas.Select(Areas.HomeName);

    public static Harbor CurrentHarbor
    {
        get => Areas.Selected;
        set
        {
            if (Areas.Selected != value)
            {
                Areas = Areas.Select(value.Name);
            }
        }
    }

    private static AreaCatalog LoadAreas()
    {
        try
        {
            if (File.Exists(AreasPath))
            {
                AreaCatalog? saved = JsonSerializer.Deserialize(File.ReadAllText(AreasPath), AisJsonContext.Default.AreaCatalog);
                if (saved is { IsValid: true })
                {
                    return saved;
                }
                Debug.WriteLine("The saved area list is invalid. Restoring the default areas.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Could not read saved areas: {ex.Message}");
        }
        AreaCatalog defaults = AreaCatalog.CreateDefault();
        if (GetJson("Harbor", AisJsonContext.Default.Harbor) is { IsValid: true } legacy)
        {
            string? original = defaults.Areas.FirstOrDefault(a => a.Name == legacy.Name)?.Name;
            return defaults.Save(legacy, original, makeHome: true);
        }
        return defaults;
    }
    /// <summary>False until the first launch has shown the flyout.</summary>
    public static bool HasLaunchedBefore
    {
        get => Get(nameof(HasLaunchedBefore), false);
        set => Set(nameof(HasLaunchedBefore), value);
    }

    public static T? GetJson<T>(string key, JsonTypeInfo<T> typeInfo)
    {
        if (Get<string?>(key, null) is not { } json)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Could not read setting '{key}': {ex.Message}");
            return default;
        }
    }

    public static void SetJson<T>(string key, T? value, JsonTypeInfo<T> typeInfo) =>
        Set(key, value is null ? null : JsonSerializer.Serialize(value, typeInfo));

    private static T Get<T>(string key, T defaultValue)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) && value is T typed
                ? typed
                : defaultValue;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not read setting '{key}': {ex.Message}");
            return defaultValue;
        }
    }

    private static void Set(string key, object? value)
    {
        try
        {
            if (value is null)
            {
                ApplicationData.Current.LocalSettings.Values.Remove(key);
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not save setting '{key}': {ex.Message}");
        }
    }
}
