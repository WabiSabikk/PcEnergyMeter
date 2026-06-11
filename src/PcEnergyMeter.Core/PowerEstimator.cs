namespace PcEnergyMeter.Core;

public sealed class PowerEstimator
{
    public PowerBreakdown Estimate(
        ComputerProfile profile,
        IReadOnlyList<SensorReading> powerSensors,
        IReadOnlyList<SensorReading> loadSensors,
        IReadOnlyList<MonitorReading> monitors,
        AppSettings settings)
    {
        var decisions = new List<PowerSourceDecision>();
        var notes = new List<string>();
        var measuredWatts = SelectMeasuredPower(powerSensors, decisions);
        var estimatedWatts = 0d;

        estimatedWatts += AddEstimate(decisions, "System", "System", "Base", settings.BaseSystemWatts, "Motherboard, fans, controllers, and minor peripherals.");
        estimatedWatts += AddEstimate(decisions, "RAM", "Memory", "Fallback", Math.Max(1, profile.MemoryModuleCount) * settings.MemoryModuleWatts, $"RAM estimated by module count: {Math.Max(1, profile.MemoryModuleCount)}.");
        estimatedWatts += AddEstimate(decisions, "Storage", "Storage", "Fallback", Math.Max(0, profile.StorageDeviceCount) * settings.StorageDeviceWatts, $"Internal storage estimated by count: {Math.Max(0, profile.StorageDeviceCount)}.");

        if (profile.UsbStorageDeviceCount > 0)
        {
            estimatedWatts += AddEstimate(decisions, "USB", "USB storage", "Fallback", profile.UsbStorageDeviceCount * settings.UsbStorageDeviceWatts, $"USB storage estimated by count: {profile.UsbStorageDeviceCount}.", profile.UsbDeviceNames);
        }
        else if (profile.UsbDeviceNames.Count > 0)
        {
            estimatedWatts += AddEstimate(decisions, "USB", "USB devices", "Fallback", profile.UsbDeviceNames.Count * settings.UsbDeviceWatts, $"USB devices without a power sensor estimated by count: {profile.UsbDeviceNames.Count}.", profile.UsbDeviceNames);
        }

        estimatedWatts += AddMonitorDecisions(decisions, monitors, profile, settings);

        if (monitors.Count == 0 && profile.MonitorNames.Count > 0)
        {
            decisions.Add(new PowerSourceDecision(
                "not_counted",
                "Monitor",
                "Monitor",
                "Windows",
                string.Empty,
                string.Empty,
                0,
                "Windows sees the monitor but does not report its power. DDC/CI gave no data; a wattmeter or UPS is needed for accurate watts.",
                profile.MonitorNames));
        }

        if (!HasIncludedCategory(decisions, "CPU"))
        {
            var cpuLoad = AverageLoad(loadSensors, "cpu");
            estimatedWatts += AddEstimate(decisions, "CPU", "CPU", "Fallback", EstimateDynamicPart(settings.CpuFallbackWatts, cpuLoad, idleFloorRatio: 0.18), $"CPU estimated from load: {cpuLoad:0}%.");

            // If the CPU has a power sensor but all values are 0, this is not "no sensor" — the sensor
            // returns 0. Typical for AMD Ryzen 7000 power tables on boards where LHM has no mapping.
            // Elevation, restart, and newer LHM versions have been tried; none help.
            var hasDeadCpuPowerSensor = powerSensors.Any(sensor => ClassifyCategory(sensor) == "CPU");
            notes.Add(hasDeadCpuPowerSensor
                ? $"CPU: power sensor returns 0 W and cannot be read on this hardware via LHM. Showing load-based estimate at {cpuLoad:0}% load."
                : $"CPU estimated from load: {cpuLoad:0}%.");
        }

        var gpuNames = profile.GpuNames.Count > 0 ? profile.GpuNames : new[] { "GPU" };
        foreach (var gpuName in gpuNames)
        {
            if (HasIncludedGpu(decisions, gpuName))
            {
                continue;
            }

            var gpuLoad = AverageLoad(loadSensors, gpuName);
            var fallback = IsIntegratedGpu(gpuName)
                ? settings.IntegratedGpuFallbackWatts
                : settings.DiscreteGpuFallbackWatts;

            estimatedWatts += AddEstimate(decisions, "GPU", ComponentName("GPU", gpuName), "Fallback", EstimateDynamicPart(fallback, gpuLoad, idleFloorRatio: 0.12), $"{gpuName} estimated from load: {gpuLoad:0}%.");
            notes.Add($"{gpuName} estimated from load: {gpuLoad:0}%.");
        }

        if (powerSensors.Count == 0)
        {
            notes.Add("No power sensors found. This is an estimate.");
        }
        else if (notes.Count > 0)
        {
            notes.Add("Some power is measured by sensors, some is estimated.");
        }

        return new PowerBreakdown(
            measuredWatts,
            estimatedWatts,
            measuredWatts + estimatedWatts,
            notes,
            decisions);
    }

