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
    private bool _opened;
    private int _openFailures;
    private DateTimeOffset _nextOpenAttempt = DateTimeOffset.MinValue;
    private bool _hardwareErrorLogged;

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
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
            Open();
        }

        var sensors = Array.Empty<SensorReading>();
        if (_opened)
        {
            try
            {
                _computer.Accept(_visitor);
                sensors = _computer.Hardware.SelectMany(ReadHardwareSensors).ToArray();
            }
            catch (Exception exception)
            {
                // Читання датчиків впало посеред роботи — знімок не валимо, лишаємось на оцінці. Закриваємо
                // монітор і плануємо повторний Open із паузою, а не вимикаємось назавжди: проблема (напр.
                // інший екземпляр чи приспаний драйвер) часто тимчасова й минає сама.
                _opened = false;
                _openFailures++;
                _nextOpenAttempt = DateTimeOffset.Now.AddSeconds(Math.Min(60, 5 * _openFailures));
                sensors = Array.Empty<SensorReading>();
                TryCloseQuietly();
                LogHardwareError("Update", exception);
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
