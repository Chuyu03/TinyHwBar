using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TinyHwBar
{
    internal enum GpuDisplayMode
    {
        Available,
        Eco,
        Unavailable
    }

    internal sealed class HardwareSnapshot
    {
        internal int? CpuPercent { get; set; }
        internal int? MemoryPercent { get; set; }
        internal GpuDisplayMode GpuMode { get; set; }
        internal int? GpuPercent { get; set; }
        internal int? VideoMemoryPercent { get; set; }
        internal int? TemperatureCelsius { get; set; }

        internal string ToDisplayText()
        {
            string cpu = FormatPercent(CpuPercent);
            string memory = FormatPercent(MemoryPercent);
            string gpu;
            string videoMemory;
            string temperature;

            if (GpuMode == GpuDisplayMode.Eco)
            {
                gpu = "ECO";
                videoMemory = "--";
                temperature = "--°";
            }
            else if (GpuMode == GpuDisplayMode.Available)
            {
                gpu = FormatPercent(GpuPercent);
                videoMemory = FormatPercent(VideoMemoryPercent);
                temperature = TemperatureCelsius.HasValue
                    ? TemperatureCelsius.Value.ToString(CultureInfo.InvariantCulture) + "°"
                    : "--°";
            }
            else
            {
                gpu = "--";
                videoMemory = "--";
                temperature = "--°";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "CPU {0} · RAM {1} · GPU {2} · VR {3} · {4}",
                cpu,
                memory,
                gpu,
                videoMemory,
                temperature);
        }

        private static string FormatPercent(int? value)
        {
            return value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture) + "%"
                : "--";
        }
    }

    internal sealed class HardwareSampler : IDisposable
    {
        private const int NvmlSuccess = 0;
        private const uint NvmlTemperatureGpu = 0;
        private const string PreferredGpuName = "NVIDIA GeForce RTX 5070 Ti Laptop GPU";

        private readonly object synchronization = new object();

        private bool disposed;
        private bool hasCpuBaseline;
        private ulong previousIdleTime;
        private ulong previousKernelTime;
        private ulong previousUserTime;

        private byte previousAcLineStatus = byte.MaxValue;
        private bool nvmlInitialized;
        private IntPtr selectedGpu = IntPtr.Zero;
        private DateTime nextNvmlRetryUtc = DateTime.MinValue;

        internal HardwareSnapshot Sample()
        {
            lock (synchronization)
            {
                HardwareSnapshot snapshot = new HardwareSnapshot();

                if (disposed)
                {
                    snapshot.GpuMode = GpuDisplayMode.Unavailable;
                    return snapshot;
                }

                byte acLineStatus = ReadAcLineStatus();
                snapshot.CpuPercent = SampleCpuPercent();
                snapshot.MemoryPercent = SampleMemoryPercent();
                SampleGpu(acLineStatus, snapshot);

                return snapshot;
            }
        }

        public void Dispose()
        {
            lock (synchronization)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                ShutdownNvmlSafely();
            }
        }

        private static byte ReadAcLineStatus()
        {
            try
            {
                NativeMethods.SystemPowerStatus status;
                return NativeMethods.GetSystemPowerStatus(out status)
                    ? status.AcLineStatus
                    : byte.MaxValue;
            }
            catch (Exception)
            {
                return byte.MaxValue;
            }
        }

        private int? SampleCpuPercent()
        {
            try
            {
                NativeMethods.FileTime idle;
                NativeMethods.FileTime kernel;
                NativeMethods.FileTime user;

                if (!NativeMethods.GetSystemTimes(out idle, out kernel, out user))
                {
                    return null;
                }

                ulong idleTime = idle.ToUInt64();
                ulong kernelTime = kernel.ToUInt64();
                ulong userTime = user.ToUInt64();

                if (!hasCpuBaseline)
                {
                    StoreCpuBaseline(idleTime, kernelTime, userTime);
                    return null;
                }

                if (idleTime < previousIdleTime ||
                    kernelTime < previousKernelTime ||
                    userTime < previousUserTime)
                {
                    StoreCpuBaseline(idleTime, kernelTime, userTime);
                    return null;
                }

                ulong idleDelta = idleTime - previousIdleTime;
                ulong kernelDelta = kernelTime - previousKernelTime;
                ulong userDelta = userTime - previousUserTime;

                StoreCpuBaseline(idleTime, kernelTime, userTime);

                ulong totalDelta = kernelDelta + userDelta;
                if (totalDelta == 0)
                {
                    return null;
                }

                double busyDelta = idleDelta >= totalDelta
                    ? 0.0
                    : totalDelta - idleDelta;
                double percentage = busyDelta * 100.0 / totalDelta;

                return ClampPercentage((int)Math.Round(
                    percentage,
                    MidpointRounding.AwayFromZero));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void StoreCpuBaseline(ulong idleTime, ulong kernelTime, ulong userTime)
        {
            previousIdleTime = idleTime;
            previousKernelTime = kernelTime;
            previousUserTime = userTime;
            hasCpuBaseline = true;
        }

        private static int? SampleMemoryPercent()
        {
            try
            {
                NativeMethods.MemoryStatusEx status = new NativeMethods.MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(NativeMethods.MemoryStatusEx));

                if (!NativeMethods.GlobalMemoryStatusEx(ref status))
                {
                    return null;
                }

                return ClampPercentage((int)status.MemoryLoad);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void SampleGpu(byte acLineStatus, HardwareSnapshot snapshot)
        {
            bool onAcPower = acLineStatus == 1;

            if (!onAcPower)
            {
                if (nvmlInitialized)
                {
                    ShutdownNvmlSafely();
                }

                previousAcLineStatus = acLineStatus;
                snapshot.GpuMode = GpuDisplayMode.Eco;
                return;
            }

            if (previousAcLineStatus != 1)
            {
                nextNvmlRetryUtc = DateTime.MinValue;
            }

            previousAcLineStatus = 1;

            if (!nvmlInitialized && !TryInitializeNvml())
            {
                snapshot.GpuMode = GpuDisplayMode.Unavailable;
                return;
            }

            try
            {
                NativeMethods.NvmlUtilization utilization;
                NativeMethods.NvmlMemoryV2 memory = NativeMethods.NvmlMemoryV2.Create();
                uint temperature;

                if (NativeMethods.NvmlDeviceGetUtilizationRates(selectedGpu, out utilization) != NvmlSuccess ||
                    NativeMethods.NvmlDeviceGetMemoryInfoV2(selectedGpu, ref memory) != NvmlSuccess ||
                    NativeMethods.NvmlDeviceGetTemperature(
                        selectedGpu,
                        NvmlTemperatureGpu,
                        out temperature) != NvmlSuccess ||
                    memory.Total == 0)
                {
                    MarkNvmlFailure();
                    snapshot.GpuMode = GpuDisplayMode.Unavailable;
                    return;
                }

                snapshot.GpuMode = GpuDisplayMode.Available;
                snapshot.GpuPercent = ClampPercentage((int)utilization.Gpu);
                snapshot.VideoMemoryPercent = ClampPercentage((int)Math.Round(
                    memory.Used * 100.0 / memory.Total,
                    MidpointRounding.AwayFromZero));
                snapshot.TemperatureCelsius = temperature > int.MaxValue
                    ? (int?)null
                    : (int)temperature;
            }
            catch (Exception)
            {
                MarkNvmlFailure();
                snapshot.GpuMode = GpuDisplayMode.Unavailable;
            }
        }

        private bool TryInitializeNvml()
        {
            DateTime now = DateTime.UtcNow;
            if (now < nextNvmlRetryUtc)
            {
                return false;
            }

            try
            {
                if (NativeMethods.NvmlInitV2() != NvmlSuccess)
                {
                    nextNvmlRetryUtc = now.AddSeconds(30);
                    return false;
                }

                nvmlInitialized = true;

                uint deviceCount;
                if (NativeMethods.NvmlDeviceGetCountV2(out deviceCount) != NvmlSuccess || deviceCount == 0)
                {
                    MarkNvmlFailure();
                    return false;
                }

                IntPtr firstAvailableGpu = IntPtr.Zero;
                IntPtr preferredGpu = IntPtr.Zero;

                for (uint index = 0; index < deviceCount; index++)
                {
                    IntPtr device;
                    if (NativeMethods.NvmlDeviceGetHandleByIndexV2(index, out device) != NvmlSuccess ||
                        device == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (firstAvailableGpu == IntPtr.Zero)
                    {
                        firstAvailableGpu = device;
                    }

                    StringBuilder name = new StringBuilder(128);
                    if (NativeMethods.NvmlDeviceGetName(device, name, (uint)name.Capacity) == NvmlSuccess &&
                        string.Equals(
                            name.ToString(),
                            PreferredGpuName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        preferredGpu = device;
                        break;
                    }
                }

                selectedGpu = preferredGpu != IntPtr.Zero ? preferredGpu : firstAvailableGpu;
                if (selectedGpu == IntPtr.Zero)
                {
                    MarkNvmlFailure();
                    return false;
                }

                nextNvmlRetryUtc = DateTime.MinValue;
                return true;
            }
            catch (Exception)
            {
                MarkNvmlFailure();
                return false;
            }
        }

        private void MarkNvmlFailure()
        {
            ShutdownNvmlSafely();
            nextNvmlRetryUtc = DateTime.UtcNow.AddSeconds(30);
        }

        private void ShutdownNvmlSafely()
        {
            if (nvmlInitialized)
            {
                try
                {
                    NativeMethods.NvmlShutdown();
                }
                catch (Exception)
                {
                    // NVML failures must not terminate the monitor.
                }
            }

            nvmlInitialized = false;
            selectedGpu = IntPtr.Zero;
        }

        private static int ClampPercentage(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            return value > 100 ? 100 : value;
        }
    }
}
