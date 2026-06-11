using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PcEnergyMeter.App.Services;

/// <summary>
/// Перевірка прав адміністратора й перезапуск застосунку з елевацією.
/// Потужність CPU/GPU доступна лише з адміністратором, тому це окрема відповідальність.
/// </summary>
public static class ElevationHelper
{
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return IsElevatedWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedWindows()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Перезапускає поточний процес із запитом UAC. Повертає true, якщо новий процес стартував
    /// (тоді викликач має завершити поточний екземпляр).
    /// </summary>
    public static bool RestartElevated()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            // Користувач відхилив UAC або запуск заблоковано — лишаємось у поточному екземплярі.
            return false;
        }
    }
}