    public static IReadOnlyList<PowerCategorySummary> SummarizeCategories(IEnumerable<PowerSourceDecision> sources)
    {
        return sources
            .Where(source => source.Decision is "included" or "estimated" or "not_counted")
            .GroupBy(source => source.Category)
            .Select(group =>
            {
                var items = group.ToArray();
                var measured = items.Where(item => item.Decision == "included").Sum(item => item.Watts);
                var estimated = items.Where(item => item.Decision == "estimated").Sum(item => item.Watts);
                return new PowerCategorySummary(group.Key, measured, estimated, measured + estimated, items);
            })
            .OrderByDescending(item => item.TotalWatts)
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static double SelectMeasuredPower(
        IReadOnlyList<SensorReading> powerSensors,
        List<PowerSourceDecision> decisions)
    {
        var validSensors = powerSensors
            .Where(sensor => sensor.Value > 0 && sensor.Value < 2000)
            .Select(sensor => (Sensor: sensor, Component: ClassifyComponent(sensor), Category: ClassifyCategory(sensor)))
            .ToArray();

        foreach (var invalid in powerSensors.Except(validSensors.Select(item => item.Sensor)))
        {
            decisions.Add(Skipped(invalid, ClassifyComponent(invalid), ClassifyCategory(invalid), "Value out of valid range 0-2000 W or empty."));
        }

        var measured = 0d;
        foreach (var group in validSensors.GroupBy(item => item.Component))
        {
            var selected = group
                .OrderByDescending(item => SensorPriority(item.Sensor))
                .ThenByDescending(item => item.Sensor.Value)
                .First();

            measured += selected.Sensor.Value;
            decisions.Add(new PowerSourceDecision(
                "included",
                selected.Component,
                selected.Category,
                "sensor",
                selected.Sensor.HardwareName,
                selected.Sensor.SensorName,
                selected.Sensor.Value,
                "Best power sensor for this component.",
                Array.Empty<string>()));

            foreach (var skipped in group.Where(item => !ReferenceEquals(item.Sensor, selected.Sensor)))
            {
                decisions.Add(Skipped(
                    skipped.Sensor,
                    skipped.Component,
                    skipped.Category,
                    $"Skipped as possible duplicate; already included {selected.Sensor.SensorName}."));
            }
        }

        return measured;
    }

    private static PowerSourceDecision Skipped(SensorReading sensor, string component, string category, string reason) =>
        new("skipped", component, category, "sensor", sensor.HardwareName, sensor.SensorName, 0, reason, Array.Empty<string>());

    private static double AddEstimate(
        List<PowerSourceDecision> decisions,
        string category,
        string component,
        string source,
        double watts,
        string reason,
        IReadOnlyList<string>? children = null)
    {
        decisions.Add(new PowerSourceDecision(
            "estimated",
            component,
            category,
            source,
            string.Empty,
            string.Empty,
            watts,
            reason,
            children ?? Array.Empty<string>()));

        return watts;
    }

    private static double EstimateDynamicPart(double maxWatts, double loadPercent, double idleFloorRatio)
    {
        var loadRatio = Math.Clamp(loadPercent / 100d, 0d, 1d);
        var floor = maxWatts * idleFloorRatio;
        return floor + (maxWatts - floor) * loadRatio;
    }

    private static double AddMonitorDecisions(
        List<PowerSourceDecision> decisions,
        IReadOnlyList<MonitorReading> monitors,
        ComputerProfile profile,
        AppSettings settings)
    {
        if (monitors.Count == 0)
        {
            return 0;
        }

        var total = 0d;
        foreach (var monitor in monitors)
        {
            var children = MonitorChildren(monitor).ToArray();
            if (monitor.IsPoweredOn == false)
            {
                total += AddEstimate(
                    decisions,
                    "Monitor",
                    MonitorComponentName(monitor),
                    "DDC/CI",
                    settings.MonitorStandbyWatts,
                    $"DDC/CI reports power state: {monitor.PowerMode ?? "not on"}. Using standby estimate.",
                    children);
                continue;
            }

            if (monitor.BrightnessPercent is { } brightness)
            {
                var ratio = Math.Clamp(brightness / 100d, 0d, 1d);
                var watts = settings.MonitorMinimumWatts + (settings.MonitorMaximumWatts - settings.MonitorMinimumWatts) * ratio;
                total += AddEstimate(
                    decisions,
                    "Monitor",
                    MonitorComponentName(monitor),
                    "DDC/CI",
                    watts,
                    $"Estimate based on DDC/CI brightness {brightness:0}% in range {settings.MonitorMinimumWatts:0.#}-{settings.MonitorMaximumWatts:0.#} W. Not a wall-socket measurement.",
                    children);
                continue;
            }

            decisions.Add(new PowerSourceDecision(
                "not_counted",
                MonitorComponentName(monitor),
                "Monitor",
                "DDC/CI",
                string.Empty,
                string.Empty,
                0,
                "DDC/CI found the monitor but brightness is unavailable, so no watts added.",
                children));
        }

        if (profile.MonitorNames.Count > monitors.Count)
        {
            decisions.Add(new PowerSourceDecision(
                "not_counted",
                "Monitor",
                "Monitor",
                "Windows",
                string.Empty,
                string.Empty,
                0,
                "Some monitors are visible through Windows but not through DDC/CI. Added as uncounted to avoid duplicates.",
                profile.MonitorNames.Skip(monitors.Count).ToArray()));
        }

        return total;
    }

    private static IEnumerable<string> MonitorChildren(MonitorReading monitor)
    {
        yield return $"Description: {monitor.Description}";
        if (monitor.BrightnessPercent is { } brightness)
        {
            yield return $"Brightness: {brightness:0}% ({monitor.BrightnessCurrent}/{monitor.BrightnessMaximum})";
        }
        else
        {
            yield return "Brightness: no data";
        }

        yield return $"Power: {monitor.PowerMode ?? "no data"}";

        foreach (var note in monitor.Notes)
        {
            yield return note;
        }
    }

    private static string MonitorComponentName(MonitorReading monitor)
    {
        var name = string.IsNullOrWhiteSpace(monitor.Description) ? monitor.DeviceName : monitor.Description;
        return ComponentName("Monitor", name);
    }

    private static bool HasIncludedCategory(IEnumerable<PowerSourceDecision> decisions, string category)
    {
        return decisions.Any(decision =>
            decision.Decision == "included" &&
            decision.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasIncludedGpu(IEnumerable<PowerSourceDecision> decisions, string gpuName)
    {
        return decisions.Any(decision =>
            decision.Decision == "included" &&
            decision.Category.Equals("GPU", StringComparison.OrdinalIgnoreCase) &&
            (decision.Component.Contains(gpuName, StringComparison.OrdinalIgnoreCase) ||
             gpuName.Contains(decision.HardwareName, StringComparison.OrdinalIgnoreCase) ||
             decision.HardwareName.Contains(gpuName, StringComparison.OrdinalIgnoreCase)));
    }

    private static double AverageLoad(IEnumerable<SensorReading> sensors, string hardwareNamePart)
    {
        var values = sensors
            .Where(sensor =>
                sensor.HardwareName.Contains(hardwareNamePart, StringComparison.OrdinalIgnoreCase) ||
                sensor.SensorName.Contains(hardwareNamePart, StringComparison.OrdinalIgnoreCase))
            .Select(sensor => sensor.Value)
            .Where(value => value >= 0 && value <= 100)
            .ToArray();

        return values.Length == 0 ? 20 : values.Average();
    }

    private static bool IsIntegratedGpu(string gpuName)
    {
        return gpuName.Contains("intel", StringComparison.OrdinalIgnoreCase) ||
               gpuName.Contains("radeon graphics", StringComparison.OrdinalIgnoreCase) ||
               gpuName.Contains("vega", StringComparison.OrdinalIgnoreCase) ||
               gpuName.Contains("iris", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyComponent(SensorReading sensor)
    {
        var category = ClassifyCategory(sensor);
        return category switch
        {
            "CPU" => "CPU",
            "GPU" => ComponentName("GPU", sensor.HardwareName),
            "Battery" => "Battery",
            _ => ComponentName(category, sensor.HardwareName)
        };
    }

    private static string ClassifyCategory(SensorReading sensor)
    {
        var name = $"{sensor.HardwareName} {sensor.SensorName}";
        if (ContainsAny(name, "cpu", "processor", "ryzen", "core i"))
        {
            return "CPU";
        }

        if (ContainsAny(name, "gpu", "nvidia", "radeon", "geforce", "intel graphics"))
        {
            return "GPU";
        }

        if (name.Contains("battery", StringComparison.OrdinalIgnoreCase))
        {
            return "Battery";
        }

        if (ContainsAny(name, "ssd", "hdd", "nvme", "disk", "drive"))
        {
            return "Storage";
        }

        return "Other";
    }

    private static string ComponentName(string type, string name)
    {
        return $"{type}:{NormalizeName(name)}";
    }

    private static string NormalizeName(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80];
    }

    private static int SensorPriority(SensorReading sensor)
    {
        var name = sensor.SensorName;
        if (ContainsAny(name, "total board", "board power", "gpu power", "package", "cpu package", "ppt", "total"))
        {
            return 100;
        }

        if (ContainsAny(name, "chip", "core power", "cores"))
        {
            return 50;
        }

        return 10;
    }

    private static bool ContainsAny(string value, params string[] parts)
    {
        return parts.Any(part => value.Contains(part, StringComparison.OrdinalIgnoreCase));
    }
}
