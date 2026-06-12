namespace PcEnergyMeter.App.Services;

/// <summary>
/// Гарантує єдиний екземпляр на користувацьку сесію. Автозапуск (заплановане завдання onlogon)
/// і ручний запуск інакше дають два процеси, які б'ються за драйвер ядра LibreHardwareMonitor,
/// глобальні м'ютекси та файли стану. Переможений гонки падає в Mutexes.Open() і показує нулі.
/// Другий екземпляр сигналить наявному показати вікно і одразу завершується.
///
/// Імена без префікса Global потрапляють у Local-простір (на сесію). І автозадача onlogon, і
/// ручний запуск виконуються в одній інтерактивній сесії користувача, тож цього достатньо — і це
/// навмисно уникає ACL на іменованому об'єкті, та ж сама операція, що ламає Mutexes.Open() у LHM.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "PcEnergyMeter.SingleInstance.v1";
    private const string EventName = "PcEnergyMeter.ShowWindow.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Thread? _listener;
    private volatile bool _disposed;

    public bool IsPrimaryInstance { get; private set; }

    /// <summary>
    /// Повертає true, якщо це перший екземпляр. Якщо ні — сигналить наявному екземпляру показати
    /// вікно й повертає false; викликач має одразу завершитись.
    /// </summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;

        if (!createdNew)
        {
            SignalExistingInstance();
            return false;
        }

        // Створюємо подію ОДРАЗУ при отриманні першості — ще до старту UI. Інакше другий екземпляр,
        // що запускається під час ініціалізації Avalonia (autostart + холодний диск — кілька секунд),
        // не знайшов би події через TryOpenExisting і сигнал «показати вікно» зник би. Подія з AutoReset
        // лишається сигнальною до першого WaitOne, тож слухач, запущений пізніше, все одно її підхопить.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        return true;
    }

    /// <summary>
    /// Запускає фоновий слухач: коли другий екземпляр сигналить, викликає <paramref name="onShowRequested"/>
    /// у наявному (першому) екземплярі. Без ефекту, якщо це не перший екземпляр.
    /// </summary>
    public void StartShowListener(Action onShowRequested)
    {
        if (!IsPrimaryInstance || _showEvent is null)
        {
            return;
        }

        _listener = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_showEvent.WaitOne(500) && !_disposed)
                    {
                        onShowRequested();
                    }
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceShowListener"
        };
        _listener.Start();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
            {
                handle.Set();
                handle.Dispose();
            }
        }
        catch
        {
            // Якщо сигнал не пройшов — наявний екземпляр усе одно працює; новий просто виходить.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            // Розбудити слухача, щоб він вийшов із WaitOne і завершив потік.
            _showEvent?.Set();
        }
        catch
        {
            // Ігноруємо: об'єкт міг уже бути закритий.
        }

        _listener?.Join(1000);
        _showEvent?.Dispose();
        _mutex?.Dispose();
    }
}
