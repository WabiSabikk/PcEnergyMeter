using System.Globalization;
using System.Text.Json;
using PcEnergyMeter.Core;

namespace PcEnergyMeter.App.Services;

public sealed class EnergyHistoryStore
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public EnergyHistoryStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PcEnergyMeter");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "energy_history.json");
    }

    public EnergyHistory Load()
    {
        if (!File.Exists(_path))
        {
            return new EnergyHistory();
        }

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch
        {
            return new EnergyHistory();
        }

        // Новий формат: {"yyyy-MM-dd": {Kwh, MinWatts, MaxWatts}}.
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, EnergyHistory.DailyEntry>>(json, _options);
            if (raw is not null && raw.Values.All(value => value is not null))
            {
                return Build(raw);
            }
        }
        catch
        {
            // Можливо старий формат — пробуємо нижче.
        }

        // Старий формат: {"yyyy-MM-dd": kWh} (до додавання min/max потужності).
        try
        {
            var old = JsonSerializer.Deserialize<Dictionary<string, double>>(json, _options);
            if (old is not null)
            {
                return Build(old.ToDictionary(pair => pair.Key, pair => new EnergyHistory.DailyEntry { Kwh = pair.Value }));
            }
        }
        catch
        {
            // Зіпсований файл — починаємо з порожньої історії.
        }

        return new EnergyHistory();
    }

    public void Save(EnergyHistory history)
    {
        try
        {
            var raw = history.Daily.ToDictionary(
                pair => pair.Key.ToString(DateFormat, CultureInfo.InvariantCulture),
                pair => pair.Value);

            // Атомарний запис: пишемо в тимчасовий файл і підміняємо одним викликом. Інакше краш чи
            // вимкнення живлення посеред щосекундного запису лишили б обрізаний JSON і обнулили історію.
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(raw, _options));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            // Збереження історії не критичне для роботи — ігноруємо помилки запису.
        }
    }

    private static EnergyHistory Build(Dictionary<string, EnergyHistory.DailyEntry> raw)
    {
        var parsed = raw
            .Select(pair => (
                Ok: DateOnly.TryParseExact(pair.Key, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day),
                Day: day,
                pair.Value))
            .Where(item => item.Ok && item.Value is not null)
            .Select(item => new KeyValuePair<DateOnly, EnergyHistory.DailyEntry>(item.Day, item.Value));

        return new EnergyHistory(parsed);
    }
}
