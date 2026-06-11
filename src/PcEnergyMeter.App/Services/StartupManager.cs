using System.Diagnostics;
using Microsoft.Win32;

namespace PcEnergyMeter.App.Services;

/// <summary>
/// Автозапуск разом із Windows. Застосунок вимагає прав адміністратора, а елевований процес
/// не запускається тихо з HKCU\Run (на логіні немає UAC-промпта, тож запис ігнорується).
/// Тому автозапуск реалізовано через заплановане завдання з найвищими правами.
/// Старий запис у HKCU\Run прибираємо, щоб не лишати неробочого дубля.
/// </summary>
public sealed class StartupManager
{
    private const string TaskName = "PcEnergyMeter Autostart";
    private const string LegacyRunValueName = "PcEnergyMeter";
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // /query повертає 0, якщо завдання існує.
        return RunSchTasks($"/query /tn \"{TaskName}\"") == 0;
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RemoveLegacyRunEntry();

        if (enabled)
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executablePath))
            {
                return;
            }

            // ONLOGON-тригер для поточного користувача, RL HIGHEST — щоб датчики потужності читались.
            // /F перезаписує наявне завдання, тому шлях до exe завжди актуальний.
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            RunSchTasks(
                $"/create /tn \"{TaskName}\" /tr \"\\\"{executablePath}\\\"\" /sc onlogon /rl highest /ru \"{user}\" /f");
        }
        else
        {
            RunSchTasks($"/delete /tn \"{TaskName}\" /f");
        }
    }

    private static int RunSchTasks(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return -1;
            }

            process.WaitForExit(10_000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void RemoveLegacyRunEntry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            if (key?.GetValue(LegacyRunValueName) is not null)
            {
                key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Якщо ключа немає або доступ обмежений — ігноруємо, це лише прибирання застарілого запису.
        }
    }
}
