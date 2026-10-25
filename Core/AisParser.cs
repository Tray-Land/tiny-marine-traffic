using System.Text.Json;

namespace TinyMarineTraffic.Core;

public static class AisParser
{
    public static TrafficSnapshot ParseSnapshot(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (Text(root, "type") != "FeatureCollection" ||
            !root.TryGetProperty("features", out JsonElement features) || features.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Open Waters returned an invalid vessel snapshot.");
        }
        List<Vessel> vessels = [];
        foreach (JsonElement feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out JsonElement p) ||
                !feature.TryGetProperty("geometry", out JsonElement geometry) ||
                Text(geometry, "type") != "Point" ||
                !geometry.TryGetProperty("coordinates", out JsonElement coordinates) ||
                coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() < 2 ||
                Text(p, "kind") is { } kind && kind != "vessel")
            {
                continue;
            }
            double? lon = Number(coordinates[0]);
            double? lat = Number(coordinates[1]);
            int? mmsi = Integer(p, "mmsi");
            if (!ValidIdentity(mmsi, lat, lon) || !TryTime(p, "seen", out DateTimeOffset seen))
            {
                continue;
            }
            vessels.Add(new Vessel(mmsi!.Value, lat!.Value, lon!.Value, seen,
                CleanName(Text(p, "name")), Speed(Number(p, "sog")), Bearing(Number(p, "cog")),
                Bearing(Number(p, "heading")), Integer(p, "type"), Status(Integer(p, "nav_status")),
                CleanName(Text(p, "destination")), Text(p, "source")));
        }
        Dictionary<string, string> attribution = [];
        if (root.TryGetProperty("attribution", out JsonElement a) && a.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in a.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    attribution[property.Name] = property.Value.GetString()!;
                }
            }
        }
        return new(vessels, attribution, root.TryGetProperty("truncated", out JsonElement t) && t.ValueKind == JsonValueKind.True);
    }

    public static VesselUpdate? ParseEvent(JsonElement root)
    {
        if (Text(root, "type") != "event" ||
            !root.TryGetProperty("message", out JsonElement message) ||
            message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string? messageType = Text(root, "msg_type");
        if (messageType is not ("PositionReport" or "StandardClassBPositionReport" or "ExtendedClassBPositionReport" or
            "LongRangeAisBroadcastMessage" or "ShipStaticData" or "StaticDataReport"))
        {
            return null;
        }
        if (message.TryGetProperty("Valid", out JsonElement valid) && valid.ValueKind == JsonValueKind.False)
        {
            return null;
        }
        int? mmsi = Integer(root, "mmsi");
        double? lat = Number(root, "lat");
        double? lon = Number(root, "lon");
        if (!ValidIdentity(mmsi, lat, lon) || !TryTime(root, "time", out DateTimeOffset seen))
        {
            return null;
        }
        string? name = Text(message, "Name");
        int? shipType = Integer(message, "Type");
        if (message.TryGetProperty("ReportA", out JsonElement reportA))
        {
            name ??= Text(reportA, "Name");
        }
        if (message.TryGetProperty("ReportB", out JsonElement reportB))
        {
            shipType ??= Integer(reportB, "ShipType");
        }
        return new(new Vessel(mmsi!.Value, lat!.Value, lon!.Value, seen, CleanName(name),
            Speed(Number(message, "Sog")), Bearing(Number(message, "Cog")),
            Bearing(Number(message, "TrueHeading")), shipType, Status(Integer(message, "NavigationalStatus")),
            CleanName(Text(message, "Destination")), Text(root, "source")),
            message.TryGetProperty("Sog", out _), message.TryGetProperty("Cog", out _),
            message.TryGetProperty("TrueHeading", out _), message.TryGetProperty("NavigationalStatus", out _),
            Text(root, "attribution"));
    }

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Integer(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) ? Number(value) : null;
    private static double? Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
    private static bool TryTime(JsonElement element, string name, out DateTimeOffset time) =>
        DateTimeOffset.TryParse(Text(element, name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out time);
    private static bool ValidIdentity(int? mmsi, double? lat, double? lon) =>
        mmsi is >= 100000000 and <= 999999999 && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    private static double? Speed(double? speed) => speed is >= 0 and < 102.3 ? speed : null;
    private static double? Bearing(double? bearing) => bearing is >= 0 and < 360 ? bearing : null;
    private static int? Status(int? status) => status is >= 0 and < 15 ? status : null;
    private static string? CleanName(string? name)
    {
        string? clean = name?.Trim(' ', '@', '\0');
        return string.IsNullOrWhiteSpace(clean) || clean is "N/A" or "UNKNOWN" ? null : clean;
    }
}
