using PcEnergyMeter.Core;

namespace PcEnergyMeter.Tests;

public sealed class EnergyHistoryTests
{
    // Понеділок 2026-06-08 .. сьогодні четвер 2026-06-11; місяць — червень.
    private static readonly DateTimeOffset Thursday = new(2026, 6, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Day_Week_Month_AggregateKwh()
    {
        var history = new EnergyHistory();
        history.AddSample(new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero), 5.0, 100); // минулий місяць
        history.AddSample(new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero), 2.0, 100);  // понеділок
        history.AddSample(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero), 3.0, 100); // середа
        history.AddSample(Thursday, 1.0, 100);                                                  // сьогодні
        history.AddSample(Thursday.AddHours(2), 0.5, 100);                                       // сьогодні ще

        Assert.Equal(1.5, history.DayKwh(Thursday), precision: 3);
        Assert.Equal(6.5, history.WeekKwh(Thursday), precision: 3);  // 2 + 3 + 1 + 0.5
        Assert.Equal(6.5, history.MonthKwh(Thursday), precision: 3); // червень без 31 травня
    }

    [Fact]
    public void Watts_TrackMinMaxPerPeriod()
    {
        var history = new EnergyHistory();
        history.AddSample(Thursday, 0.1, 50);
        history.AddSample(Thursday.AddMinutes(1), 0.1, 312);
        history.AddSample(Thursday.AddMinutes(2), 0.1, 120);
        history.AddSample(new DateTimeOffset(2026, 6, 8, 9, 0, 0, TimeSpan.Zero), 0.1, 40); // понеділок, найнижче

        var day = history.DayWatts(Thursday);
        Assert.NotNull(day);
        Assert.Equal(50, day!.Value.Min, precision: 1);
        Assert.Equal(312, day.Value.Max, precision: 1);

        var week = history.WeekWatts(Thursday);
        Assert.NotNull(week);
        Assert.Equal(40, week!.Value.Min, precision: 1);  // мін по всіх днях тижня
        Assert.Equal(312, week.Value.Max, precision: 1);  // макс по всіх днях тижня
    }

    [Fact]
    public void AddSample_IgnoresWhenNothingToRecord()
    {
        var history = new EnergyHistory();
        history.AddSample(Thursday, 0, 0);
        history.AddSample(Thursday, -1, -1);

        Assert.Empty(history.Daily);
        Assert.Null(history.DayWatts(Thursday));
    }

    [Fact]
    public void Roundtrip_PreservesKwhAndWatts()
    {
        var history = new EnergyHistory();
        history.AddSample(Thursday, 1.25, 90);

        var restored = new EnergyHistory(history.Daily);

        Assert.Equal(1.25, restored.DayKwh(Thursday), precision: 3);
        Assert.Equal(90, restored.DayWatts(Thursday)!.Value.Max, precision: 1);
    }
}
