using System.Text.Json;
using TinyMarineTraffic.Core;

namespace TinyMarineTraffic.Tests;

[TestClass]
public sealed class AreaTests
{
    [TestMethod]
    public void DefaultsIncludePresetsAndOslofjordHome()
    {
        AreaCatalog catalog = AreaCatalog.CreateDefault();
        Assert.IsTrue(catalog.IsValid);
        Assert.AreEqual("Oslofjord", catalog.HomeName);
        Assert.AreEqual(catalog.Home, catalog.Selected);
        CollectionAssert.AreEqual(Harbor.Presets.ToArray(), catalog.Areas);
    }

    [TestMethod]
    public void SavingMapAreaAddsSelectsAndOptionallyMakesHomeWithoutMutatingOriginal()
    {
        AreaCatalog original = AreaCatalog.CreateDefault();
        Harbor area = Harbor.FromMapView("  My harbor  ", 59.8, 10.6, 10, 420);
        AreaCatalog saved = original.Save(area, makeHome: true);
        Assert.IsTrue(saved.IsValid);
        Assert.AreEqual("My harbor", saved.HomeName);
        Assert.AreEqual(area, saved.Home);
        Assert.AreEqual(saved.Home, saved.Selected);
        Assert.HasCount(Harbor.Presets.Count, original.Areas);
        Assert.AreEqual("Oslofjord", original.HomeName);
        Assert.AreEqual(10d, saved.Home.ZoomLevel);
    }

    [TestMethod]
    public void RenamingHomeUpdatesReferencesAndPreservesDropdownOrder()
    {
        AreaCatalog original = AreaCatalog.CreateDefault();
        Harbor renamed = original.Home with { Name = "My fjord", RadiusNm = 8 };
        AreaCatalog saved = original.Save(renamed, original.HomeName);
        Assert.AreEqual("My fjord", saved.HomeName);
        Assert.AreEqual("My fjord", saved.SelectedName);
        Assert.AreEqual(renamed, saved.Areas[0]);
        Assert.AreEqual("Oslofjord", original.Areas[0].Name);
    }

    [TestMethod]
    public void DuplicateBlankAndInvalidAreasAreRejected()
    {
        AreaCatalog catalog = AreaCatalog.CreateDefault();
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(new("osloFJORD", 0, 0, 10)));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(new("   ", 0, 0, 10)));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(new(new string('a', 41), 0, 0, 10)));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(new("Invalid", 81, 0, 10)));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(new("Invalid", 0, 0, 1)));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(catalog.Home, "Missing"));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Select("Missing"));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.MakeHome("Missing"));
    }

    [TestMethod]
    public void SettingHomeDoesNotChangeSelectedArea()
    {
        AreaCatalog catalog = AreaCatalog.CreateDefault().Select("Bergen").MakeHome("Rotterdam");
        Assert.AreEqual("Bergen", catalog.SelectedName);
        Assert.AreEqual("Rotterdam", catalog.HomeName);
        Assert.IsTrue(catalog.IsValid);
    }

    [TestMethod]
    public void DeletingHomeUsesRemainingAreaAndCannotDeleteLastArea()
    {
        AreaCatalog catalog = AreaCatalog.CreateDefault().Delete("Oslofjord");
        Assert.AreEqual("Bergen", catalog.HomeName);
        Assert.AreEqual("Bergen", catalog.SelectedName);
        Assert.IsTrue(catalog.IsValid);
        catalog = catalog.Select("Helsinki").Delete("Bergen");
        Assert.AreEqual("Helsinki", catalog.SelectedName);
        foreach (string name in catalog.Areas.Skip(1).Select(a => a.Name).ToArray())
        {
            catalog = catalog.Delete(name);
        }
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Delete(catalog.HomeName));
    }

    [TestMethod]
    public void AreaLimitAllowsEditsButRejectsFurtherAdditions()
    {
        AreaCatalog catalog = AreaCatalog.CreateDefault();
        while (catalog.Areas.Length < AreaCatalog.MaximumAreas)
        {
            catalog = catalog.Save(new($"Area {catalog.Areas.Length}", 0, 0, 10));
        }
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Save(new("One too many", 0, 0, 10)));
        Assert.IsTrue(catalog.Save(catalog.Home with { Name = "Renamed" }, catalog.HomeName).IsValid);
    }

    [TestMethod]
    public void AreaCatalogJsonRoundTripsZoomAndRejectsBrokenReferences()
    {
        AreaCatalog catalog = AreaCatalog.CreateDefault().Save(new("My area", 59, 10, 5, 11.2), makeHome: true);
        string json = JsonSerializer.Serialize(catalog, AisJsonContext.Default.AreaCatalog);
        AreaCatalog saved = JsonSerializer.Deserialize(json, AisJsonContext.Default.AreaCatalog)!;
        Assert.IsTrue(saved.IsValid);
        Assert.AreEqual(catalog.Home, saved.Home);
        Assert.DoesNotContain("\"Boxes\"", json);
        Assert.DoesNotContain("\"IsValid\"", json);
        Assert.IsFalse((saved with { HomeName = "Missing" }).IsValid);
        Assert.IsFalse((saved with { SelectedName = "Missing" }).IsValid);
        Assert.IsFalse((saved with { Areas = [saved.Home, saved.Home] }).IsValid);
        Harbor legacy = JsonSerializer.Deserialize(
            """{"Name":"Legacy","Latitude":59,"Longitude":10,"RadiusNm":12}""", AisJsonContext.Default.Harbor)!;
        Assert.IsTrue(legacy.IsValid);
        Assert.IsNull(legacy.ZoomLevel);
    }

    [TestMethod]
    public void MapViewUsesCenterAndZoomAndLimitsAnonymousCoverage()
    {
        Harbor area = Harbor.FromMapView("Map", 59.81234567, 10.62345678, 10, 420);
        Harbor closer = Harbor.FromMapView("Map", 59.81234567, 10.62345678, 11, 420);
        Assert.AreEqual(59.812346, area.Latitude);
        Assert.AreEqual(10.623457, area.Longitude);
        Assert.AreEqual(area.RadiusNm / 2, closer.RadiusNm, 0.1);
        Assert.AreEqual(2d, Harbor.FromMapView("Close", 59, 10, 20, 420).RadiusNm);
        Harbor wide = Harbor.FromMapView("Wide", 0, 0, 3, 420);
        Assert.AreEqual(50d, wide.RadiusNm);
        Assert.IsNull(wide.ZoomLevel);
        Assert.IsTrue(wide.Boxes.Sum(b => b.Area) < 100);
        Assert.ThrowsExactly<ArgumentException>(() => Harbor.FromMapView("", 59, 10, 10, 420));
        Assert.ThrowsExactly<ArgumentException>(() => Harbor.FromMapView("Polar", 85, 10, 10, 420));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Harbor.FromMapView("Not ready", 59, 10, 10, 0));
    }
}
