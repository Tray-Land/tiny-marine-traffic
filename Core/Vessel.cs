using System.Globalization;

namespace TinyMarineTraffic.Core;

public sealed record Vessel(
    int Mmsi, double Latitude, double Longitude, DateTimeOffset Seen,
    string? Name = null, double? Speed = null, double? Course = null, double? Heading = null,
    int? ShipType = null, int? NavigationStatus = null, string? Destination = null, string? Source = null)
{
    public string DisplayName => Name ?? $"MMSI {Mmsi:D9}";
    public bool IsMoving => Speed is >= 1;
    public string SpeedText => Speed is { } speed ? $"{speed.ToString("0.0", CultureInfo.CurrentCulture)} kn" : "Speed unknown";
    public string TypeText => ShipType switch
    {
        >= 20 and <= 29 => "Wing in ground",
        30 => "Fishing",
        31 or 32 => "Towing",
        33 => "Dredging",
        34 => "Diving",
        35 => "Military",
        36 => "Sailing",
        37 => "Pleasure craft",
        >= 40 and <= 49 => "High-speed craft",
        50 => "Pilot vessel",
        51 => "Search and rescue",
        52 => "Tug",
        53 => "Port tender",
        54 => "Anti-pollution",
        55 => "Law enforcement",
        58 => "Medical",
        >= 60 and <= 69 => "Passenger",
        >= 70 and <= 79 => "Cargo",
        >= 80 and <= 89 => "Tanker",
        >= 90 and <= 99 => "Other vessel",
        _ => "Vessel",
    };
    public string StatusText => NavigationStatus switch
    {
        0 => "Under way using engine",
        1 => "At anchor",
        2 => "Not under command",
        3 => "Restricted maneuverability",
        4 => "Constrained by draught",
        5 => "Moored",
        6 => "Aground",
        7 => "Fishing",
        8 => "Under way sailing",
        11 => "Towing astern",
        12 => "Pushing ahead",
        14 => "AIS-SART active",
        _ => IsMoving ? "Moving" : Speed is null ? "Status unknown" : "Below 1 kn",
    };
    public string Summary => $"{TypeText} · {StatusText}";
    public string SourceText => Source switch
    {
        "kystverket" => "Norwegian Coastal Administration",
        "barentswatch" => "BarentsWatch",
        "digitraffic" => "Fintraffic / Digitraffic",
        "aishub" => "AISHub",
        "aisstream" => "aisstream.io",
        _ => "Open Waters receiver",
    };
    public string SeenText(DateTimeOffset now)
    {
        TimeSpan age = now - Seen;
        return age.TotalSeconds < 60 ? "Just now" : age.TotalHours < 1
            ? $"{Math.Max(1, (int)age.TotalMinutes)} min ago"
            : $"{(int)age.TotalHours} h ago";
    }
}

public sealed record VesselUpdate(Vessel Vessel, bool HasSpeed, bool HasCourse, bool HasHeading, bool HasStatus,
    string? Attribution = null);
public sealed record TrafficSnapshot(IReadOnlyList<Vessel> Vessels, IReadOnlyDictionary<string, string> Attribution, bool Truncated);

public sealed class VesselStore
{
    private readonly Dictionary<int, Vessel> _vessels = [];
    public IEnumerable<Vessel> Vessels => _vessels.Values;
    public Vessel? Find(int mmsi) => _vessels.GetValueOrDefault(mmsi);
    public void Clear() => _vessels.Clear();
    public void Replace(TrafficSnapshot snapshot)
    {
        Clear();
        foreach (Vessel vessel in snapshot.Vessels)
        {
            _vessels[vessel.Mmsi] = vessel;
        }
    }
    public void Apply(VesselUpdate update)
    {
        Vessel incoming = update.Vessel;
        if (!_vessels.TryGetValue(incoming.Mmsi, out Vessel? previous))
        {
            // Bound memory even if the server sends traffic outside the requested box.
            if (_vessels.Count < 3000)
            {
                _vessels[incoming.Mmsi] = incoming;
            }
            return;
        }
        bool newer = incoming.Seen >= previous.Seen;
        _vessels[incoming.Mmsi] = (newer ? incoming : previous) with
        {
            Name = newer ? incoming.Name ?? previous.Name : previous.Name ?? incoming.Name,
            ShipType = newer ? incoming.ShipType ?? previous.ShipType : previous.ShipType ?? incoming.ShipType,
            Destination = newer ? incoming.Destination ?? previous.Destination : previous.Destination ?? incoming.Destination,
            Speed = newer && update.HasSpeed ? incoming.Speed : previous.Speed,
            Course = newer && update.HasCourse ? incoming.Course : previous.Course,
            Heading = newer && update.HasHeading ? incoming.Heading : previous.Heading,
            NavigationStatus = newer && update.HasStatus ? incoming.NavigationStatus : previous.NavigationStatus,
        };
    }
    public void Prune(Harbor harbor, DateTimeOffset now)
    {
        foreach (int mmsi in _vessels.Where(p => now - p.Value.Seen > TimeSpan.FromMinutes(30) ||
            !harbor.Contains(p.Value.Latitude, p.Value.Longitude)).Select(p => p.Key).ToArray())
        {
            _vessels.Remove(mmsi);
        }
    }
}
