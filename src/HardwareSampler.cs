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
        internal HardwareSnapshot()
        {
            SampledAtUtc = DateTime.UtcNow;
            GpuMode = GpuDisplayMode.Unavailable;
            NetworkSelectionStatus = NetworkSelectionStatus.NoRoute;
            GatewayLatencyStatus = GatewayLatencyStatus.Disabled;
            DiscreteAdapterStatus = IntelGpuDataStatus.NotFound;
            DiscreteUtilizationStatus = IntelGpuDataStatus.NotFound;
            DiscreteMemoryStatus = IntelGpuDataStatus.NotFound;
            DiscreteTemperatureStatus = IntelGpuDataStatus.Unsupported;
            IntegratedAdapterStatus = IntelGpuDataStatus.NotFound;
            IntegratedUtilizationStatus = IntelGpuDataStatus.NotFound;
            IntegratedMemoryStatus = IntelGpuDataStatus.NotFound;
            IntegratedTemperatureStatus = IntelGpuDataStatus.Unsupported;
        }

        internal DateTime SampledAtUtc { get; set; }
        internal int? CpuPercent { get; set; }
        internal int? MemoryPercent { get; set; }
        internal GpuDisplayMode GpuMode { get; set; }
        internal int? GpuPercent { get; set; }
        internal int? VideoMemoryPercent { get; set; }
        internal int? TemperatureCelsius { get; set; }
        internal bool DiscreteGpuDetected { get; set; }
        internal string DiscreteGpuName { get; set; }
        internal uint? DiscreteGpuVendorId { get; set; }
        internal long? DiscreteMemoryBytes { get; set; }
        internal long? DiscreteMemoryLimitBytes { get; set; }
        internal IntelGpuDataStatus DiscreteAdapterStatus { get; set; }
        internal IntelGpuDataStatus DiscreteUtilizationStatus { get; set; }
        internal IntelGpuDataStatus DiscreteMemoryStatus { get; set; }
        internal IntelGpuDataStatus DiscreteTemperatureStatus { get; set; }
        internal long? NetworkReceiveBytesPerSecond { get; set; }
        internal long? NetworkSendBytesPerSecond { get; set; }
        internal long? NetworkLinkSpeedBitsPerSecond { get; set; }
        internal string NetworkAdapterName { get; set; }
        internal string NetworkGatewayAddress { get; set; }
        internal NetworkSelectionStatus NetworkSelectionStatus { get; set; }
        internal long? GatewayLatencyMilliseconds { get; set; }
        internal GatewayLatencyStatus GatewayLatencyStatus { get; set; }
        internal bool IntegratedGpuDetected { get; set; }
        internal string IntegratedGpuName { get; set; }
        internal uint? IntegratedGpuVendorId { get; set; }
        internal int? IntegratedGpuPercent { get; set; }
        internal long? IntegratedDedicatedMemoryBytes { get; set; }
        internal long? IntegratedDedicatedMemoryLimitBytes { get; set; }
        internal long? IntegratedSharedMemoryBytes { get; set; }
        internal long? IntegratedSharedMemoryLimitBytes { get; set; }
        internal IntelGpuDataStatus IntegratedAdapterStatus { get; set; }
        internal IntelGpuDataStatus IntegratedUtilizationStatus { get; set; }
        internal IntelGpuDataStatus IntegratedMemoryStatus { get; set; }
        internal IntelGpuDataStatus IntegratedTemperatureStatus { get; set; }

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

        private readonly object synchronization = new object();
        private readonly NetworkSampler networkSampler;
        private readonly GatewayLatencySampler gatewayLatencySampler;
        private readonly GpuRoleSampler discreteGpuSampler;
        private readonly GpuRoleSampler integratedGpuSampler;

        private bool disposed;
        private bool hasCpuBaseline;
        private ulong previousIdleTime;
        private ulong previousKernelTime;
        private ulong previousUserTime;

        private byte previousAcLineStatus = byte.MaxValue;
        private bool nvmlInitialized;
        private IntPtr selectedGpu = IntPtr.Zero;
        private string selectedNvmlAdapterName = string.Empty;
        private uint? selectedNvmlAdapterLuidLowPart;
        private int? selectedNvmlAdapterLuidHighPart;
        private DateTime nextNvmlRetryUtc = DateTime.MinValue;

        internal HardwareSampler()
        {
            networkSampler = new NetworkSampler();
            gatewayLatencySampler = new GatewayLatencySampler();
            discreteGpuSampler = new GpuRoleSampler(GpuAdapterRole.Discrete);
            integratedGpuSampler = new GpuRoleSampler(GpuAdapterRole.Integrated);
        }

        internal void SetGatewayLatencyEnabled(bool enabled)
        {
            lock (synchronization)
            {
                if (!disposed)
                {
                    gatewayLatencySampler.SetEnabled(enabled);
                }
            }
        }

        internal HardwareSnapshot Sample()
        {
            lock (synchronization)
            {
                HardwareSnapshot snapshot = new HardwareSnapshot();

                if (disposed)
                {
                    snapshot.GpuMode = GpuDisplayMode.Unavailable;
                    snapshot.NetworkSelectionStatus = NetworkSelectionStatus.Disposed;
                    snapshot.SampledAtUtc = DateTime.UtcNow;
                    return snapshot;
                }

                byte acLineStatus = ReadAcLineStatus();
                snapshot.CpuPercent = SampleCpuPercent();
                snapshot.MemoryPercent = SampleMemoryPercent();
                SampleDiscreteGpu(acLineStatus, snapshot);
                SampleNetwork(snapshot);
                SampleIntegratedGpu(snapshot);

                snapshot.SampledAtUtc = DateTime.UtcNow;
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
                networkSampler.Dispose();
                gatewayLatencySampler.Dispose();
                discreteGpuSampler.Dispose();
                integratedGpuSampler.Dispose();
                ShutdownNvmlSafely();
            }
        }

        private void SampleNetwork(HardwareSnapshot snapshot)
        {
            try
            {
                NetworkMetrics metrics = networkSampler.Sample();
                snapshot.NetworkSelectionStatus = metrics.SelectionStatus;
                if (metrics.IsAvailable)
                {
                    snapshot.NetworkReceiveBytesPerSecond = metrics.ReceivedBytesPerSecond;
                    snapshot.NetworkSendBytesPerSecond = metrics.SentBytesPerSecond;
                    snapshot.NetworkLinkSpeedBitsPerSecond = metrics.LinkSpeedBitsPerSecond;
                    snapshot.NetworkAdapterName = metrics.InterfaceName;
                    snapshot.NetworkGatewayAddress = metrics.DefaultGatewayAddress;
                }

                GatewayLatencyMetrics latency = gatewayLatencySampler.Sample(metrics);
                snapshot.GatewayLatencyMilliseconds = latency.RoundtripTimeMilliseconds;
                snapshot.GatewayLatencyStatus = latency.Status;
            }
            catch (Exception)
            {
                snapshot.NetworkSelectionStatus = NetworkSelectionStatus.CounterUnavailable;
                snapshot.GatewayLatencyMilliseconds = null;
                snapshot.GatewayLatencyStatus = gatewayLatencySampler.IsEnabled
                    ? GatewayLatencyStatus.Failed
                    : GatewayLatencyStatus.Disabled;
                // Network counter failures must not affect hardware monitoring.
            }
        }

        private void SampleIntegratedGpu(HardwareSnapshot snapshot)
        {
            try
            {
                ApplyIntegratedGpuMetrics(snapshot, integratedGpuSampler.Sample());
            }
            catch (Exception)
            {
                snapshot.IntegratedGpuDetected = false;
                snapshot.IntegratedAdapterStatus = IntelGpuDataStatus.SampleFailed;
                snapshot.IntegratedUtilizationStatus = IntelGpuDataStatus.SampleFailed;
                snapshot.IntegratedMemoryStatus = IntelGpuDataStatus.SampleFailed;
                snapshot.IntegratedTemperatureStatus = IntelGpuDataStatus.Unsupported;
                // Integrated GPU failures must not affect the other metrics.
            }
        }

        internal static void ApplyIntegratedGpuMetrics(
            HardwareSnapshot snapshot,
            IntelGpuMetrics metrics)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            if (metrics == null)
            {
                throw new ArgumentNullException("metrics");
            }

            snapshot.IntegratedGpuDetected = false;
            snapshot.IntegratedGpuName = null;
            snapshot.IntegratedGpuVendorId = null;
            snapshot.IntegratedGpuPercent = null;
            snapshot.IntegratedDedicatedMemoryBytes = null;
            snapshot.IntegratedDedicatedMemoryLimitBytes = null;
            snapshot.IntegratedSharedMemoryBytes = null;
            snapshot.IntegratedSharedMemoryLimitBytes = null;
            snapshot.IntegratedGpuDetected = metrics.IsAvailable;
            snapshot.IntegratedAdapterStatus = metrics.AdapterStatus;
            snapshot.IntegratedUtilizationStatus = metrics.UtilizationStatus;
            snapshot.IntegratedMemoryStatus = metrics.MemoryStatus;
            snapshot.IntegratedTemperatureStatus = metrics.TemperatureStatus;

            if (!metrics.IsAvailable)
            {
                return;
            }

            snapshot.IntegratedGpuName = metrics.AdapterName;
            snapshot.IntegratedGpuVendorId = metrics.AdapterVendorId;
            snapshot.IntegratedGpuPercent = metrics.UtilizationPercent;
            snapshot.IntegratedDedicatedMemoryBytes = metrics.DedicatedMemoryUsageBytes;
            snapshot.IntegratedDedicatedMemoryLimitBytes = metrics.DedicatedMemoryLimitBytes;
            snapshot.IntegratedSharedMemoryBytes = metrics.SharedMemoryUsageBytes;
            snapshot.IntegratedSharedMemoryLimitBytes = metrics.SharedMemoryLimitBytes;
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

        private void SampleDiscreteGpu(byte acLineStatus, HardwareSnapshot snapshot)
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

            try
            {
                IntelGpuMetrics metrics = discreteGpuSampler.Sample();
                ApplyDiscreteGpuMetrics(snapshot, metrics);

                if (!metrics.IsAvailable ||
                    !metrics.AdapterVendorId.HasValue ||
                    metrics.AdapterVendorId.Value != GpuRoleSampler.NvidiaVendorId)
                {
                    ShutdownNvmlSafely();
                    return;
                }

                SampleNvmlEnhancement(metrics, snapshot);
            }
            catch (Exception)
            {
                snapshot.DiscreteGpuDetected = false;
                snapshot.GpuMode = GpuDisplayMode.Unavailable;
                snapshot.DiscreteAdapterStatus = IntelGpuDataStatus.SampleFailed;
                snapshot.DiscreteUtilizationStatus = IntelGpuDataStatus.SampleFailed;
                snapshot.DiscreteMemoryStatus = IntelGpuDataStatus.SampleFailed;
                snapshot.DiscreteTemperatureStatus = IntelGpuDataStatus.Unsupported;
                ShutdownNvmlSafely();
            }
        }

        internal static void ApplyDiscreteGpuMetrics(
            HardwareSnapshot snapshot,
            IntelGpuMetrics metrics)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            if (metrics == null)
            {
                throw new ArgumentNullException("metrics");
            }

            snapshot.DiscreteGpuDetected = false;
            snapshot.DiscreteGpuName = null;
            snapshot.DiscreteGpuVendorId = null;
            snapshot.DiscreteMemoryBytes = null;
            snapshot.DiscreteMemoryLimitBytes = null;
            snapshot.GpuPercent = null;
            snapshot.VideoMemoryPercent = null;
            snapshot.TemperatureCelsius = null;
            snapshot.DiscreteGpuDetected = metrics.IsAvailable;
            snapshot.DiscreteAdapterStatus = metrics.AdapterStatus;
            snapshot.DiscreteUtilizationStatus = metrics.UtilizationStatus;
            snapshot.DiscreteMemoryStatus = metrics.MemoryStatus;
            snapshot.DiscreteTemperatureStatus = metrics.TemperatureStatus;

            if (!metrics.IsAvailable)
            {
                snapshot.GpuMode = GpuDisplayMode.Unavailable;
                return;
            }

            snapshot.DiscreteGpuName = metrics.AdapterName;
            snapshot.DiscreteGpuVendorId = metrics.AdapterVendorId;
            snapshot.DiscreteMemoryBytes = metrics.DedicatedMemoryUsageBytes;
            snapshot.DiscreteMemoryLimitBytes = metrics.DedicatedMemoryLimitBytes;
            snapshot.GpuMode = GpuDisplayMode.Available;
            snapshot.GpuPercent = metrics.UtilizationPercent;
            snapshot.VideoMemoryPercent = CalculatePercentage(
                metrics.DedicatedMemoryUsageBytes,
                metrics.DedicatedMemoryLimitBytes);
        }

        private void SampleNvmlEnhancement(
            IntelGpuMetrics metrics,
            HardwareSnapshot snapshot)
        {
            if (!TryInitializeNvml(metrics))
            {
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
                    return;
                }

                if (!snapshot.GpuPercent.HasValue)
                {
                    snapshot.GpuPercent = ClampPercentage((int)utilization.Gpu);
                }

                if (!snapshot.VideoMemoryPercent.HasValue)
                {
                    snapshot.VideoMemoryPercent = ClampPercentage((int)Math.Round(
                        memory.Used * 100.0 / memory.Total,
                        MidpointRounding.AwayFromZero));
                }

                snapshot.TemperatureCelsius = temperature > int.MaxValue
                    ? (int?)null
                    : (int)temperature;
                snapshot.DiscreteTemperatureStatus = snapshot.TemperatureCelsius.HasValue
                    ? IntelGpuDataStatus.Available
                    : IntelGpuDataStatus.SampleFailed;
            }
            catch (Exception)
            {
                MarkNvmlFailure();
            }
        }

        private bool TryInitializeNvml(IntelGpuMetrics metrics)
        {
            if (metrics == null ||
                string.IsNullOrWhiteSpace(metrics.AdapterName) ||
                !metrics.AdapterLuidLowPart.HasValue ||
                !metrics.AdapterLuidHighPart.HasValue)
            {
                return false;
            }

            if (nvmlInitialized)
            {
                if (string.Equals(
                        selectedNvmlAdapterName,
                        metrics.AdapterName,
                        StringComparison.OrdinalIgnoreCase) &&
                    selectedNvmlAdapterLuidLowPart == metrics.AdapterLuidLowPart &&
                    selectedNvmlAdapterLuidHighPart == metrics.AdapterLuidHighPart)
                {
                    return true;
                }

                ShutdownNvmlSafely();
            }

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

                IntPtr matchingGpu = IntPtr.Zero;
                int matchingGpuCount = 0;

                for (uint index = 0; index < deviceCount; index++)
                {
                    IntPtr device;
                    if (NativeMethods.NvmlDeviceGetHandleByIndexV2(index, out device) != NvmlSuccess ||
                        device == IntPtr.Zero)
                    {
                        continue;
                    }

                    StringBuilder name = new StringBuilder(128);
                    if (NativeMethods.NvmlDeviceGetName(device, name, (uint)name.Capacity) == NvmlSuccess &&
                        string.Equals(
                            name.ToString().Trim(),
                            metrics.AdapterName.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        matchingGpu = device;
                        matchingGpuCount++;
                    }
                }

                if (matchingGpuCount != 1 || matchingGpu == IntPtr.Zero)
                {
                    MarkNvmlFailure();
                    return false;
                }

                selectedGpu = matchingGpu;
                selectedNvmlAdapterName = metrics.AdapterName.Trim();
                selectedNvmlAdapterLuidLowPart = metrics.AdapterLuidLowPart;
                selectedNvmlAdapterLuidHighPart = metrics.AdapterLuidHighPart;
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
            selectedNvmlAdapterName = string.Empty;
            selectedNvmlAdapterLuidLowPart = null;
            selectedNvmlAdapterLuidHighPart = null;
        }

        private static int? CalculatePercentage(long? usage, long? limit)
        {
            if (!usage.HasValue || !limit.HasValue || limit.Value <= 0)
            {
                return null;
            }

            return ClampPercentage((int)Math.Round(
                usage.Value * 100.0 / limit.Value,
                MidpointRounding.AwayFromZero));
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
