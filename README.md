# PC Energy Meter

A Windows desktop app that tracks your computer's electricity consumption and cost in real time.

## What it does

- Reads CPU, GPU, RAM, storage, and USB power from available hardware sensors
- Shows current draw in watts with a per-category breakdown
- Displays power and voltage charts over the last 120 seconds
- Reads DDC/CI monitor data: brightness and power state
- Tracks kWh and cost for the current session
- Accumulates daily, weekly, and monthly energy history
- Projects cost for the next hour, day, and 30 days based on current draw
- Stores per-day min/max watt ranges and 24-hour voltage extremes
- Writes CSV logs to `%LOCALAPPDATA%\PcEnergyMeter\`
- Runs the measuring loop in the background; the taskbar button shows live cost and watts
- Starts minimized to the taskbar by default; closing hides to tray rather than stopping measurements
- Supports Windows startup via a scheduled task with elevated privileges

## Requirements

- Windows 10 or later (64-bit)
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64)
- Administrator rights for CPU/GPU power sensor access via LibreHardwareMonitor

Without administrator rights the app falls back to load-based estimates for CPU and GPU.

## Running

```
dotnet PcEnergyMeter.dll
```

Or build a self-contained executable (see **Building** below). The app requests UAC elevation on launch because LibreHardwareMonitor needs kernel-level driver access to read power sensors.

## Taskbar and tray

The taskbar button title shows session cost and current watts (`0.0042 € · 65 W — Pc Energy Meter`) so you can check the number at a glance while the window is minimized.

Closing the window hides it to the taskbar rather than stopping measurements. To exit the process, use **Exit** in the tray menu or uncheck **X minimizes to tray** in settings.

Tray menu items:

- Open
- Hide
- Reset session
- Exit

## Log files

| File | Updated | Content |
|---|---|---|
| `measurements_v2.csv` | every 15 s | total watts, kWh, cost, voltage, DDC/CI monitor count |
| `latest_breakdown.csv` | every 5 s | per-category snapshot |
| `latest_sensors.csv` | every 5 s | all raw sensors |
| `latest_monitors.csv` | every 5 s | DDC/CI brightness and power state |
| `session.json` | every sample | session start time, accumulated kWh |
| `energy_history.json` | every sample | daily kWh and watt ranges |

`measurements_v2.csv` rotates to `measurements_v2.csv.1` when it exceeds 5 MB.

## Tariff

The default tariff field is editable in the Settings panel. Enter your electricity rate in €/kWh to match your contract. Cost figures update on the next sensor tick when the value changes.

## Limitations

Windows does not expose mains voltage or total PSU draw. The app reads only what hardware sensors report through LibreHardwareMonitor and WMI.

**CPU package power**: some AMD Ryzen 7000-series boards do not expose a power table that LibreHardwareMonitor can map. On those boards the CPU power sensor returns 0 W regardless of elevation or driver version. The app displays a load-based estimate in that case and labels it as such.

**Monitor power**: DDC/CI reports brightness as a percentage, not watts. The app interpolates between configurable min/max watt values and labels the result as an estimate. It does not measure actual monitor power consumption.

For exact wall-socket numbers, use an external wattmeter or a UPS with USB telemetry.

## Building

```bash
dotnet build PcEnergyMeter.sln

dotnet test tests/PcEnergyMeter.Tests/PcEnergyMeter.Tests.csproj

# Framework-dependent (requires .NET 8 Runtime installed on target machine)
dotnet publish src/PcEnergyMeter.App/PcEnergyMeter.App.csproj \
  -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=false -o dist/win-x64-dotnet

# Self-contained single file
dotnet publish src/PcEnergyMeter.App/PcEnergyMeter.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishReadyToRun=false -o dist/win-x64
```

Building on Linux/WSL requires the `EnableWindowsTargeting` MSBuild property:

```bash
dotnet build PcEnergyMeter.sln -p:EnableWindowsTargeting=true
```

## Tech stack

- [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) / C#
- [Avalonia UI](https://avaloniaui.net/) for the desktop window
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) for sensor reads
- WMI for hardware profile (CPU name, memory, storage, USB devices)
- DDC/CI via `dxva2.dll` for monitor brightness and power state

## License

MIT
