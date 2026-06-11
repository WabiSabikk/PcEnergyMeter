using PcEnergyMeter.Core;

namespace PcEnergyMeter.Tests;

public sealed class RollingExtremesTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Current_ReturnsMinAndMaxWithTimes()
    {
        var extremes = new RollingExtremes(TimeSpan.FromHours(24));
        extremes.Add(Base, 1.50);
        extremes.Add(Base.AddMinutes(1), 1.45);
        extremes.Add(Base.AddMinutes(2), 1.56);

        var snapshot = extremes.Current;

        Assert.NotNull(snapshot);
        Assert.Equal(1.45, snapshot!.Value.Min, precision: 3);
        Assert.Equal(Base.AddMinutes(1), snapshot.Value.MinAt);
        Assert.Equal(1.56, snapshot.Value.Max, precision: 3);
        Assert.Equal(Base.AddMinutes(2), snapshot.Value.MaxAt);
    }

    [Fact]
    public void Add_DropsSamplesOlderThanWindow()
    {
        var extremes = new RollingExtremes(TimeSpan.FromHours(24));
        extremes.Add(Base, 0.90); // має випасти з вікна
        extremes.Add(Base.AddHours(25), 1.50);
        extremes.Add(Base.AddHours(25).AddMinutes(1), 1.55);

        var snapshot = extremes.Current;

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Value.Count);
        Assert.Equal(1.50, snapshot.Value.Min, precision: 3);
        Assert.Equal(1.55, snapshot.Value.Max, precision: 3);
    }

    [Fact]
    public void Current_IsNullWhenEmpty()
    {
        Assert.Null(new RollingExtremes(TimeSpan.FromHours(24)).Current);
    }
}
