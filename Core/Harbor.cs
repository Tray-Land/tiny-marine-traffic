using System.Globalization;
using System.Text.Json.Serialization;

namespace TinyMarineTraffic.Core;

public sealed record Harbor(string Name, double Latitude, double Longitude, double RadiusNm, double? ZoomLevel = null)
{
    public static IReadOnlyList<Harbor> Presets { get; } =
    new Harbor[]
    {
        new("Oslofjord", 59.82, 10.65, 12),
        new("Bergen", 60.39, 5.25, 12),
        new("Helsinki", 60.12, 24.95, 12),
        new("Rotterdam", 51.96, 4.08, 15),
        new("English Channel", 50.98, 1.40, 25),
        new("Puget Sound", 47.72, -122.45, 18),
        new("San Francisco Bay", 37.80, -122.39, 12),
        new("New York Harbor", 40.60, -74.03, 12),
    };

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name.Length <= 40 &&
        double.IsFinite(Latitude) && Math.Abs(Latitude) <= 80 &&
        double.IsFinite(Longitude) && Math.Abs(Longitude) <= 180 &&
        double.IsFinite(RadiusNm) && RadiusNm is >= 2 and <= 50 &&
        (ZoomLevel is null || double.IsFinite(ZoomLevel.Value) && ZoomLevel is >= 0 and <= 22);

    [JsonIgnore]
    public IReadOnlyList<GeoBox> Boxes
    {
        get
        {
            if (!IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(Harbor), "Use latitude -80 to 80, longitude -180 to 180, and a range of 2 to 50 nautical miles.");
            }
            double lat = RadiusNm / 60;
            double lon = lat / Math.Cos(Latitude * Math.PI / 180);
            double west = Longitude - lon;
            double east = Longitude + lon;
            double south = Latitude - lat;
            double north = Latitude + lat;
            if (west < -180)
            {
                return new GeoBox[] { new(south, west + 360, north, 180), new(south, -180, north, east) };
            }
            if (east > 180)
            {
                return new GeoBox[] { new(south, west, north, 180), new(south, -180, north, east - 360) };
            }
            return new GeoBox[] { new(south, west, north, east) };
        }
    }

    public bool Contains(double latitude, double longitude) => Boxes.Any(b => b.Contains(latitude, longitude));

    public static Harbor FromMapView(string name, double latitude, double longitude, double zoom, double width)
    {
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(zoom) || zoom is < 0 or > 22)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Wait for the map to finish opening.");
        }
        double radius = 156543.03392 * Math.Cos(latitude * Math.PI / 180) * width / Math.Pow(2, zoom) / 3704;
        Harbor harbor = new(name.Trim(), Math.Round(latitude, 6), Math.Round(longitude, 6),
            Math.Clamp(Math.Round(radius, 1), 2, 50), radius > 50 ? null : zoom);
        if (!harbor.IsValid)
        {
            throw new ArgumentException("Enter a name up to 40 characters and choose a center between 80 degrees south and north.");
        }
        return harbor;
    }

    public double DistanceNm(double latitude, double longitude)
    {
        double lat1 = Latitude * Math.PI / 180;
        double lat2 = latitude * Math.PI / 180;
        double dlat = lat2 - lat1;
        double dlon = (longitude - Longitude) * Math.PI / 180;
        double a = Math.Pow(Math.Sin(dlat / 2), 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dlon / 2), 2);
        return 3440.065 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}

public sealed record GeoBox(double South, double West, double North, double East)
{
    public double Area => (North - South) * (East - West);
    public bool Contains(double latitude, double longitude) =>
        latitude >= South && latitude <= North && longitude >= West && longitude <= East;
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{South:F6},{West:F6},{North:F6},{East:F6}");
}
