using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PcEnergyMeter.App.Services;
using PcEnergyMeter.Core;

namespace PcEnergyMeter.App;

public sealed class MainWindow : Window
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly SettingsStore _settingsStore = new();
    private readonly SessionStateStore _sessionStateStore = new();
    private readonly EnergyHistoryStore _energyHistoryStore = new();
    private readonly HardwareMonitorService _monitor = new();
    private readonly StartupManager _startupManager = new();
    private readonly CsvLogWriter _csvLogWriter = new();
    private readonly DispatcherTimer _timer = new();
    private readonly AppSettings _settings;
    private EnergySession _session;
    private EnergyHistory _energyHistory;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _traySpendItem;
    private bool _isExiting;
    private bool _servicesStopped;
    private readonly PowerChart _chart = new("Power (last 120 s)", "W", "#4ADE8B");
    private readonly PowerChart _voltageChart = new("CPU core voltage (last 120 s)", "V", "#54B0FF");
    private readonly TextBlock _watts = new();
    private readonly TextBlock _voltage = new();
    private readonly TextBlock _measured = new();
    private readonly TextBlock _estimated = new();
    private readonly TextBlock _kwh = new();
    private readonly TextBlock _cost = new();
    private readonly TextBlock _costDay = new();
    private readonly TextBlock _costWeek = new();
    private readonly TextBlock _costMonth = new();
    private readonly TextBlock _kwhDay = new();
    private readonly TextBlock _kwhWeek = new();
    private readonly TextBlock _kwhMonth = new();
    private readonly TextBlock _rangeDay = new();
    private readonly TextBlock _rangeWeek = new();
    private readonly TextBlock _rangeMonth = new();
    private readonly TextBlock _projDay = new();
    private readonly TextBlock _projWeek = new();
    private readonly TextBlock _projMonth = new();
    private readonly TextBlock _voltageRange = new();
    private readonly RollingExtremes _voltage24h = new(TimeSpan.FromHours(24));
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _profilePanel = new() { Spacing = 1 };
    private readonly TextBox _sensors = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace") };
    private readonly StackPanel _breakdownPanel = new() { Spacing = 2 };
    private readonly NumericUpDown _tariff = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly CheckBox _csvLogging = new();
    private readonly CheckBox _startMinimizedToTray = new();
    private readonly CheckBox _closeToTray = new();
    private readonly Border _banner = new();
    private readonly TextBlock _bannerText = new();
    private readonly Button _bannerButton = new();
    private readonly bool _isElevated = ElevationHelper.IsElevated();
    private readonly object _sessionLock = new();
    private volatile bool _reading;

    // Палітра: одна узгоджена темна тема з семантичними акцентами.
    private static readonly IBrush WindowBg = Brush("#0F1218");
    private static readonly IBrush Surface = Brush("#181C25");
    private static readonly IBrush SurfaceAlt = Brush("#1F2632");
    private static readonly IBrush CardBorder = Brush("#272F3C");
    private static readonly IBrush TextPrimary = Brush("#EAECF2");
    private static readonly IBrush TextSecondary = Brush("#9AA3B2");
    private static readonly IBrush TextMuted = Brush("#6B7280");
    private static readonly IBrush AccentGreen = Brush("#4ADE8B");
    private static readonly IBrush Blue = Brush("#54B0FF");
    private static readonly IBrush Amber = Brush("#F7C948");
    private static readonly IBrush Violet = Brush("#A78BFA");
    private static readonly IBrush Teal = Brush("#2DD4BF");
    private static readonly IBrush Pink = Brush("#F472B6");

    public MainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _settings = _settingsStore.Load();
        _session = _sessionStateStore.Load();
        _energyHistory = _energyHistoryStore.Load();

        Title = "Pc Energy Meter";
        ShowInTaskbar = true;
        Width = 1200;
        Height = 800;
        MinWidth = 1040;
        MinHeight = 720;
        Background = WindowBg;
        FontFamily = new FontFamily("Segoe UI");
        Icon = LoadAppIcon();
        Content = BuildLayout();
        CreateTrayIcon();

        // Коли другий екземпляр намагається запуститись, він сигналить нам показати вікно замість
        // того, щоб підняти власний процес. Слухач спрацьовує у фоновому потоці, тож повертаємось у UI-потік.
        Program.InstanceGuard?.StartShowListener(() => Dispatcher.UIThread.Post(ShowFromTray));

        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => RefreshSnapshot();
        Opened += (_, _) =>
        {
            RefreshSnapshot();
            _timer.Start();
            if (_settings.StartMinimizedToTray)
            {
                HideToTray();
            }
        };
        Closing += (_, args) =>
        {
            if (!_isExiting)
            {
                if (_settings.CloseToTray)
                {
                    args.Cancel = true;
                    HideToTray();
                    return;
                }

                _isExiting = true;
                Dispatcher.UIThread.Post(() => _desktop.Shutdown());
            }

            ShutdownServices();
        };
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto")
        };

        AddRow(root, BuildTopbar(), 0);
        AddRow(root, BuildBanner(), 1);
        AddRow(root, BuildOverview(), 2);
        AddRow(root, BuildMain(), 3);

        _status.FontSize = 12;
        _status.Foreground = TextMuted;
        _status.Margin = new Thickness(4, 12, 4, 0);
        AddRow(root, _status, 4);

        return root;
    }

    private Control BuildTopbar()
    {
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "Pc Energy Meter",
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary
        });
        titles.Children.Add(new TextBlock
        {
            Text = "PC electricity cost tracker",
            FontSize = 13,
            Foreground = TextSecondary
        });

        var elevation = ElevationPill();
        Grid.SetColumn(elevation, 1);

        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(4, 0, 4, 16),
            Children = { titles, elevation }
        };
        return bar;
    }

    private Control ElevationPill()
    {
        var on = _isElevated;
        var color = on ? AccentGreen : Amber;
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = color, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock
        {
            Text = on ? "Administrator" : "No administrator rights",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = color,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        return new Border
        {
            Background = SurfaceAlt,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } }
        };
    }

    private Control BuildBanner()
    {
        _bannerText.TextWrapping = TextWrapping.Wrap;
        _bannerText.VerticalAlignment = VerticalAlignment.Center;
        _bannerText.FontSize = 13;
        _bannerText.Foreground = TextPrimary;

        _bannerButton.Content = "Restart as administrator";
        _bannerButton.VerticalAlignment = VerticalAlignment.Center;
        _bannerButton.IsVisible = false;
        _bannerButton.Click += (_, _) => RestartWithAdminRights();

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 10) };
        content.Children.Add(_bannerText);
        Grid.SetColumn(_bannerButton, 1);
        content.Children.Add(_bannerButton);

        _banner.CornerRadius = new CornerRadius(10);
        _banner.BorderThickness = new Thickness(1);
        _banner.Margin = new Thickness(4, 0, 4, 14);
        _banner.IsVisible = false;
        _banner.Child = content;
        return _banner;
    }

    private void UpdateBanner(HardwareSnapshot snapshot)
    {
        if (!_isElevated)
        {
            ShowBanner(
                "Running without administrator rights. CPU/GPU power unavailable; showing load-based estimates only.",
                "#3A2E12", "#F7C948",
                showButton: true);
            return;
        }

        var hasMeasuredCpuOrGpu = snapshot.Power.Sources.Any(source =>
            source.Decision == "included" &&
            (source.Category.Equals("CPU", StringComparison.OrdinalIgnoreCase) ||
             source.Category.Equals("GPU", StringComparison.OrdinalIgnoreCase)));

        if (!hasMeasuredCpuOrGpu)
        {
            ShowBanner(
                "Administrator rights present, but CPU/GPU power sensors return no data on this hardware. Showing load-based estimates.",
                "#332B12", "#FCE9A8",
                showButton: false);
            return;
        }

        _banner.IsVisible = false;
    }

    private void ShowBanner(string text, string bgHex, string borderHex, bool showButton)
    {
        _bannerText.Text = text;
        _banner.Background = Brush(bgHex);
        _banner.BorderBrush = Brush(borderHex);
        _bannerButton.IsVisible = showButton;
        _banner.IsVisible = true;
    }

    private void RestartWithAdminRights()
    {
        if (ElevationHelper.RestartElevated())
        {
            ExitApplication();
        }
        else
        {
            _status.Text = "Failed to restart as administrator: UAC denied or launch blocked.";
        }
    }

    // Огляд: великий герой-показник зліва + плитки прогнозу справа.
    private Control BuildOverview()
    {
        var hero = BuildHeroCard();
        var kpis = BuildKpiStrip();
        Grid.SetColumn(kpis, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("360,*"),
            Margin = new Thickness(0, 0, 0, 14),
            Children = { hero, kpis }
        };
    }

    private Control BuildHeroCard()
    {
        _watts.Text = "0 W";
        _watts.FontSize = 46;
        _watts.FontWeight = FontWeight.Bold;
        _watts.Foreground = AccentGreen;

        var caption = new TextBlock { Text = "CURRENT DRAW", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = TextMuted };

        var details = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(0, 14, 0, 0)
        };
        HeroRow(details, 0, Blue, "Measured by sensors", _measured, TextPrimary);
        HeroRow(details, 1, Amber, "Estimated", _estimated, TextPrimary);
        HeroRow(details, 2, null, "CPU core voltage", _voltage, TextPrimary);
        HeroRow(details, 3, null, "Session kWh", _kwh, TextPrimary);
        HeroRow(details, 4, null, "Session cost", _cost, AccentGreen);

        var stack = new StackPanel
        {
            Children =
            {
                caption,
                _watts,
                new Border { Height = 1, Background = CardBorder, Margin = new Thickness(0, 10, 0, 0) },
                details
            }
        };
        var card = Card(stack);
        card.Margin = new Thickness(0, 0, 14, 0);
        return card;
    }

    private void HeroRow(Grid grid, int row, IBrush? dot, string label, TextBlock value, IBrush valueColor)
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5), VerticalAlignment = VerticalAlignment.Center };
        if (dot is not null)
        {
            left.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        }

        left.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(left, row);

        value.FontSize = 14;
        value.FontWeight = FontWeight.SemiBold;
        value.Foreground = valueColor;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);

        grid.Children.Add(left);
        grid.Children.Add(value);
    }

    private Control BuildKpiStrip()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        AddKpi(grid, 0, "Today", _costDay, _kwhDay, _rangeDay, _projDay);
        AddKpi(grid, 1, "This week", _costWeek, _kwhWeek, _rangeWeek, _projWeek);
        AddKpi(grid, 2, "This month", _costMonth, _kwhMonth, _rangeMonth, _projMonth);
        return grid;
    }

    private void AddKpi(Grid grid, int column, string caption, TextBlock cost, TextBlock kwh, TextBlock range, TextBlock projection)
    {
        cost.Text = "0,0000 €";
        cost.FontSize = 23;
        cost.FontWeight = FontWeight.Bold;
        cost.Foreground = AccentGreen;

        kwh.Text = "0.000 kWh";
        kwh.FontSize = 12;
        kwh.Foreground = TextSecondary;
        kwh.Margin = new Thickness(0, 5, 0, 0);

        range.Text = "power: —";
        range.FontSize = 12;
        range.Foreground = TextSecondary;

        projection.Text = "forecast: —";
        projection.FontSize = 12;
        projection.Foreground = TextMuted;
        projection.Margin = new Thickness(0, 3, 0, 0);

        var content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = caption, FontSize = 12, Foreground = TextMuted, Margin = new Thickness(0, 0, 0, 8) },
                cost,
                kwh,
                new Border { Height = 1, Background = CardBorder, Margin = new Thickness(0, 10) },
                range,
                projection
            }
        };

        var card = Card(content);
        card.Margin = new Thickness(column == 0 ? 0 : 7, 0, column == 2 ? 0 : 7, 0);
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    // Основна зона: ліворуч живі дані (графік + розклад), праворуч — конфіг і профіль.
    private Control BuildMain()
    {
        var left = new Grid { RowDefinitions = new RowDefinitions("*,*") };
        var chartCard = Card(BuildChartContent());
        chartCard.Margin = new Thickness(0, 0, 0, 7);
        AddRow(left, chartCard, 0);

        var breakdownCard = Card(new DockPanel
        {
            Children =
            {
                Docked(SectionTitle("Where the watts go"), Dock.Top),
                new ScrollViewer { Content = _breakdownPanel }
            }
        });
        breakdownCard.Margin = new Thickness(0, 7, 0, 0);
        AddRow(left, breakdownCard, 1);

        var right = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    BuildProfileCard(),
                    BuildVoltageCard(),
                    BuildSettingsCard()
                }
            }
        };
        Grid.SetColumn(right, 1);
        right.Margin = new Thickness(14, 0, 0, 0);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,420"),
            Children = { left, right }
        };
    }

    private Control BuildChartContent()
    {
        return new DockPanel
        {
            Children =
            {
                Docked(SectionTitle("Power over time"), Dock.Top),
                _chart
            }
        };
    }

    private Control BuildProfileCard()
    {
        return Card(new StackPanel
        {
            Children =
            {
                SectionTitle("Computer"),
                _profilePanel
            }
        });
    }

    private Control BuildVoltageCard()
    {
        _voltageChart.MinHeight = 140;
        _voltageRange.Text = "Last 24 h: accumulating…";
        _voltageRange.FontSize = 12;
        _voltageRange.Foreground = TextSecondary;
        _voltageRange.TextWrapping = TextWrapping.Wrap;
        _voltageRange.Margin = new Thickness(2, 12, 2, 0);

        return Card(new DockPanel
        {
            Children =
            {
                Docked(_voltageRange, Dock.Bottom),
                _voltageChart
            }
        });
    }

    private Control BuildSettingsCard()
    {
        _tariff.Minimum = 0.000001m;
        _tariff.Maximum = 10m;
        _tariff.Increment = 0.001m;
        _tariff.FormatString = "0.000000";
        _tariff.Value = _settings.EurPerKwh;
        _tariff.ValueChanged += (_, args) =>
        {
            if (args.NewValue is { } value)
            {
                _settings.EurPerKwh = value;
            }
        };

        _startWithWindows.Content = "Start with Windows";
        _startWithWindows.IsChecked = _startupManager.IsEnabled();
        _startWithWindows.IsCheckedChanged += (_, _) => SetStartup(_startWithWindows.IsChecked == true);

        _csvLogging.Content = "CSV logging";
        _csvLogging.IsChecked = _settings.LogCsv;
        _csvLogging.IsCheckedChanged += (_, _) => _settings.LogCsv = _csvLogging.IsChecked == true;

        _startMinimizedToTray.Content = "Start minimized";
        _startMinimizedToTray.IsChecked = _settings.StartMinimizedToTray;
        _startMinimizedToTray.IsCheckedChanged += (_, _) => _settings.StartMinimizedToTray = _startMinimizedToTray.IsChecked == true;

        _closeToTray.Content = "X minimizes to tray";
        _closeToTray.IsChecked = _settings.CloseToTray;
        _closeToTray.IsCheckedChanged += (_, _) => _settings.CloseToTray = _closeToTray.IsChecked == true;

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SectionTitle("Settings"),
                new TextBlock { Text = "Tariff, €/kWh", FontSize = 13, Foreground = TextSecondary },
                _tariff,
                new TextBlock
                {
                    Text = "Edit to match your electricity contract rate.",
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap
                },
                new Border { Height = 1, Background = CardBorder, Margin = new Thickness(0, 2) },
                _startWithWindows,
                _csvLogging,
                _startMinimizedToTray,
                _closeToTray
            }
        };

        return Card(panel);
    }

    private void RefreshSnapshot()
    {
        // Читання датчиків (LHM + WMI + DDC/CI) і запис на диск повільні — виносимо їх із UI-потоку,
        // інакше вікно підлагує щосекунди. Поки попереднє читання триває, новий тік пропускаємо.
        if (_reading || _servicesStopped)
        {
            return;
        }

        _reading = true;
        Task.Run(() =>
        {
            try
            {
                var snapshot = _monitor.Read(_settings);

                lock (_sessionLock)
                {
                    var addedKwh = _session.AddSample(snapshot.SampledAt, snapshot.Power.TotalWatts);
                    _energyHistory.AddSample(snapshot.SampledAt, addedKwh, snapshot.Power.TotalWatts);
                    _sessionStateStore.Save(_session);
                    _energyHistoryStore.Save(_energyHistory);
                }

                if (_settings.LogCsv)
                {
                    _csvLogWriter.Append(snapshot, _session, _settings.EurPerKwh);
                }

                Dispatcher.UIThread.Post(() => UpdateUi(snapshot));
            }
            catch (Exception exception)
            {
                Dispatcher.UIThread.Post(() => _status.Text = $"Sensor read error: {exception.Message}");
            }
            finally
            {
                _reading = false;
            }
        });
    }

    private void UpdateUi(HardwareSnapshot snapshot)
    {
        var watts = snapshot.Power.TotalWatts;
        var voltage = CpuCoreVoltage(snapshot);
        _chart.Add(watts);
        if (voltage is not null)
        {
            _voltageChart.Add(voltage.Value);
            _voltage24h.Add(snapshot.SampledAt, voltage.Value);
        }

        double totalKwh;
        decimal totalCost;
        double dayKwh, weekKwh, monthKwh;
        WattRange? dayWatts, weekWatts, monthWatts;
        lock (_sessionLock)
        {
            totalKwh = _session.TotalKwh;
            totalCost = _session.TotalCost(_settings.EurPerKwh);
            dayKwh = _energyHistory.DayKwh(snapshot.SampledAt);
            weekKwh = _energyHistory.WeekKwh(snapshot.SampledAt);
            monthKwh = _energyHistory.MonthKwh(snapshot.SampledAt);
            dayWatts = _energyHistory.DayWatts(snapshot.SampledAt);
            weekWatts = _energyHistory.WeekWatts(snapshot.SampledAt);
            monthWatts = _energyHistory.MonthWatts(snapshot.SampledAt);
        }

        var tariff = _settings.EurPerKwh;
        _watts.Text = $"{watts:0.0} W";
        _voltage.Text = voltage is null ? "no data" : $"{voltage.Value:0.000} V";
        _measured.Text = $"{snapshot.Power.MeasuredWatts:0.0} W";
        _estimated.Text = $"{snapshot.Power.EstimatedWatts:0.0} W";
        _kwh.Text = $"{totalKwh:0.000000} kWh";
        _cost.Text = $"{totalCost:0.000000} €";

        _costDay.Text = $"{CostCalculator.KwhToEur(dayKwh, tariff):0.0000} €";
        _kwhDay.Text = $"{dayKwh:0.000} kWh";
        _rangeDay.Text = FormatWattRange(dayWatts);
        _projDay.Text = $"day forecast: {CostCalculator.ProjectCost(watts, TimeSpan.FromDays(1), tariff):0.000} €";

        _costWeek.Text = $"{CostCalculator.KwhToEur(weekKwh, tariff):0.000} €";
        _kwhWeek.Text = $"{weekKwh:0.000} kWh";
        _rangeWeek.Text = FormatWattRange(weekWatts);
        _projWeek.Text = $"week forecast: {CostCalculator.ProjectCost(watts, TimeSpan.FromDays(7), tariff):0.00} €";

        _costMonth.Text = $"{CostCalculator.KwhToEur(monthKwh, tariff):0.00} €";
        _kwhMonth.Text = $"{monthKwh:0.00} kWh";
        _rangeMonth.Text = FormatWattRange(monthWatts);
        _projMonth.Text = $"month forecast: {CostCalculator.ProjectCost(watts, TimeSpan.FromDays(30), tariff):0.00} €";

        UpdateVoltageRange();
        UpdateTrayAndTaskbar(watts);
        UpdateBanner(snapshot);
        UpdateProfile(snapshot);
        UpdateBreakdown(snapshot);

        var monitorStatus = snapshot.Monitors.Count == 0
            ? "DDC/CI monitors: 0"
            : $"DDC/CI monitors: {snapshot.Monitors.Count}";
        _status.Text = snapshot.VoltageSensors.Count == 0
            ? $"{snapshot.SampledAt:HH:mm:ss}. {monitorStatus}. Mains voltage unavailable without an external wattmeter; no motherboard voltage sensors found."
            : $"{snapshot.SampledAt:HH:mm:ss}. {monitorStatus}. Voltage sensors: {snapshot.VoltageSensors.Count}.";
    }

    private static string FormatWattRange(WattRange? range)
        => range is null ? "power: —" : $"min {range.Value.Min:0} W · max {range.Value.Max:0} W";

    private void UpdateVoltageRange()
    {
        var extremes = _voltage24h.Current;
        _voltageRange.Text = extremes is null
            ? "Last 24 h: no voltage data"
            : $"Last 24 h:  min {extremes.Value.Min:0.000} V ({extremes.Value.MinAt:HH:mm})   ·   max {extremes.Value.Max:0.000} V ({extremes.Value.MaxAt:HH:mm})";
    }

    private void UpdateProfile(HardwareSnapshot snapshot)
    {
        var profile = snapshot.Profile;
        _profilePanel.Children.Clear();

        _profilePanel.Children.Add(ProfileRow(Blue, "CPU", $"{profile.ProcessorName}  ·  {profile.LogicalProcessorCount} threads"));

        var gpus = profile.GpuNames.Count == 0 ? "Not found" : string.Join(", ", profile.GpuNames);
        _profilePanel.Children.Add(ProfileRow(Violet, "GPU", gpus));

        _profilePanel.Children.Add(ProfileRow(AccentGreen, "RAM", $"{profile.MemoryGb:0.0} GB  ·  {profile.MemoryModuleCount} modules"));
        _profilePanel.Children.Add(ProfileRow(Amber, "Storage", $"{profile.StorageDeviceCount} internal  ·  {profile.UsbStorageDeviceCount} USB"));
        _profilePanel.Children.Add(ProfileRow(Pink, "USB devices", profile.UsbDeviceNames.Count.ToString()));

        var monitors = $"{snapshot.Monitors.Count} DDC/CI  ·  {profile.MonitorNames.Count} Windows";
        _profilePanel.Children.Add(ProfileRow(Teal, "Monitors", monitors));
        _profilePanel.Children.Add(ProfileRow(null, "OS", profile.OperatingSystem));
        _profilePanel.Children.Add(ProfileRow(null, "Type", profile.IsLaptop ? "laptop" : "desktop"));

        if (snapshot.Power.Notes.Count > 0)
        {
            _profilePanel.Children.Add(new Border { Height = 1, Background = CardBorder, Margin = new Thickness(0, 8) });
            foreach (var note in snapshot.Power.Notes)
            {
                _profilePanel.Children.Add(new TextBlock
                {
                    Text = $"• {note}",
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1)
                });
            }
        }
    }

    private Control ProfileRow(IBrush? dot, string label, string value)
    {
        var labelPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (dot is not null)
        {
            labelPanel.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        }
        else
        {
            labelPanel.Children.Add(new Border { Width = 8, Margin = new Thickness(0, 0, 8, 0) });
        }

        labelPanel.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(labelPanel, 0);

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right
        };
        Grid.SetColumn(valueBlock, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Margin = new Thickness(0, 5),
            Children = { labelPanel, valueBlock }
        };
    }

    private void UpdateBreakdown(HardwareSnapshot snapshot)
    {
        _breakdownPanel.Children.Clear();

        var categories = PowerEstimator.SummarizeCategories(snapshot.Power.Sources)
            .Where(category => category.TotalWatts > 0)
            .ToArray();
        var maxWatts = categories.Length == 0 ? 1 : Math.Max(1, categories.Max(category => category.TotalWatts));

        foreach (var category in categories)
        {
            _breakdownPanel.Children.Add(BreakdownBar(category, maxWatts));
        }

        if (categories.Length == 0)
        {
            _breakdownPanel.Children.Add(new TextBlock { Text = "Waiting for data…", FontSize = 13, Foreground = TextMuted });
        }

        _breakdownPanel.Children.Add(new Border { Height = 1, Background = CardBorder, Margin = new Thickness(0, 10, 0, 6) });

        var detailText = string.Join(Environment.NewLine, snapshot.Power.Sources.Select(FormatSource));
        _breakdownPanel.Children.Add(new Expander
        {
            Header = "Decision details",
            Content = new TextBlock { Text = detailText, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = TextSecondary }
        });

        _sensors.Text = FormatSensors(snapshot);
        _sensors.Foreground = TextSecondary;
        _breakdownPanel.Children.Add(new Expander
        {
            Header = $"Raw sensors: {snapshot.PowerSensors.Count + snapshot.VoltageSensors.Count + snapshot.LoadSensors.Count + snapshot.TemperatureSensors.Count}",
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = _sensors
            }
        });

        if (snapshot.Monitors.Count > 0)
        {
            _breakdownPanel.Children.Add(new Expander
            {
                Header = $"DDC/CI monitors: {snapshot.Monitors.Count}",
                Content = new TextBlock
                {
                    Text = FormatMonitors(snapshot.Monitors),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = TextSecondary
                }
            });
        }
    }

    private Control BreakdownBar(PowerCategorySummary category, double maxWatts)
    {
        var ratio = Math.Clamp(category.TotalWatts / maxWatts, 0, 1);
        var color = CategoryColor(category.Category);

        var fill = new Border { Background = color, CornerRadius = new CornerRadius(5) };
        Grid.SetColumn(fill, 0);
        var track = new Border
        {
            Background = SurfaceAlt,
            CornerRadius = new CornerRadius(5),
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0),
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(Math.Max(ratio, 0.0001), GridUnitType.Star),
                    new ColumnDefinition(Math.Max(1 - ratio, 0.0001), GridUnitType.Star)
                },
                Children = { fill }
            }
        };
        Grid.SetColumn(track, 1);

        var label = new TextBlock { Text = category.Category, FontSize = 13, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);

        var watt = new TextBlock { Text = $"{category.TotalWatts:0.0} W", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = TextPrimary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(watt, 2);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("120,*,76"),
            Margin = new Thickness(0, 5),
            Children = { label, track, watt }
        };
        ToolTip.SetTip(row, $"Measured {category.MeasuredWatts:0.0} W · estimated {category.EstimatedWatts:0.0} W");
        return row;
    }

    private static IBrush CategoryColor(string category)
    {
        if (category.Contains("CPU", StringComparison.OrdinalIgnoreCase))
        {
            return Blue;
        }

        if (category.Contains("GPU", StringComparison.OrdinalIgnoreCase))
        {
            return Violet;
        }

        if (category.Contains("RAM", StringComparison.OrdinalIgnoreCase))
        {
            return AccentGreen;
        }

        if (category.Contains("Storage", StringComparison.OrdinalIgnoreCase))
        {
            return Amber;
        }

        if (category.Contains("Monitor", StringComparison.OrdinalIgnoreCase))
        {
            return Teal;
        }

        if (category.Contains("USB", StringComparison.OrdinalIgnoreCase))
        {
            return Pink;
        }

        return TextMuted;
    }

    private static string FormatSensors(HardwareSnapshot snapshot)
    {
        var sensors = snapshot.PowerSensors
            .Concat(snapshot.VoltageSensors)
            .Concat(snapshot.LoadSensors)
            .Concat(snapshot.TemperatureSensors);

        return string.Join(Environment.NewLine, sensors.Select(sensor =>
            $"{sensor.Kind,-13} | {sensor.HardwareName,-30} | {sensor.SensorName,-28} | {sensor.Value:0.##} {sensor.Unit}"));
    }

    private static string FormatSource(PowerSourceDecision source)
    {
        var device = string.IsNullOrWhiteSpace(source.HardwareName)
            ? string.Empty
            : $" | {source.HardwareName} {source.SensorName}".Trim();
        var children = source.Children.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", source.Children)}";

        return $"{TranslateDecision(source.Decision)} | {source.Component} | {source.Watts:0.##} W | {source.Reason}{device}{children}";
    }

    private static string FormatMonitors(IReadOnlyList<MonitorReading> monitors)
    {
        return string.Join($"{Environment.NewLine}{Environment.NewLine}", monitors.Select(monitor =>
            $"{monitor.Index}. {monitor.Description}{Environment.NewLine}" +
            $"   Brightness: {FormatBrightness(monitor)}{Environment.NewLine}" +
            $"   Power: {monitor.PowerMode ?? "no data"}{Environment.NewLine}" +
            $"   Notes: {string.Join("; ", monitor.Notes)}"));
    }

    private static string FormatBrightness(MonitorReading monitor)
    {
        return monitor.BrightnessPercent is { } percent
            ? $"{percent:0}% ({monitor.BrightnessCurrent}/{monitor.BrightnessMaximum})"
            : "no data";
    }

    private static bool IsCpuSensor(SensorReading sensor)
    {
        var text = $"{sensor.HardwareName} {sensor.SensorName}";
        return text.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Processor", StringComparison.OrdinalIgnoreCase);
    }

    private static string TranslateDecision(string decision)
    {
        return decision switch
        {
            "included" => "measured",
            "estimated" => "estimated",
            "skipped" => "skipped",
            "not_counted" => "not counted",
            _ => decision
        };
    }

    private static double? CpuCoreVoltage(HardwareSnapshot snapshot)
    {
        // Усереднювати всі rail-и (CPU VID ~1 В, +12 В, +5 В, +3.3 В) фізично беззмістовно.
        // Беремо лише напругу ядра CPU у реалістичному діапазоні VID.
        var values = snapshot.VoltageSensors
            .Where(sensor => IsCpuSensor(sensor) && sensor.Value > 0 && sensor.Value < 3)
            .Select(sensor => sensor.Value)
            .ToArray();

        return values.Length == 0 ? null : values.Average();
    }

    private void SetStartup(bool enabled)
    {
        _settings.StartWithWindows = enabled;
        _startupManager.SetEnabled(enabled);
    }

    private void CreateTrayIcon()
    {
        // Неактивний рядок-заголовок: показує витрачені за сесію кошти прямо в меню трею,
        // щоб суму було видно при відкритті меню, а не лише при наведенні на іконку.
        _traySpendItem = new NativeMenuItem($"Session spend: {0m:0.000000} €") { IsEnabled = false };

        var showItem = new NativeMenuItem("Open");
        showItem.Click += (_, _) => ShowFromTray();

        var hideItem = new NativeMenuItem("Hide");
        hideItem.Click += (_, _) => HideToTray();

        var resetItem = new NativeMenuItem("Reset session");
        resetItem.Click += (_, _) => ResetSession();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        var menu = new NativeMenu();
        menu.Items.Add(_traySpendItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(showItem);
        menu.Items.Add(hideItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(resetItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = LoadAppIcon(),
            ToolTipText = "Pc Energy Meter",
            IsVisible = true,
            Menu = menu
        };
        _trayIcon.Clicked += (_, _) =>
        {
            TrayLog("clicked");
            ShowFromTray();
        };
    }

    private void ShowFromTray()
    {
        // Кнопка завжди лишається на панелі задач (ShowInTaskbar = true), тож «показати» — це просто
        // розгорнути з мінімізованого стану. Show() — захист на випадок, якщо вікно колись сховане.
        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Activate();
        // Розгортання з трея/панелі не передає фокус процесу, тож піднімаємо вікно над іншими.
        Topmost = true;
        Topmost = false;
        TrayLog("show");
    }

    private void HideToTray()
    {
        // Згортаємо на панель задач, а не ховаємо: кнопка лишається видимою, а в її заголовку —
        // витрачені кошти й поточні ватти. Вимірювання при цьому не зупиняється.
        WindowState = WindowState.Minimized;
        TrayLog("minimize");
        SaveSettings();
        _sessionStateStore.Save(_session);
    }

    private void ResetSession()
    {
        lock (_sessionLock)
        {
            _sessionStateStore.Reset();
            _session = new EnergySession();
            _sessionStateStore.Save(_session);
        }

        _kwh.Text = $"{0d:0.000000} kWh";
        _cost.Text = $"{0d:0.000000} €";
        UpdateTrayAndTaskbar(null);
    }

    private void ExitApplication()
    {
        _isExiting = true;
        ShutdownServices();
        _desktop.Shutdown();
    }

    private void ShutdownServices()
    {
        if (_servicesStopped)
        {
            return;
        }

        _servicesStopped = true;
        _timer.Stop();

        // Дочекатися фонового читача, щоб не закрити монітор посеред Accept/Update.
        var waitUntil = DateTime.UtcNow.AddSeconds(2);
        while (_reading && DateTime.UtcNow < waitUntil)
        {
            Thread.Sleep(20);
        }

        SaveSettings();
        lock (_sessionLock)
        {
            _sessionStateStore.Save(_session);
            _energyHistoryStore.Save(_energyHistory);
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        _monitor.Dispose();
    }

    private void UpdateTrayAndTaskbar(double? watts)
    {
        double kwh;
        decimal cost;
        lock (_sessionLock)
        {
            kwh = _session.TotalKwh;
            cost = _session.TotalCost(_settings.EurPerKwh);
        }

        // Заголовок вікна = текст кнопки на панелі задач. Кошти ведуть, щоб суму було видно одразу,
        // навіть коли вікно згорнуте: кнопка показує початок заголовка, наведення — повний.
        var wattsShort = watts is null ? "—" : $"{watts:0} W";
        Title = $"{cost:0.0000} € · {wattsShort} — Pc Energy Meter";

        if (_trayIcon is null)
        {
            return;
        }

        var current = watts is null ? "no data" : $"{watts:0.0} W";
        var spend = $"Session spend: {cost:0.000000} €";
        _trayIcon.ToolTipText =
            $"Pc Energy Meter{Environment.NewLine}" +
            $"{spend}{Environment.NewLine}" +
            $"Current: {current}, consumed {kwh:0.000000} kWh";

        if (_traySpendItem is not null)
        {
            _traySpendItem.Header = spend;
        }
    }

    private static void TrayLog(string message)
    {
        // Тимчасовий журнал поведінки трею. WSL не показує область сповіщень, тож це єдиний спосіб
        // побачити, чи дійшов клік і чи відпрацював показ на реальному Windows. Після підтвердження прибрати.
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PcEnergyMeter");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "tray.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Журнал трею не критичний — ігноруємо помилки запису.
        }
    }

    private static WindowIcon? LoadAppIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        return File.Exists(path) ? new WindowIcon(path) : null;
    }

    private void SaveSettings()
    {
        if (_tariff.Value is { } value)
        {
            _settings.EurPerKwh = value;
        }

        _settings.StartWithWindows = _startWithWindows.IsChecked == true;
        _settings.LogCsv = _csvLogging.IsChecked == true;
        _settings.StartMinimizedToTray = _startMinimizedToTray.IsChecked == true;
        _settings.CloseToTray = _closeToTray.IsChecked == true;
        _settingsStore.Save(_settings);
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private static Border Card(Control content)
    {
        return new Border
        {
            Background = Surface,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = content
        };
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.Bold,
        Foreground = TextPrimary,
        Margin = new Thickness(0, 0, 0, 12)
    };

    private static T Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static void AddRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }
}
