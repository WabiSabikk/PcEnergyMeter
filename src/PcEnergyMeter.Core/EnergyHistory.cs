namespace PcEnergyMeter.Core;

/// <summary>
/// Фактично спожита енергія по днях: кВт·год плюс мінімальна й максимальна потужність (W) за добу.
/// Віддає суми та діапазони потужності за поточний день, тиждень (з понеділка) і календарний місяць.
/// Зберігається на диск, тож переживає перезапуски — на відміну від сесії, що рахує лише поточний запуск.
/// </summary>
public sealed class EnergyHistory
{
    private const int KeepDays = 400;
    private readonly Dictionary<DateOnly, DailyEntry> _daily = new();

    public EnergyHistory()
    {
    }

    public EnergyHistory(IEnumerable<KeyValuePair<DateOnly, DailyEntry>> daily)
    {
        foreach (var pair in daily)
        {
            if (pair.Value is not null)
            {
                _daily[pair.Key] = new DailyEntry { Kwh = pair.Value.Kwh, MinWatts = pair.Value.MinWatts, MaxWatts = pair.Value.MaxWatts };
            }
        }
    }

    /// <summary>
    /// <paramref name="addedKwh"/> — кВт·год, зараховані за цей крок (0, якщо проміжок пропущено як сон).
    /// <paramref name="watts"/> — поточна потужність для оновлення добового min/max.
    /// </summary>
    public void AddSample(DateTimeOffset at, double addedKwh, double watts)
    {
        if (addedKwh <= 0 && watts <= 0)
        {
            return;
        }

        var day = DateOnly.FromDateTime(at.DateTime);
        if (!_daily.TryGetValue(day, out var entry))
        {
            entry = new DailyEntry();
            _daily[day] = entry;
        }

        if (addedKwh > 0)
        {
            entry.Kwh += addedKwh;
        }

        if (watts > 0)
        {
            entry.MaxWatts = Math.Max(entry.MaxWatts, watts);
            entry.MinWatts = entry.MinWatts <= 0 ? watts : Math.Min(entry.MinWatts, watts);
        }

        Prune(day);
    }

    public double DayKwh(DateTimeOffset now) => _daily.TryGetValue(Today(now), out var entry) ? entry.Kwh : 0;
    public double WeekKwh(DateTimeOffset now) => WeekEntries(now).Sum(entry => entry.Kwh);
    public double MonthKwh(DateTimeOffset now) => MonthEntries(now).Sum(entry => entry.Kwh);

    public WattRange? DayWatts(DateTimeOffset now) => _daily.TryGetValue(Today(now), out var entry) ? RangeOf(new[] { entry }) : null;
    public WattRange? WeekWatts(DateTimeOffset now) => RangeOf(WeekEntries(now));
    public WattRange? MonthWatts(DateTimeOffset now) => RangeOf(MonthEntries(now));

    public IReadOnlyDictionary<DateOnly, DailyEntry> Daily => _daily;

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.DateTime);

    private IEnumerable<DailyEntry> WeekEntries(DateTimeOffset now)
    {
        var today = Today(now);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // понеділок = 0
        return _daily.Where(pair => pair.Key >= monday && pair.Key <= today).Select(pair => pair.Value);
    }

    private IEnumerable<DailyEntry> MonthEntries(DateTimeOffset now)
    {
        var today = Today(now);
        return _daily.Where(pair => pair.Key.Year == today.Year && pair.Key.Month == today.Month).Select(pair => pair.Value);
    }

    private static WattRange? RangeOf(IEnumerable<DailyEntry> entries)
    {
        double? min = null;
        double? max = null;
        foreach (var entry in entries)
        {
            if (entry.MaxWatts > 0)
            {
                max = max is null ? entry.MaxWatts : Math.Max(max.Value, entry.MaxWatts);
            }

            if (entry.MinWatts > 0)
            {
                min = min is null ? entry.MinWatts : Math.Min(min.Value, entry.MinWatts);
            }
        }

        return min is null || max is null ? null : new WattRange(min.Value, max.Value);
    }

    private void Prune(DateOnly newest)
    {
        if (_daily.Count <= KeepDays)
        {
            return;
        }

        var cutoff = newest.AddDays(-KeepDays);
        foreach (var key in _daily.Keys.Where(key => key < cutoff).ToArray())
        {
            _daily.Remove(key);
        }
    }

    public sealed class DailyEntry
    {
        public double Kwh { get; set; }
        public double MinWatts { get; set; }
        public double MaxWatts { get; set; }
    }
}

public readonly record struct WattRange(double Min, double Max);
