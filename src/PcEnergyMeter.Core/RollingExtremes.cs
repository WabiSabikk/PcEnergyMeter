namespace PcEnergyMeter.Core;

/// <summary>
/// Ковзне вікно: тримає зразки за останній проміжок (напр. 24 год) і віддає мінімум та максимум
/// разом із часом, коли вони сталися. Старіші за вікно зразки відкидаються при кожному додаванні.
/// </summary>
public sealed class RollingExtremes
{
    private readonly TimeSpan _window;
    private readonly Queue<Sample> _samples = new();

    public RollingExtremes(TimeSpan window)
    {
        _window = window;
    }

    public void Add(DateTimeOffset at, double value)
    {
        _samples.Enqueue(new Sample(at, value));

        // Викидаємо все, що старіше за вікно відносно найновішого зразка.
        var cutoff = at - _window;
        while (_samples.Count > 0 && _samples.Peek().At < cutoff)
        {
            _samples.Dequeue();
        }
    }

    public ExtremesSnapshot? Current
    {
        get
        {
            if (_samples.Count == 0)
            {
                return null;
            }

            var min = _samples.Peek();
            var max = min;
            foreach (var sample in _samples)
            {
                if (sample.Value < min.Value)
                {
                    min = sample;
                }

                if (sample.Value > max.Value)
                {
                    max = sample;
                }
            }

            return new ExtremesSnapshot(min.Value, min.At, max.Value, max.At, _samples.Count);
        }
    }

    private readonly record struct Sample(DateTimeOffset At, double Value);
}

public readonly record struct ExtremesSnapshot(double Min, DateTimeOffset MinAt, double Max, DateTimeOffset MaxAt, int Count);
