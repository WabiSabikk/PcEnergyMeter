using PcEnergyMeter.Core;

namespace PcEnergyMeter.App.Services;

public static class ProbeRunner
{
    public static void Run(TimeSpan duration)
    {
        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        var writer = new CsvLogWriter();
        var session = new EnergySession();
        using var monitor = new HardwareMonitorService();
        monitor.Open();

        var endAt = DateTimeOffset.Now + duration;
        do
        {
            var snapshot = monitor.Read(settings);
            session.AddSample(snapshot.SampledAt, snapshot.Power.TotalWatts);
            writer.Append(snapshot, session, settings.EurPerKwh);
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
        while (DateTimeOffset.Now < endAt);
    }
}
