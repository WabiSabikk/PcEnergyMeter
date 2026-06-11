namespace PcEnergyMeter.Core;

public sealed class EnergySession
{
    /// <summary>
    /// Найбільший проміжок між зразками, який ще рахується як безперервне вимірювання.
    /// Більший проміжок означає сон, гібернацію, лаг або зміну годинника — такий крок не рахуємо,
    /// щоб не додати фантомні кВт·год за час, коли застосунок фактично не вимірював.
    /// </summary>
    public static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(30);

    private DateTimeOffset? _lastSampleAt;

    public EnergySession()
        : this(DateTimeOffset.Now, 0)
    {
    }

    public EnergySession(DateTimeOffset startedAt, double totalKwh)
    {
        StartedAt = startedAt;
        TotalKwh = Math.Max(0, totalKwh);
    }

    public DateTimeOffset StartedAt { get; }
    public double TotalKwh { get; private set; }

    /// <summary>
    /// Додає зразок і повертає кВт·год, реально зараховані за цей крок (0, якщо проміжок пропущено
    /// як сон/лаг). Дельту використовує історія спожитого по днях, щоб не дублювати логіку пропусків.
    /// </summary>
    public double AddSample(DateTimeOffset sampledAt, double watts)
    {
        var added = 0d;
        if (_lastSampleAt is { } lastSampleAt)
        {
            var gap = sampledAt - lastSampleAt;
            if (gap > TimeSpan.Zero && gap <= MaxSampleGap)
            {
                added = CostCalculator.WattsToKwh(watts, gap);
                TotalKwh += added;
            }
        }

        _lastSampleAt = sampledAt;
        return added;
    }

    public TimeSpan Elapsed(DateTimeOffset now) => now - StartedAt;

    public decimal TotalCost(decimal eurPerKwh) => CostCalculator.KwhToEur(TotalKwh, eurPerKwh);
}
