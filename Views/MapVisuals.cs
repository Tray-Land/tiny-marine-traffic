using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TinyMarineTraffic.Core;
using Windows.UI;

namespace TinyMarineTraffic.Views;

internal sealed class MapVisuals
{
    private readonly Dictionary<(int, string), IconElement> _cache = [];

    public void Clear() => _cache.Clear();
    public Color Color(string key) => (Color)Application.Current.Resources[key];

    public IconElement Ship(Vessel vessel, bool selected)
    {
        int bucket = (int)Math.Round((vessel.Heading ?? vessel.Course ?? 0) / 15) % 24;
        string key = selected ? "MapSelectedColor" : vessel.IsMoving ? "MapVesselColor" : "MapStationaryColor";
        if (_cache.TryGetValue((bucket, key), out IconElement? cached))
        {
            return cached;
        }
        Geometry geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry),
            "M12,1 L18,9 L18,21 L6,21 L6,9 Z M9,11 L9,17 L15,17 L15,11 Z");
        geometry.Transform = new RotateTransform { Angle = bucket * 15, CenterX = 12, CenterY = 12 };
        PathIcon icon = new()
        {
            Data = geometry,
            Width = selected ? 28 : 22,
            Height = selected ? 28 : 22,
            Foreground = new SolidColorBrush(Color(key)),
        };
        _cache[(bucket, key)] = icon;
        return icon;
    }
}
