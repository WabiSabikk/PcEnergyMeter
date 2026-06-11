using PcEnergyMeter.Core;

namespace PcEnergyMeter.Tests;

public sealed class PowerEstimatorTests
{
    [Fact]
    public void Estimate_AddsMeasuredAndEstimatedPower()
    {
        var profile = new ComputerProfile(
            "PC",
            "Windows",
            "AMD Ryzen",
            new[] { "NVIDIA RTX" },
            16,
            32,
            2,
            1,
            0,
            Array.Empty<string>(),
            Array.Empty<string>(),
            false);

        var powerSensors = new[]
        {
            new SensorReading("CPU Package", "Package", "Power", 50, "W")
        };

        var loadSensors = new[]
        {
            new SensorReading("NVIDIA RTX", "GPU Core", "Load", 50, "%")
        };

        var estimate = new PowerEstimator().Estimate(profile, powerSensors, loadSensors, Array.Empty<MonitorReading>(), new AppSettings());

        Assert.True(estimate.TotalWatts > 140);
        Assert.Equal(50, estimate.MeasuredWatts, precision: 2);
        Assert.Contains(estimate.Notes, note => note.Contains("NVIDIA RTX", StringComparison.Ordinal));
    }

    [Fact]
    public void Estimate_FallsBackWhenNoSensorsExist()
    {
        var profile = new ComputerProfile("PC", "Windows", "Intel Core", Array.Empty<string>(), 8, 16, 1, 1, 0, Array.Empty<string>(), Array.Empty<string>(), true);

        var estimate = new PowerEstimator().Estimate(profile, Array.Empty<SensorReading>(), Array.Empty<SensorReading>(), Array.Empty<MonitorReading>(), new AppSettings());

        Assert.True(estimate.TotalWatts > 0);
        Assert.Contains(estimate.Notes, note => note.Contains("Датчики потужності", StringComparison.Ordinal));
    }

    [Fact]
    public void Estimate_AddsDdcCiMonitorEstimateWithoutWindowsDuplicate()
    {
        var profile = new ComputerProfile(
            "PC",
            "Windows",
            "Intel Core",
            Array.Empty<string>(),
            8,
            16,
            1,
            1,
            0,
            Array.Empty<string>(),
            new[] { "Generic PnP Monitor" },
            false);
        var monitors = new[]
        {
            new MonitorReading(
                1,
                "Display 1",
                "Dell U2720Q",
                0,
                50,
                100,
                50,
                1,
                "увімкнено",
                true,
                new[] { "DDC/CI відповів." })
        };

        var estimate = new PowerEstimator().Estimate(
            profile,
            Array.Empty<SensorReading>(),
            Array.Empty<SensorReading>(),
            monitors,
            new AppSettings { MonitorMinimumWatts = 10, MonitorMaximumWatts = 30 });

        var monitorSources = estimate.Sources.Where(source => source.Category == "Монітор").ToArray();

        Assert.Single(monitorSources);
        Assert.Equal("estimated", monitorSources[0].Decision);
        Assert.Equal(20, monitorSources[0].Watts, precision: 2);
    }
}
