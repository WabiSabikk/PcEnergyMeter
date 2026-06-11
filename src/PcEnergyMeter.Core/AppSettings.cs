namespace PcEnergyMeter.Core;

public sealed class AppSettings
{
    public decimal EurPerKwh { get; set; } = 0.127758m;
    public double CpuFallbackWatts { get; set; } = 65;
    public double DiscreteGpuFallbackWatts { get; set; } = 140;
    public double IntegratedGpuFallbackWatts { get; set; } = 20;
    public double BaseSystemWatts { get; set; } = 18;
    public double MemoryModuleWatts { get; set; } = 3;
    public double StorageDeviceWatts { get; set; } = 3;
    public double UsbStorageDeviceWatts { get; set; } = 4;
    public double UsbDeviceWatts { get; set; } = 0.5;
    public double MonitorMinimumWatts { get; set; } = 8;
    public double MonitorMaximumWatts { get; set; } = 35;
    public double MonitorStandbyWatts { get; set; } = 0.5;
    public bool StartWithWindows { get; set; }
    public bool LogCsv { get; set; } = true;
    public bool StartMinimizedToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
}
