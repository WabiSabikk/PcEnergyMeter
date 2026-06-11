using System.Management;
using LibreHardwareMonitor.Hardware;
using PcEnergyMeter.Core;

namespace PcEnergyMeter.App.Services;

public sealed class WmiHardwareInfoProvider
{
    public ComputerProfile Read(IEnumerable<IHardware> hardware)
    {
        var cpuName = QueryFirst("Win32_Processor", "Name") ?? FirstHardwareName(hardware, HardwareType.Cpu) ?? "Unknown CPU";
        var gpus = QueryAll("Win32_VideoController", "Name")
            .Concat(hardware.Where(item => item.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia).Select(item => item.Name))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var os = QueryFirst("Win32_OperatingSystem", "Caption") ?? Environment.OSVersion.VersionString;
        var logicalProcessors = ParseInt(QueryFirst("Win32_Processor", "NumberOfLogicalProcessors"), Environment.ProcessorCount);
        var memoryBytes = QueryAll("Win32_PhysicalMemory", "Capacity").Select(ParseLong).Where(value => value > 0).ToArray();
        var memoryGb = memoryBytes.Length == 0 ? 0 : memoryBytes.Sum() / 1024d / 1024d / 1024d;
        var moduleCount = Math.Max(1, memoryBytes.Length);
        var diskInterfaces = QueryAll("Win32_DiskDrive", "InterfaceType").ToArray();
        var usbStorageCount = diskInterfaces.Count(value => value.Contains("USB", StringComparison.OrdinalIgnoreCase));
        var storageCount = Math.Max(1, diskInterfaces.Length - usbStorageCount);
        var usbDevices = QueryAll("Win32_PnPEntity", "Name")
            .Where(IsUsbDeviceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToArray();
        var monitors = QueryAll("Win32_DesktopMonitor", "Name")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var chassisTypes = QueryAll("Win32_SystemEnclosure", "ChassisTypes").ToArray();
        var isLaptop = chassisTypes.Any(IsLaptopChassis) || cpuName.Contains("mobile", StringComparison.OrdinalIgnoreCase);

        return new ComputerProfile(
            Environment.MachineName,
            os,
            cpuName.Trim(),
            gpus,
            logicalProcessors,
            memoryGb,
            moduleCount,
            storageCount,
            usbStorageCount,
            usbDevices,
            monitors,
            isLaptop);
    }

    private static string? FirstHardwareName(IEnumerable<IHardware> hardware, HardwareType type)
    {
        return hardware.FirstOrDefault(item => item.HardwareType == type)?.Name;
    }

    private static string? QueryFirst(string wmiClass, string property)
    {
        return QueryAll(wmiClass, property).FirstOrDefault();
    }

    private static IReadOnlyList<string> QueryAll(string wmiClass, string property)
    {
        var values = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return values;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                var value = item[property];
                if (value is null)
                {
                    continue;
                }

                values.Add(value is Array array
                    ? string.Join(",", array.Cast<object>())
                    : value.ToString() ?? string.Empty);
            }
        }
        catch (Exception exception)
        {
            // WMI-клас недоступний або запит впав (брак прав, зайнятий WMI, чужий контекст запуску,
            // або System.Management-заглушка на неправильному TFM) — повертаємо вже зібране, профіль
            // дістане запасні значення замість падіння всього читання. Причину пишемо у hardware.log
            // (раз), бо тихий catch колись приховав PlatformNotSupportedException і завів діагностику не туди.
            LogWmiFailure(wmiClass, exception);
        }

        return values;
    }

    private static bool _failureLogged;

    private static void LogWmiFailure(string wmiClass, Exception exception)
    {
        if (_failureLogged || !OperatingSystem.IsWindows())
        {
            return;
        }

        _failureLogged = true;
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PcEnergyMeter");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "hardware.log"),
                $"{DateTimeOffset.Now:O} [WMI {wmiClass}] {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Журнал не критичний — ігноруємо помилки запису.
        }
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static long ParseLong(string value)
    {
        return long.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static bool IsLaptopChassis(string value)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Any(part => part is "8" or "9" or "10" or "14" or "30" or "31" or "32");
    }

    private static bool IsUsbDeviceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("hub", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("extensible host", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Composite Device", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Mass Storage", StringComparison.OrdinalIgnoreCase);
    }
}
