using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TinyHwBar
{
    internal static class NativeMethods
    {
        internal const int GwlExStyle = -20;
        internal const long WsExTransparent = 0x00000020L;
        internal const int WsExToolWindow = 0x00000080;
        internal const int WsExNoActivate = 0x08000000;

        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoZOrder = 0x0004;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpFrameChanged = 0x0020;

        internal const int WmDpiChanged = 0x02E0;
        internal const int WmDisplayChange = 0x007E;

        private const string NvmlPath = "nvml.dll";

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            internal uint LowDateTime;
            internal uint HighDateTime;

            internal ulong ToUInt64()
            {
                return ((ulong)HighDateTime << 32) | LowDateTime;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct MemoryStatusEx
        {
            internal uint Length;
            internal uint MemoryLoad;
            internal ulong TotalPhysical;
            internal ulong AvailablePhysical;
            internal ulong TotalPageFile;
            internal ulong AvailablePageFile;
            internal ulong TotalVirtual;
            internal ulong AvailableVirtual;
            internal ulong AvailableExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SystemPowerStatus
        {
            internal byte AcLineStatus;
            internal byte BatteryFlag;
            internal byte BatteryLifePercent;
            internal byte SystemStatusFlag;
            internal uint BatteryLifeTime;
            internal uint BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlUtilization
        {
            internal uint Gpu;
            internal uint Memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlMemoryV2
        {
            internal uint Version;
            internal ulong Total;
            internal ulong Reserved;
            internal ulong Free;
            internal ulong Used;

            internal static NvmlMemoryV2 Create()
            {
                NvmlMemoryV2 memory = new NvmlMemoryV2();
                memory.Version = (uint)(Marshal.SizeOf(typeof(NvmlMemoryV2)) | (2 << 24));
                return memory;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(
            out FileTime idleTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SystemPowerStatus powerStatus);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr windowHandle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(NvmlPath, EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvmlInitV2();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(NvmlPath, EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvmlShutdown();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(NvmlPath, EntryPoint = "nvmlDeviceGetCount_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvmlDeviceGetCountV2(out uint deviceCount);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(NvmlPath, EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvmlDeviceGetHandleByIndexV2(uint index, out IntPtr device);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(
            NvmlPath,
            EntryPoint = "nvmlDeviceGetName",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        internal static extern int NvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(NvmlPath, EntryPoint = "nvmlDeviceGetUtilizationRates", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvmlDeviceGetUtilizationRates(
            IntPtr device,
            out NvmlUtilization utilization);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(NvmlPath, EntryPoint = "nvmlDeviceGetMemoryInfo_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvmlDeviceGetMemoryInfoV2(IntPtr device, ref NvmlMemoryV2 memory);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(NvmlPath, EntryPoint = "nvmlDeviceGetTemperature", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvmlDeviceGetTemperature(
            IntPtr device,
            uint sensorType,
            out uint temperature);
    }
}
