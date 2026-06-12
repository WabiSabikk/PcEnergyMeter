using LibreHardwareMonitor.Hardware;
using PcEnergyMeter.Core;

namespace PcEnergyMeter.App.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly WmiHardwareInfoProvider _profileProvider = new();
    private readonly DdcCiMonitorService _ddcCiMonitorService = new();
    private readonly PowerEstimator _estimator = new();
    private IReadOnlyList<MonitorReading> _lastMonitors = Array.Empty<MonitorReading>();
    private DateTimeOffset _lastMonitorRead = DateTimeOffset.MinValue;
    private ComputerProfile? _profile;
    private DateTimeOffset _lastProfileRead = DateTimeOffset.MinValue;
    private volatile bool _opened;
    private int _openFailures;
    private DateTimeOffset _nextOpenAttempt = DateTimeOffset.MinValue;
    private bool _hardwareErrorLogged;
    private SensorReading[] _lastSensors = Array.Empty<SensorReading>();
    private SensorReading[] _updateResult = Array.Empty<SensorReading>();
    private Task? _inflightUpdate;
    private bool _updateTimeoutLogged;

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            // Материнка (SuperIO через ISA/LPC) і контролери (вбудований EC, фан-контролери) ВИМКНЕНІ
            // навмисно. На кожному Update LHM опитує цю шину, шукаючи SuperIO-чип, але та сама шина й EC
            // монополізовані ASUS Armoury Crate / AsusFanControlService — їхнє утримання шини підвішувало
            // Update(), а отже й увесь застосунок, на нулях. На цьому залізі SuperIO взагалі не дає жодного
            // датчика: напруга ядра йде від CPU (Core VID), потужність — від GPU (NVAPI/ADL). Тож вимкнення
            // прибирає конкуренцію без втрати даних. Watchdog-тайм-аут нижче страхує від будь-якого іншого блокування.
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsStorageEnabled = true,
            IsBatteryEnabled = true
        };
    }

    public void Open()
    {
        if (_opened || DateTimeOffset.Now < _nextOpenAttempt)
        {
            return;
        }

        try
        {
            _computer.Open();
            // Прогрів: перший Update лише ініціалізує дельта-датчики (потужність за RAPL рахується
            // між двома вибірками), тому одразу після Open частина значень — нулі. Робимо холостий апдейт.
            _computer.Accept(_visitor);
            _opened = true;
            _openFailures = 0;
            // Успішний Open закриває попередню смугу збоїв: дозволяємо журналу знову записати причину,
            // якщо LHM відвалиться пізніше посеред сесії.
            _hardwareErrorLogged = false;
        }
        catch (Exception exception)
        {
            // LibreHardwareMonitor падає на Open: напр. ArgumentNullException 'identity' у Mutexes.Open
            // (типово, коли драйвер ядра тримає інший екземпляр), або брак прав на драйвер. РАНІШЕ ми
            // вимикали LHM назавжди — один невдалий старт лишав «лічильники по 0» на всю сесію. Тепер
            // плануємо повтор із наростаючою паузою (до 60 с), щоб відновитись, щойно драйвер звільниться.
            // До відновлення працюємо на WMI-профілі й оцінці. Причину пишемо у hardware.log (раз).
            // Open() міг пройти частково (драйвер піднявся, а Accept кинув) — закриваємо, щоб наступний
            // повтор переініціалізував з чистого стану, а не відкривав уже відкритий Computer.
            _openFailures++;
            _nextOpenAttempt = DateTimeOffset.Now.AddSeconds(Math.Min(60, 5 * _openFailures));
            TryCloseQuietly();
            LogHardwareError($"Open (attempt {_openFailures})", exception);
        }
    }

    public HardwareSnapshot Read(AppSettings settings)
    {
        if (!_opened)
        {
            // Open() теж під watchdog: завантаження ring0-драйвера може зависнути, якщо його перехопив
            // антивірус (Avast автопісочниця). Без цього Read() замерзав би тут і весь UI лишався на 0.
            OpenWithWatchdog(TimeSpan.FromSeconds(6));
        }

        var sensors = _lastSensors;
        if (_opened)
        {
            var (fresh, status) = UpdateSensorsWithWatchdog(TimeSpan.FromSeconds(4));
            switch (status)
            {
                case UpdateStatus.Completed:
                    sensors = fresh!;
                    _lastSensors = fresh!;
                    break;

                case UpdateStatus.TimedOut:
                    // Читання LHM заблоковане (типово — ASUS Armoury Crate тримає EC/ISA-шину). Не валимо
                    // застосунок і не вимикаємо LHM: цей тік ідемо на останніх датчиках + оцінці, а фонова
                    // задача добіжить, щойно шина звільниться. Так UI лишається живим замість зависання.
                    sensors = _lastSensors;
                    break;

                case UpdateStatus.Faulted:
                    // Справжній збій читання — закриваємо й плануємо повторний Open із паузою, а не вимикаємось назавжди.
                    _opened = false;
                    _openFailures++;
                    _nextOpenAttempt = DateTimeOffset.Now.AddSeconds(Math.Min(60, 5 * _openFailures));
                    sensors = Array.Empty<SensorReading>();
                    TryCloseQuietly();
                    break;
            }
        }

        var profile = GetProfile();
        var powerSensors = sensors.Where(sensor => sensor.Kind == "Power").ToArray();
        var voltageSensors = sensors.Where(sensor => sensor.Kind == "Voltage").ToArray();
        var loadSensors = sensors.Where(sensor => sensor.Kind == "Load").ToArray();
        var temperatureSensors = sensors.Where(sensor => sensor.Kind == "Temperature").ToArray();
        var monitors = ReadMonitors();
        var power = _estimator.Estimate(profile, powerSensors, loadSensors, monitors, settings);

        return new HardwareSnapshot(
            DateTimeOffset.Now,
            profile,
            power,
            powerSensors,
            voltageSensors,
            loadSensors,
            temperatureSensors,
            monitors);
    }

    private enum UpdateStatus
    {
        Completed,
        TimedOut,
        Faulted
    }

    /// <summary>
    /// Викликає Open() на фоновій задачі з тайм-аутом. Завантаження ring0-драйвера (PawnIO) усередині
    /// Open може зависнути, коли його перехоплює захисне ПЗ (напр. Avast пісочить процес). Без захисту
    /// Read() замерзав би тут і весь застосунок лишався на 0. При зависанні цей тік іде на оцінці + банері,
    /// а задача добігає у фоні, щойно драйвер дозволять. Справжні дані датчиків повертаються лише після
    /// додавання застосунку у винятки антивіруса — кодом перехоплення драйвера не оминути.
    /// </summary>
    private void OpenWithWatchdog(TimeSpan timeout)
    {
        if (_opened || DateTimeOffset.Now < _nextOpenAttempt)
        {
            return;
        }

        // Попередня спроба відкриття ще висить у нативному коді (драйвер заблокований) — не плодимо потоки.
        if (_inflightUpdate is { IsCompleted: false })
        {
            return;
        }

        var task = Task.Run(Open);
        _inflightUpdate = task;

        try
        {
            if (task.Wait(timeout))
            {
                // Open сам обробляє успіх і збій (журнал + backoff). Тут лише знімаємо inflight.
                _inflightUpdate = null;
            }
            else
            {
                // Open завис: драйвер ring0 ймовірно перехоплений антивірусом. Лишаємо задачу добігати,
                // даємо паузу й логуємо раз, щоб не спамити. UI цей тік покаже оцінку, а не замерзне.
                _nextOpenAttempt = DateTimeOffset.Now.AddSeconds(30);
                if (!_updateTimeoutLogged)
                {
                    _updateTimeoutLogged = true;
                    LogHardwareError(
                        "Open timeout",
                        new TimeoutException(
                            $"LHM Open did not finish within {timeout.TotalSeconds:0}s. The ring0 driver is likely blocked or sandboxed by security software (e.g. Avast auto-sandbox). Add the app folder to the antivirus exceptions to read real sensors. Showing estimates until then."));
                }
            }
        }
        catch
        {
            _inflightUpdate = null;
        }
    }

    /// <summary>
    /// Оновлює датчики LHM на фоновій задачі з тайм-аутом. Нативний Update() може заблокуватись, якщо
    /// інший інструмент (ASUS Armoury Crate, HWiNFO тощо) тримає апаратну шину — без цього захисту
    /// блокування підвішувало б увесь вимірювальний цикл назавжди. Повертає свіжі датчики лише при
    /// успіху; інакше викликач лишається на останніх даних (TimedOut) або переоткриває LHM (Faulted).
    /// </summary>
    private (SensorReading[]? Sensors, UpdateStatus Status) UpdateSensorsWithWatchdog(TimeSpan timeout)
    {
        // Попередній апдейт ще висить у нативному коді (шину досі тримають) — не плодимо заблоковані потоки.
        if (_inflightUpdate is { IsCompleted: false })
        {
            return (null, UpdateStatus.TimedOut);
        }

        var task = Task.Run(() =>
        {
            _computer.Accept(_visitor);
            _updateResult = _computer.Hardware.SelectMany(ReadHardwareSensors).ToArray();
        });
        _inflightUpdate = task;

        try
        {
            // Wait(timeout): true — успіх; кидає при збої задачі; false — тайм-аут.
            if (task.Wait(timeout))
            {
                _inflightUpdate = null;
                // Успішний апдейт = LHM здоровий: переозброюємо журнал, щоб наступна проблема записалась знову.
                _updateTimeoutLogged = false;
                _hardwareErrorLogged = false;
                return (_updateResult, UpdateStatus.Completed);
            }

            // Тайм-аут: лишаємо задачу добігати у фоні (звільниться, коли шина звільниться). Логуємо раз на смугу.
            if (!_updateTimeoutLogged)
            {
                _updateTimeoutLogged = true;
                LogHardwareError(
                    "Update timeout",
                    new TimeoutException(
                        $"Sensor update did not finish within {timeout.TotalSeconds:0}s. Another tool likely holds the hardware bus (e.g. ASUS Armoury Crate). Falling back to estimates."));
            }

            return (null, UpdateStatus.TimedOut);
        }
        catch (Exception exception)
        {
            _inflightUpdate = null;
            LogHardwareError("Update", (exception as AggregateException)?.GetBaseException() ?? exception);
            return (null, UpdateStatus.Faulted);
        }
    }

    public void Dispose()
    {
        // Закриваємо безумовно: Open() міг піднятись частково (драйвер є, але _opened лишився false),
        // і тоді if(_opened) пропустив би Close, лишивши хендл драйвера висіти до наступного запуску.
        TryCloseQuietly();
        _opened = false;
    }

    private void TryCloseQuietly()
    {
        // Безпечне закриття LHM з будь-якого стану (повністю відкритий, частково відкритий після збою
        // Open/Update, або взагалі не відкритий). Потрібне, щоб наступний Open() переініціалізував з нуля,
        // а Dispose не лишив хендл драйвера. Close зламаного/невідкритого монітора сам може кинути — глушимо.
        try
        {
            _computer.Close();
        }
        catch
        {
            // Закриття зламаного монітора саме може кинути — ігноруємо, нас цікавить лише повторний Open.
        }
    }

    private ComputerProfile GetProfile()
    {
        // Профіль заліза майже статичний, а WMI-запити (Win32_PnPEntity тощо) повільні.
        // Тому читаємо його раз і кешуємо, зрідка оновлюючи на випадок підключення USB чи монітора.
        var now = DateTimeOffset.Now;
        if (_profile is null || now - _lastProfileRead > TimeSpan.FromSeconds(60))
        {
            try
            {
                // Якщо LHM не піднявся, його список заліза недоступний — профіль усе одно читається з WMI.
                var hardware = _opened ? _computer.Hardware : Enumerable.Empty<IHardware>();
                _profile = _profileProvider.Read(hardware);
            }
            catch (Exception exception)
            {
                // Профіль не має валити читання: лишаємось на попередньому або мінімальному.
                LogHardwareError("Profile", exception);
                _profile ??= FallbackProfile();
            }

            _lastProfileRead = now;
        }

        return _profile;
    }

    private static ComputerProfile FallbackProfile()
    {
        return new ComputerProfile(
            Environment.MachineName,
            Environment.OSVersion.VersionString,
            "Unknown CPU",
            Array.Empty<string>(),
            Environment.ProcessorCount,
            0,
            1,
            1,
            0,
            Array.Empty<string>(),
            Array.Empty<string>(),
            false);
    }

    private void LogHardwareError(string stage, Exception exception)
    {
        if (_hardwareErrorLogged)
        {
            return;
        }

        _hardwareErrorLogged = true;
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PcEnergyMeter");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "hardware.log"),
                $"{DateTimeOffset.Now:O} [{stage}] {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Журнал не критичний — ігноруємо помилки запису.
        }
    }

    private static IEnumerable<SensorReading> ReadHardwareSensors(IHardware hardware)
    {
        foreach (var subHardware in hardware.SubHardware)
        {
            foreach (var reading in ReadHardwareSensors(subHardware))
            {
                yield return reading;
            }
        }

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value)
            {
                continue;
            }

            var mapped = MapSensor(sensor.SensorType);
            if (mapped is null)
            {
                continue;
            }

            yield return new SensorReading(
                hardware.Name,
                sensor.Name,
                mapped.Value.Kind,
                value,
                mapped.Value.Unit);
        }
    }

    private static (string Kind, string Unit)? MapSensor(SensorType type)
    {
        return type switch
        {
            SensorType.Power => ("Power", "W"),
            SensorType.Voltage => ("Voltage", "V"),
            SensorType.Load => ("Load", "%"),
            SensorType.Temperature => ("Temperature", "°C"),
            _ => null
        };
    }

    private IReadOnlyList<MonitorReading> ReadMonitors()
    {
        var now = DateTimeOffset.Now;
        if (now - _lastMonitorRead < TimeSpan.FromSeconds(5))
        {
            return _lastMonitors;
        }

        _lastMonitorRead = now;
        try
        {
            _lastMonitors = _ddcCiMonitorService.Read();
        }
        catch (Exception exception)
        {
            _lastMonitors = new[]
            {
                new MonitorReading(
                    1,
                    "DDC/CI",
                    "DDC/CI",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new[] { $"DDC/CI error: {exception.Message}" })
            };
        }

        return _lastMonitors;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();

            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
