using System.ComponentModel;
using TinyMarineTraffic.Core;

namespace TinyMarineTraffic.Views;

public sealed partial class VesselRow(Vessel vessel) : INotifyPropertyChanged
{
    public Vessel Vessel { get; private set; } = vessel;
    public string Name => Vessel.DisplayName;
    public string Summary => $"{Vessel.TypeText} · {Vessel.SeenText(DateTimeOffset.UtcNow)}";
    public string Speed => Vessel.SpeedText;
    public string AccessibleName => $"{Name}, {Speed}, {Summary}";
    public override string ToString() => AccessibleName;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(Vessel vessel)
    {
        Vessel = vessel;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
