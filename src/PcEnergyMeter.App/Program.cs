using System.Runtime.InteropServices;
using Avalonia;
using PcEnergyMeter.App.Services;

namespace PcEnergyMeter.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Раніше будь-який виняток на старті (наприклад збій ініціалізації LibreHardwareMonitor у
        // конструкторі MainWindow) валив процес тихо, без вікна. Тепер пишемо crash.log і показуємо причину.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        if (args.Length > 0 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
        {
            var seconds = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 10;
            ProbeRunner.Run(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120)));
            return 0;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            ReportCrash(exception);
            return 1;
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static void ReportCrash(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        var logPath = "(write failed)";
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PcEnergyMeter");
            Directory.CreateDirectory(folder);
            logPath = Path.Combine(folder, "crash.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Якщо навіть лог не пишеться — усе одно спробуємо показати вікно з помилкою нижче.
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                MessageBoxW(
                    nint.Zero,
                    $"The app failed to start.{Environment.NewLine}{Environment.NewLine}{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{Environment.NewLine}Details: {logPath}",
                    "Pc Energy Meter",
                    0x10); // MB_ICONERROR
            }
            catch
            {
                // Нативне вікно теж може не піднятись — лог уже записано вище.
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
