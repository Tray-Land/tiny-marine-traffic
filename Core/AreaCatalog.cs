using System.Text.Json.Serialization;

namespace TinyMarineTraffic.Core;

public sealed record AreaCatalog(Harbor[] Areas, string HomeName, string SelectedName)
{
    public const int MaximumAreas = 30;

    public static AreaCatalog CreateDefault() =>
        new(Harbor.Presets.ToArray(), Harbor.Presets[0].Name, Harbor.Presets[0].Name);

    [JsonIgnore]
    public bool IsValid => Areas is { Length: > 0 and <= MaximumAreas } &&
        Areas.All(a => a is { IsValid: true }) &&
        Areas.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Areas.Length &&
        Areas.Any(a => a.Name == HomeName) && Areas.Any(a => a.Name == SelectedName);

    [JsonIgnore]
    public Harbor Home => Areas.First(a => a.Name == HomeName);

    [JsonIgnore]
    public Harbor Selected => Areas.First(a => a.Name == SelectedName);

    public AreaCatalog Save(Harbor area, string? originalName = null, bool makeHome = false)
    {
        area = area with { Name = area.Name.Trim() };
        if (!area.IsValid)
        {
            throw new ArgumentException("Enter a name up to 40 characters, valid coordinates, and a half-width of 2 to 50 nautical miles.");
        }
        int index = originalName is null ? -1 : Array.FindIndex(Areas, a => a.Name == originalName);
        if (originalName is not null && index < 0)
        {
            throw new ArgumentException("This saved area no longer exists. Select another area.");
        }
        if (Areas.Where((_, i) => i != index).Any(a => string.Equals(a.Name, area.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("An area with this name already exists. Choose a different name.");
        }
        if (index < 0 && Areas.Length >= MaximumAreas)
        {
            throw new ArgumentException($"You can save up to {MaximumAreas} areas. Delete an area before adding another.");
        }
        Harbor[] areas = index < 0 ? [.. Areas, area] : (Harbor[])Areas.Clone();
        if (index >= 0)
        {
            areas[index] = area;
        }
        string home = makeHome || HomeName == originalName ? area.Name : HomeName;
        return new(areas, home, area.Name);
    }

    public AreaCatalog Select(string name)
    {
        RequireArea(name);
        return this with { SelectedName = name };
    }

    public AreaCatalog MakeHome(string name)
    {
        RequireArea(name);
        return this with { HomeName = name };
    }

    public AreaCatalog Delete(string name)
    {
        RequireArea(name);
        if (Areas.Length == 1)
        {
            throw new ArgumentException("Keep at least one saved area.");
        }
        Harbor[] areas = Areas.Where(a => a.Name != name).ToArray();
        string home = HomeName == name ? areas[0].Name : HomeName;
        return new(areas, home, SelectedName == name ? home : SelectedName);
    }

    private void RequireArea(string name)
    {
        if (!Areas.Any(a => a.Name == name))
        {
            throw new ArgumentException("This saved area no longer exists. Select another area.");
        }
    }
}
