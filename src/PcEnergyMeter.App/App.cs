using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace PcEnergyMeter.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // Темна тема: Fluent сам стилізує CheckBox, NumericUpDown, TextBox, Expander, кнопки й скролбари
        // під темний варіант. Власні елементи (картки, метрики, графіки) фарбуємо темною палітрою нижче.
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new MainWindow(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
