using System.Text.Json;
using PcEnergyMeter.Core;

namespace PcEnergyMeter.App.Services;

public sealed class SessionStateStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public SessionStateStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PcEnergyMeter");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "session.json");
    }

    public EnergySession Load()
    {
        if (!File.Exists(_path))
        {
            return new EnergySession();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<SessionState>(json, _options);
            if (state is null || state.StartedAt == default)
            {
                return new EnergySession();
            }

            return new EnergySession(state.StartedAt, state.TotalKwh);
        }
        catch
        {
            return new EnergySession();
        }
    }

    public void Save(EnergySession session)
    {
        var state = new SessionState(session.StartedAt, session.TotalKwh, DateTimeOffset.Now);
        var json = JsonSerializer.Serialize(state, _options);
        File.WriteAllText(_path, json);
    }

    public void Reset()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private sealed record SessionState(DateTimeOffset StartedAt, double TotalKwh, DateTimeOffset SavedAt);
}
