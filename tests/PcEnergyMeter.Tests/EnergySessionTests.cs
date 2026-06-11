using PcEnergyMeter.Core;

namespace PcEnergyMeter.Tests;

public sealed class EnergySessionTests
{
    [Fact]
    public void RestoredSessionKeepsPreviousKwhAndDoesNotCountClosedTime()
    {
        var session = new EnergySession(DateTimeOffset.Parse("2026-06-10T10:00:00Z"), 0.25);

        // Перший зразок після відновлення не рахує дві години, поки застосунок був закритий.
        session.AddSample(DateTimeOffset.Parse("2026-06-10T12:00:00Z"), 100);

        Assert.Equal(0.25, session.TotalKwh, precision: 6);

        // Наступний зразок через секунду додає енергію поверх відновленого значення.
        session.AddSample(DateTimeOffset.Parse("2026-06-10T12:00:01Z"), 120);

        Assert.True(session.TotalKwh > 0.25);
    }

    [Fact]
    public void AddSample_IgnoresLargeGapFromSleepOrClockChange()
    {
        var session = new EnergySession(DateTimeOffset.Parse("2026-06-10T10:00:00Z"), 0);

        session.AddSample(DateTimeOffset.Parse("2026-06-10T10:00:00Z"), 100);
        // ПК спав три години: один зразок не повинен додати watts × 3 год.
        session.AddSample(DateTimeOffset.Parse("2026-06-10T13:00:00Z"), 100);

        Assert.Equal(0, session.TotalKwh, precision: 6);
    }

    [Fact]
    public void AddSample_IgnoresBackwardClockChange()
    {
        var session = new EnergySession(DateTimeOffset.Parse("2026-06-10T10:00:00Z"), 0.1);

        session.AddSample(DateTimeOffset.Parse("2026-06-10T10:00:05Z"), 100);
        // Годинник зсунувся назад: від'ємний проміжок не зменшує накопичене значення.
        session.AddSample(DateTimeOffset.Parse("2026-06-10T10:00:03Z"), 100);

        Assert.Equal(0.1, session.TotalKwh, precision: 6);
    }
}
