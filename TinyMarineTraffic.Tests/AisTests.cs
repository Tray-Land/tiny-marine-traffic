using System.Globalization;
using System.Text;
using System.Text.Json;
using TinyMarineTraffic.Core;

namespace TinyMarineTraffic.Tests;

[TestClass]
public sealed class AisTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T15:30:00Z", CultureInfo.InvariantCulture);

    [TestMethod]
    public void SnapshotReadsGeoJsonLongitudeFirstAndKeepsCredits()
    {
        TrafficSnapshot snapshot = Snapshot("""
            {"type":"FeatureCollection","attribution":{"kystverket":"NLOD 2.0"},"truncated":true,"features":[
              {"geometry":{"type":"Point","coordinates":[10.7,59.8]},"properties":{
                "mmsi":257090090,"kind":"vessel","name":"FERRY @@@","type":60,
                "sog":5.3,"cog":125.5,"heading":119,"nav_status":0,"seen":"2026-10-25T15:30:00Z"}}
            ]}
            """);
        Vessel vessel = snapshot.Vessels.Single();
        Assert.AreEqual(59.8, vessel.Latitude);
        Assert.AreEqual(10.7, vessel.Longitude);
        Assert.AreEqual("FERRY", vessel.Name);
        Assert.AreEqual("Passenger", vessel.TypeText);
        Assert.IsTrue(vessel.IsMoving);
        Assert.AreEqual("NLOD 2.0", snapshot.Attribution["kystverket"]);
        Assert.IsTrue(snapshot.Truncated);
    }

    [TestMethod]
    public void SnapshotDoesNotInventMissingOrSentinelValues()
    {
        TrafficSnapshot snapshot = Snapshot("""
            {"type":"FeatureCollection","features":[
              {"geometry":{"type":"Point","coordinates":[10,59]},"properties":{
                "mmsi":257090090,"sog":102.3,"cog":360,"heading":511,"nav_status":15,"seen":"2026-10-25T15:30:00Z"}}
            ]}
            """);
        Vessel vessel = snapshot.Vessels.Single();
        Assert.IsNull(vessel.Speed);
        Assert.IsNull(vessel.Course);
        Assert.IsNull(vessel.Heading);
        Assert.IsNull(vessel.NavigationStatus);
        Assert.AreEqual("MMSI 257090090", vessel.DisplayName);
        Assert.AreEqual("Speed unknown", vessel.SpeedText);
    }

    [TestMethod]
    public void SnapshotIgnoresNonVesselsAndInvalidPositions()
    {
        Assert.IsEmpty(Snapshot("""
            {"type":"FeatureCollection","features":[
              {"geometry":null,"properties":{"mmsi":257090090}},
              {"geometry":{"type":"Point","coordinates":[10,91]},"properties":{"mmsi":257090090,"seen":"2026-10-25T15:30:00Z"}},
              {"geometry":{"type":"Point","coordinates":[10,59]},"properties":{"mmsi":257090090,"kind":"gear","seen":"2026-10-25T15:30:00Z"}}
            ]}
            """).Vessels);
    }

    [TestMethod]
    public void MalformedSnapshotFailsRatherThanShowingEmptySuccess()
    {
        Assert.ThrowsExactly<JsonException>(() => Snapshot("""{"error":"down"}"""));
    }

    [TestMethod]
    public void PositionEventReadsMotionAndUnavailableFields()
    {
        VesselUpdate update = Event("""
            {"type":"event","mmsi":257090090,"lat":59.8,"lon":10.7,"time":"2026-10-25T15:30:00Z",
             "msg_type":"PositionReport","source":"kystverket","message":{
               "Valid":true,"Sog":4.2,"Cog":360,"TrueHeading":511,"NavigationalStatus":0}}
            """)!;
        Assert.AreEqual(4.2, update.Vessel.Speed);
        Assert.IsNull(update.Vessel.Course);
        Assert.IsNull(update.Vessel.Heading);
        Assert.IsTrue(update.HasCourse);
        Assert.IsTrue(update.HasSpeed);
    }

    [TestMethod]
    public void StaticClassBReportsReadBothParts()
    {
        Assert.AreEqual("CERULEAN", Event("""
            {"type":"event","mmsi":368168720,"lat":41.6,"lon":-70.6,"time":"2026-10-25T15:30:00Z",
             "msg_type":"StaticDataReport","message":{"ReportA":{"Name":"CERULEAN@@@@@"}}}
            """)!.Vessel.Name);
        Assert.AreEqual(36, Event("""
            {"type":"event","mmsi":368168720,"lat":41.6,"lon":-70.6,"time":"2026-10-25T15:30:00Z",
             "msg_type":"StaticDataReport","message":{"ReportB":{"ShipType":36}}}
            """)!.Vessel.ShipType);
    }

    [TestMethod]
    public void AidsToNavigationAndInvalidReportsAreNotShips()
    {
        Assert.IsNull(Event("""
            {"type":"event","mmsi":257090090,"lat":59.8,"lon":10.7,"time":"2026-10-25T15:30:00Z",
             "msg_type":"AidsToNavigationReport","message":{}}
            """));
        Assert.IsNull(Event("""
            {"type":"event","mmsi":257090090,"lat":59.8,"lon":10.7,"time":"2026-10-25T15:30:00Z",
             "msg_type":"PositionReport","message":{"Valid":false}}
            """));
    }

    [TestMethod]
    public void StaticReplayEnrichesWithoutRewindingPositionOrSpeed()
    {
        VesselStore store = new();
        store.Apply(new(new(257090090, 59.8, 10.7, Now, Speed: 5), true, false, false, false));
        store.Apply(new(new(257090090, 59, 10, Now.AddMinutes(-1), Name: "FERRY", ShipType: 60), false, false, false, false));
        Vessel vessel = store.Find(257090090)!;
        Assert.AreEqual("FERRY", vessel.Name);
        Assert.AreEqual(59.8, vessel.Latitude);
        Assert.AreEqual(5d, vessel.Speed);
        Assert.AreEqual(Now, vessel.Seen);
        store.Apply(new(new(257090090, 59.81, 10.71, Now.AddSeconds(1)), true, false, false, false));
        Assert.IsNull(store.Find(257090090)!.Speed);
        Assert.AreEqual("FERRY", store.Find(257090090)!.Name);
    }

    [TestMethod]
    public void PruneExpiresOldShipsAndShipsOutsideArea()
    {
        VesselStore store = new();
        store.Apply(new(new(257090090, 59.8, 10.7, Now.AddMinutes(-31)), false, false, false, false));
        store.Apply(new(new(257090091, 40, 10, Now), false, false, false, false));
        store.Apply(new(new(257090092, 59.8, 10.7, Now), false, false, false, false));
        store.Prune(Harbor.Presets[0], Now);
        Assert.HasCount(1, store.Vessels.ToArray());
    }

    [TestMethod]
    public void AllHarborsFitAnonymousAreaLimitAndUseInvariantCoordinates()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nb-NO");
            foreach (Harbor harbor in Harbor.Presets.Concat([new("Polar", 80, 179.9, 50)]))
            {
                Assert.IsTrue(harbor.Boxes.Sum(b => b.Area) <= 100);
                Assert.IsTrue(harbor.Contains(harbor.Latitude, harbor.Longitude));
                Assert.HasCount(4, harbor.Boxes[0].ToString().Split(','));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public void DatelineIsSplitAndInvalidSettingsAreRejected()
    {
        Harbor harbor = new("Dateline", 0, 179.9, 25);
        Assert.HasCount(2, harbor.Boxes);
        Assert.IsTrue(harbor.Contains(0, -179.9));
        Assert.IsFalse(new Harbor("Invalid", double.NaN, 0, 10).IsValid);
        Assert.IsFalse(new Harbor("Invalid", 85, 0, 10).IsValid);
        Assert.IsFalse(new Harbor("Invalid", 0, 0, 100).IsValid);
        Assert.AreEqual(60, new Harbor("Center", 0, 0, 10).DistanceNm(1, 0), 0.1);
    }

    [TestMethod]
    public void BackoffIsBoundedAndSubscriptionIsTrimSafe()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), RetryPolicy.NextInterval(TimeSpan.FromSeconds(5), 0));
        Assert.AreEqual(TimeSpan.FromSeconds(10), RetryPolicy.NextInterval(TimeSpan.FromSeconds(5), 1));
        Assert.AreEqual(TimeSpan.FromMinutes(10), RetryPolicy.NextInterval(TimeSpan.FromSeconds(5), 20));
        string json = JsonSerializer.Serialize(new StreamSubscription([[59, 10, 60, 11]]),
            AisJsonContext.Default.StreamSubscription);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("subscribe", document.RootElement.GetProperty("type").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("snapshot").GetBoolean());
    }

    private static TrafficSnapshot Snapshot(string json) => AisParser.ParseSnapshot(Encoding.UTF8.GetBytes(json));
    private static VesselUpdate? Event(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return AisParser.ParseEvent(document.RootElement);
    }
}
