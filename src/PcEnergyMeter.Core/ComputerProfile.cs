namespace PcEnergyMeter.Core;

public sealed record ComputerProfile(
    string ComputerName,
    string OperatingSystem,
    string ProcessorName,
    IReadOnlyList<string> GpuNames,
    int LogicalProcessorCount,
    double MemoryGb,
    int MemoryModuleCount,
    int StorageDeviceCount,
    int UsbStorageDeviceCount,
    IReadOnlyList<string> UsbDeviceNames,
    IReadOnlyList<string> MonitorNames,
    bool IsLaptop);
