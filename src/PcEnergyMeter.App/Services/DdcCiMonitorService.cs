using System.ComponentModel;
using System.Runtime.InteropServices;
using PcEnergyMeter.Core;

namespace PcEnergyMeter.App.Services;

public sealed class DdcCiMonitorService
{
    private const byte PowerModeVcpCode = 0xD6;

    public IReadOnlyList<MonitorReading> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<MonitorReading>();
        }

        var displayHandles = new List<nint>();
        if (!NativeMethods.EnumDisplayMonitors(
                nint.Zero,
                nint.Zero,
                (nint monitor, nint _, ref NativeMethods.Rect _, nint _) =>
                {
                    displayHandles.Add(monitor);
                    return true;
                },
                nint.Zero))
        {
            return Array.Empty<MonitorReading>();
        }

        var result = new List<MonitorReading>();
        var index = 1;
        foreach (var displayHandle in displayHandles)
        {
            result.AddRange(ReadPhysicalMonitors(displayHandle, ref index));
        }

        return result;
    }

    private static IReadOnlyList<MonitorReading> ReadPhysicalMonitors(nint displayHandle, ref int index)
    {
        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(displayHandle, out var count) || count == 0)
        {
            return Array.Empty<MonitorReading>();
        }

        var physicalMonitors = new NativeMethods.PhysicalMonitor[count];
        if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(displayHandle, count, physicalMonitors))
        {
            return Array.Empty<MonitorReading>();
        }

        var readings = new List<MonitorReading>();
        try
        {
            foreach (var physical in physicalMonitors)
            {
                readings.Add(ReadPhysicalMonitor(physical, index++));
            }
        }
        finally
        {
            NativeMethods.DestroyPhysicalMonitors(count, physicalMonitors);
        }

        return readings;
    }

    private static MonitorReading ReadPhysicalMonitor(NativeMethods.PhysicalMonitor physical, int index)
    {
        var notes = new List<string>();
        int? brightnessMinimum = null;
        int? brightnessCurrent = null;
        int? brightnessMaximum = null;
        double? brightnessPercent = null;
        int? powerModeCode = null;
        string? powerMode = null;
        bool? isPoweredOn = null;

        if (NativeMethods.GetMonitorBrightness(physical.Handle, out var minimum, out var current, out var maximum))
        {
            brightnessMinimum = checked((int)minimum);
            brightnessCurrent = checked((int)current);
            brightnessMaximum = checked((int)maximum);
            brightnessPercent = ToPercent(minimum, current, maximum);
        }
        else
        {
            notes.Add($"Brightness unavailable: {Win32Error()}.");
        }

        if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                physical.Handle,
                PowerModeVcpCode,
                out _,
                out var currentPowerMode,
                out _))
        {
            powerModeCode = checked((int)currentPowerMode);
            powerMode = DescribePowerMode(currentPowerMode);
            isPoweredOn = currentPowerMode == 1;
        }
        else
        {
            notes.Add($"VCP 0xD6 power state unavailable: {Win32Error()}.");
        }

        if (notes.Count == 0)
        {
            notes.Add("DDC/CI responded.");
        }

        return new MonitorReading(
            index,
            $"Display {index}",
            string.IsNullOrWhiteSpace(physical.Description) ? $"Monitor {index}" : physical.Description.Trim(),
            brightnessMinimum,
            brightnessCurrent,
            brightnessMaximum,
            brightnessPercent,
            powerModeCode,
            powerMode,
            isPoweredOn,
            notes);
    }

    private static double? ToPercent(uint minimum, uint current, uint maximum)
    {
        if (maximum <= minimum)
        {
            return null;
        }

        return Math.Clamp((current - minimum) * 100d / (maximum - minimum), 0d, 100d);
    }

    private static string DescribePowerMode(uint code)
    {
        return code switch
        {
            1 => "on",
            2 => "standby",
            3 => "sleep",
            4 => "off",
            5 => "off",
            _ => $"unknown ({code})"
        };
    }

    private static string Win32Error()
    {
        var error = Marshal.GetLastWin32Error();
        return error == 0 ? "no error code" : new Win32Exception(error).Message;
    }

    private static partial class NativeMethods
    {
        public delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(
            nint hdc,
            nint clipRect,
            MonitorEnumProc callback,
            nint data);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint numberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(
            nint monitor,
            uint physicalMonitorArraySize,
            [Out] PhysicalMonitor[] physicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyPhysicalMonitors(
            uint physicalMonitorArraySize,
            [In] PhysicalMonitor[] physicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorBrightness(
            nint monitor,
            out uint minimumBrightness,
            out uint currentBrightness,
            out uint maximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(
            nint monitor,
            byte vcpCode,
            out uint vcpCodeType,
            out uint currentValue,
            out uint maximumValue);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PhysicalMonitor
        {
            public nint Handle;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
        }
    }
}
