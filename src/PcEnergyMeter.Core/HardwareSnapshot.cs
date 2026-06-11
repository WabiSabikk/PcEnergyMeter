namespace PcEnergyMeter.Core;

public sealed record SensorReading(
    string HardwareName,
    string SensorName,
    string Kind,
    double Value,
    string Unit);

public sealed record PowerBreakdown(
    double MeasuredWatts,
    double EstimatedWatts,
    double TotalWatts,
    IReadOnlyList<string> Notes,
    IReadOnlyList<PowerSourceDecision> Sources);

public sealed record PowerSourceDecision(
    string Decision,
    string Component,
    string Category,
    string Source,
    string HardwareName,
    string SensorName,
    double Watts,
    string Reason,
    IReadOnlyList<string> Children);

public sealed record PowerCategorySummary(
    string Category,
    double MeasuredWatts,
    double EstimatedWatts,
    double TotalWatts,
    IReadOnlyList<PowerSourceDecision> Items);

public sealed record MonitorReading(
    int Index,
    string DeviceName,
    string Description,
    int? BrightnessMinimum,
    int? BrightnessCurrent,
    int? BrightnessMaximum,
    double? BrightnessPercent,
    int? PowerModeCode,
    string? PowerMode,
    bool? IsPoweredOn,
    IReadOnlyList<string> Notes);

public sealed record HardwareSnapshot(
    DateTimeOffset SampledAt,
    ComputerProfile Profile,
    PowerBreakdown Power,
    IReadOnlyList<SensorReading> PowerSensors,
    IReadOnlyList<SensorReading> VoltageSensors,
    IReadOnlyList<SensorReading> LoadSensors,
    IReadOnlyList<SensorReading> TemperatureSensors,
    IReadOnlyList<MonitorReading> Monitors);
