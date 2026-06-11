using PcEnergyMeter.Core;
using System.Text;

namespace PcEnergyMeter.App.Services;

public sealed class CsvLogWriter
{
    // Дашборд оновлюється щосекунди, але писати на диск так само часто немає сенсу:
    // підсумок дописуємо рідше, а останній зріз перезаписуємо помірно.
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LatestInterval = TimeSpan.FromSeconds(5);
    private const long MaxMeasurementsBytes = 5L * 1024 * 1024;

    private readonly string _measurementsPath;
    private readonly string _latestBreakdownPath;
    private readonly string _latestSensorsPath;
    private readonly string _latestMonitorsPath;
    private DateTimeOffset _lastSummaryAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLatestAt = DateTimeOffset.MinValue;

    public CsvLogWriter()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PcEnergyMeter");
        Directory.CreateDirectory(folder);
        _measurementsPath = Path.Combine(folder, "measurements_v2.csv");
        _latestBreakdownPath = Path.Combine(folder, "latest_breakdown.csv");
        _latestSensorsPath = Path.Combine(folder, "latest_sensors.csv");
        _latestMonitorsPath = Path.Combine(folder, "latest_monitors.csv");
    }

    public void Append(HardwareSnapshot snapshot, EnergySession session, decimal eurPerKwh)
    {
        var now = snapshot.SampledAt;

        if (now - _lastSummaryAt >= SummaryInterval)
        {
            AppendSummary(snapshot, session, eurPerKwh);
            _lastSummaryAt = now;
        }

        if (now - _lastLatestAt >= LatestInterval)
        {
            WriteLatestBreakdown(snapshot);
            WriteLatestSensors(snapshot);
            WriteLatestMonitors(snapshot);
            _lastLatestAt = now;
        }
    }

    private void AppendSummary(HardwareSnapshot snapshot, EnergySession session, decimal eurPerKwh)
    {
        RotateIfTooLarge(_measurementsPath);
        var exists = File.Exists(_measurementsPath);
        using var writer = new StreamWriter(_measurementsPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        if (!exists)
        {
            writer.WriteLine("sampled_at,total_watts,measured_watts,estimated_watts,avg_voltage,total_kwh,total_eur,eur_per_kwh,categories,ddc_ci_monitors");
        }

        writer.WriteLine(string.Join(",",
            snapshot.SampledAt.ToString("O"),
            Format(snapshot.Power.TotalWatts),
            Format(snapshot.Power.MeasuredWatts),
            Format(snapshot.Power.EstimatedWatts),
            Format(AverageVoltage(snapshot)),
            Format(session.TotalKwh),
            session.TotalCost(eurPerKwh).ToString(System.Globalization.CultureInfo.InvariantCulture),
            eurPerKwh.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PowerEstimator.SummarizeCategories(snapshot.Power.Sources).Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            snapshot.Monitors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private void WriteLatestBreakdown(HardwareSnapshot snapshot)
    {
        using var writer = new StreamWriter(_latestBreakdownPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("sampled_at,category,total_watts,measured_watts,estimated_watts,item_count,details");
        foreach (var category in PowerEstimator.SummarizeCategories(snapshot.Power.Sources))
        {
            writer.WriteLine(string.Join(",",
                Escape(snapshot.SampledAt.ToString("O")),
                Escape(category.Category),
                Format(category.TotalWatts),
                Format(category.MeasuredWatts),
                Format(category.EstimatedWatts),
                category.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Escape(string.Join(" | ", category.Items.Select(Describe)))));
        }
    }

    private void WriteLatestSensors(HardwareSnapshot snapshot)
    {
        using var writer = new StreamWriter(_latestSensorsPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("sampled_at,kind,hardware_name,sensor_name,value,unit");
        foreach (var sensor in snapshot.PowerSensors
                     .Concat(snapshot.VoltageSensors)
                     .Concat(snapshot.LoadSensors)
                     .Concat(snapshot.TemperatureSensors))
        {
            writer.WriteLine(string.Join(",",
                Escape(snapshot.SampledAt.ToString("O")),
                Escape(sensor.Kind),
                Escape(sensor.HardwareName),
                Escape(sensor.SensorName),
                Format(sensor.Value),
                Escape(sensor.Unit)));
        }
    }

    private void WriteLatestMonitors(HardwareSnapshot snapshot)
    {
        using var writer = new StreamWriter(_latestMonitorsPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("sampled_at,index,device_name,description,brightness_percent,brightness_current,brightness_minimum,brightness_maximum,power_mode_code,power_mode,is_powered_on,notes");
        foreach (var monitor in snapshot.Monitors)
        {
            writer.WriteLine(string.Join(",",
                Escape(snapshot.SampledAt.ToString("O")),
                monitor.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Escape(monitor.DeviceName),
                Escape(monitor.Description),
                FormatNullable(monitor.BrightnessPercent),
                FormatNullable(monitor.BrightnessCurrent),
                FormatNullable(monitor.BrightnessMinimum),
                FormatNullable(monitor.BrightnessMaximum),
                FormatNullable(monitor.PowerModeCode),
                Escape(monitor.PowerMode ?? string.Empty),
                Escape(monitor.IsPoweredOn?.ToString() ?? string.Empty),
                Escape(string.Join(" | ", monitor.Notes))));
        }
    }

    private static string Describe(PowerSourceDecision source)
    {
        var children = source.Children.Count == 0 ? string.Empty : $" [{string.Join("; ", source.Children.Take(12))}]";
        return $"{source.Decision}:{source.Component}:{source.Watts:0.##}W:{source.Reason}{children}";
    }

    private static string Format(double value)
    {
        return value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatNullable(double? value)
    {
        return value?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string FormatNullable(int? value)
    {
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static double AverageVoltage(HardwareSnapshot snapshot)
    {
        var values = snapshot.VoltageSensors
            .Select(sensor => sensor.Value)
            .Where(value => value > 0 && value < 300)
            .ToArray();

        return values.Length == 0 ? 0 : values.Average();
    }

    private static void RotateIfTooLarge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxMeasurementsBytes)
            {
                return;
            }

            var archive = path + ".1";
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }

            File.Move(path, archive);
        }
        catch
        {
            // Якщо ротація не вдалась (файл зайнятий тощо) — просто продовжуємо дописувати.
        }
    }

    private static string Escape(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}
