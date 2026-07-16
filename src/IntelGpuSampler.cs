using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;

namespace TinyHwBar
{
    internal enum IntelGpuDataStatus
    {
        Available,
        NotFound,
        AmbiguousAdapter,
        ProbeFailed,
        FirstSamplePending,
        CounterUnavailable,
        AccessDenied,
        NoMatchingInstances,
        SampleFailed,
        Unsupported,
        Disposed
    }

    internal enum IntelAdapterIntegrationKind
    {
        Integrated,
        Discrete,
        Unknown
    }

    internal enum GpuAdapterRole
    {
        Discrete,
        Integrated
    }

    internal sealed class IntelGpuMetrics
    {
        internal bool IsAvailable { get; private set; }
        internal bool PerformanceDataAvailable { get; private set; }
        internal string AdapterName { get; private set; }
        internal uint? AdapterVendorId { get; private set; }
        internal GpuAdapterRole AdapterRole { get; private set; }
        internal uint? AdapterLuidLowPart { get; private set; }
        internal int? AdapterLuidHighPart { get; private set; }
        internal int? UtilizationPercent { get; private set; }
        internal long? DedicatedMemoryUsageBytes { get; private set; }
        internal long? DedicatedMemoryLimitBytes { get; private set; }
        internal long? SharedMemoryUsageBytes { get; private set; }
        internal long? SharedMemoryLimitBytes { get; private set; }
        internal int? TemperatureCelsius { get; private set; }
        internal IntelGpuDataStatus AdapterStatus { get; private set; }
        internal IntelGpuDataStatus UtilizationStatus { get; private set; }
        internal IntelGpuDataStatus MemoryStatus { get; private set; }
        internal IntelGpuDataStatus TemperatureStatus { get; private set; }

        internal static IntelGpuMetrics CreateUnavailable()
        {
            return CreateUnavailable(IntelGpuDataStatus.NotFound);
        }

        internal static IntelGpuMetrics CreateUnavailable(
            IntelGpuDataStatus adapterStatus)
        {
            return CreateUnavailable(
                adapterStatus,
                GpuAdapterRole.Integrated);
        }

        internal static IntelGpuMetrics CreateUnavailable(
            IntelGpuDataStatus adapterStatus,
            GpuAdapterRole adapterRole)
        {
            return new IntelGpuMetrics
            {
                IsAvailable = false,
                PerformanceDataAvailable = false,
                AdapterName = string.Empty,
                AdapterVendorId = null,
                AdapterRole = adapterRole,
                AdapterLuidLowPart = null,
                AdapterLuidHighPart = null,
                UtilizationPercent = null,
                DedicatedMemoryUsageBytes = null,
                DedicatedMemoryLimitBytes = null,
                SharedMemoryUsageBytes = null,
                SharedMemoryLimitBytes = null,
                TemperatureCelsius = null,
                AdapterStatus = adapterStatus,
                UtilizationStatus = adapterStatus,
                MemoryStatus = adapterStatus,
                TemperatureStatus = IntelGpuDataStatus.Unsupported
            };
        }

        internal static IntelGpuMetrics CreateDetected(
            string adapterName,
            uint adapterLuidLowPart,
            int adapterLuidHighPart,
            long? sharedMemoryLimitBytes)
        {
            return CreateSampled(
                adapterName,
                0x8086,
                GpuAdapterRole.Integrated,
                adapterLuidLowPart,
                adapterLuidHighPart,
                null,
                null,
                null,
                null,
                sharedMemoryLimitBytes,
                IntelGpuDataStatus.FirstSamplePending,
                IntelGpuDataStatus.FirstSamplePending);
        }

        internal static IntelGpuMetrics CreateSampled(
            string adapterName,
            uint adapterLuidLowPart,
            int adapterLuidHighPart,
            int? utilizationPercent,
            long? dedicatedMemoryUsageBytes,
            long? sharedMemoryUsageBytes,
            long? sharedMemoryLimitBytes,
            IntelGpuDataStatus utilizationStatus,
            IntelGpuDataStatus memoryStatus)
        {
            return CreateSampled(
                adapterName,
                0x8086,
                GpuAdapterRole.Integrated,
                adapterLuidLowPart,
                adapterLuidHighPart,
                utilizationPercent,
                dedicatedMemoryUsageBytes,
                null,
                sharedMemoryUsageBytes,
                sharedMemoryLimitBytes,
                utilizationStatus,
                memoryStatus);
        }

        internal static IntelGpuMetrics CreateSampled(
            string adapterName,
            uint adapterVendorId,
            GpuAdapterRole adapterRole,
            uint adapterLuidLowPart,
            int adapterLuidHighPart,
            int? utilizationPercent,
            long? dedicatedMemoryUsageBytes,
            long? dedicatedMemoryLimitBytes,
            long? sharedMemoryUsageBytes,
            long? sharedMemoryLimitBytes,
            IntelGpuDataStatus utilizationStatus,
            IntelGpuDataStatus memoryStatus)
        {
            return new IntelGpuMetrics
            {
                IsAvailable = true,
                PerformanceDataAvailable =
                    utilizationStatus == IntelGpuDataStatus.Available ||
                    memoryStatus == IntelGpuDataStatus.Available,
                AdapterName = adapterName ?? string.Empty,
                AdapterVendorId = adapterVendorId,
                AdapterRole = adapterRole,
                AdapterLuidLowPart = adapterLuidLowPart,
                AdapterLuidHighPart = adapterLuidHighPart,
                UtilizationPercent = utilizationPercent,
                DedicatedMemoryUsageBytes = dedicatedMemoryUsageBytes,
                DedicatedMemoryLimitBytes = dedicatedMemoryLimitBytes,
                SharedMemoryUsageBytes = sharedMemoryUsageBytes,
                SharedMemoryLimitBytes = sharedMemoryLimitBytes,
                TemperatureCelsius = null,
                AdapterStatus = IntelGpuDataStatus.Available,
                UtilizationStatus = utilizationStatus,
                MemoryStatus = memoryStatus,
                TemperatureStatus = IntelGpuDataStatus.Unsupported
            };
        }

        internal IntelGpuMetrics Copy()
        {
            return new IntelGpuMetrics
            {
                IsAvailable = IsAvailable,
                PerformanceDataAvailable = PerformanceDataAvailable,
                AdapterName = AdapterName,
                AdapterVendorId = AdapterVendorId,
                AdapterRole = AdapterRole,
                AdapterLuidLowPart = AdapterLuidLowPart,
                AdapterLuidHighPart = AdapterLuidHighPart,
                UtilizationPercent = UtilizationPercent,
                DedicatedMemoryUsageBytes = DedicatedMemoryUsageBytes,
                DedicatedMemoryLimitBytes = DedicatedMemoryLimitBytes,
                SharedMemoryUsageBytes = SharedMemoryUsageBytes,
                SharedMemoryLimitBytes = SharedMemoryLimitBytes,
                TemperatureCelsius = TemperatureCelsius,
                AdapterStatus = AdapterStatus,
                UtilizationStatus = UtilizationStatus,
                MemoryStatus = MemoryStatus,
                TemperatureStatus = TemperatureStatus
            };
        }
    }

    internal sealed class IntelGpuSampler : IDisposable
    {
        private readonly GpuRoleSampler sampler =
            new GpuRoleSampler(GpuAdapterRole.Integrated);

        internal IntelGpuMetrics Sample()
        {
            return sampler.Sample();
        }

        public void Dispose()
        {
            sampler.Dispose();
        }

        internal static IntelGpuDataStatus ResolveIntegrationStatus(
            IntelAdapterIntegrationKind[] kinds)
        {
            return GpuRoleSampler.ResolveRoleStatus(
                GpuAdapterRole.Integrated,
                kinds);
        }
    }

    internal sealed class GpuRoleSampler : IDisposable
    {
        internal const uint NvidiaVendorId = 0x10DE;
        internal const uint AmdVendorId = 0x1002;
        internal const uint IntelVendorId = 0x8086;
        internal const uint DxgiAdapterFlagRemote = 0x1;
        internal const uint DxgiAdapterFlagSoftware = 0x2;
        private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
        private const int AccessDeniedHResult = unchecked((int)0x80070005);
        private const uint DxCoreAdapterPropertyIsHardware = 11;
        private const uint DxCoreAdapterPropertyIsIntegrated = 12;
        private const string GpuEngineCategoryName = "GPU Engine";
        private const string GpuUtilizationCounterName =
            "Utilization Percentage";
        private const string GpuAdapterMemoryCategoryName =
            "GPU Adapter Memory";
        private const string GpuDedicatedUsageCounterName = "Dedicated Usage";
        private const string GpuSharedUsageCounterName = "Shared Usage";

        private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

        private readonly object synchronization = new object();
        private readonly GpuAdapterRole role;
        private Dictionary<string, CounterSample> previousEngineSamples =
            new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);

        private bool disposed;
        private DateTime nextProbeUtc = DateTime.MinValue;
        private AdapterProbeResult cachedAdapterProbe =
            AdapterProbeResult.CreateUnavailable(IntelGpuDataStatus.NotFound);

        internal GpuRoleSampler(GpuAdapterRole role)
        {
            this.role = role;
        }

        internal IntelGpuMetrics Sample()
        {
            lock (synchronization)
            {
                if (disposed)
                {
                    return IntelGpuMetrics.CreateUnavailable(
                        IntelGpuDataStatus.Disposed,
                        role);
                }

                DateTime now = DateTime.UtcNow;
                if (now >= nextProbeUtc)
                {
                    AdapterProbeResult discoveredAdapter = DiscoverAdapter(role);
                    if (!IsSameAdapter(cachedAdapterProbe, discoveredAdapter))
                    {
                        previousEngineSamples.Clear();
                    }

                    cachedAdapterProbe = discoveredAdapter;
                    nextProbeUtc = now.Add(ProbeInterval);
                }

                if (cachedAdapterProbe.Adapter == null)
                {
                    return IntelGpuMetrics.CreateUnavailable(
                        cachedAdapterProbe.Status,
                        role);
                }

                GpuAdapterInfo adapter = cachedAdapterProbe.Adapter;
                UtilizationSample utilization = SampleUtilization(adapter);
                MemorySample memory = SampleMemory(adapter);

                return IntelGpuMetrics.CreateSampled(
                    adapter.Name,
                    adapter.VendorId,
                    role,
                    adapter.LuidLowPart,
                    adapter.LuidHighPart,
                    utilization.Percent,
                    memory.DedicatedUsageBytes,
                    adapter.DedicatedMemoryLimitBytes,
                    memory.SharedUsageBytes,
                    adapter.SharedMemoryLimitBytes,
                    utilization.Status,
                    memory.Status);
            }
        }

        public void Dispose()
        {
            lock (synchronization)
            {
                disposed = true;
                cachedAdapterProbe = AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.Disposed);
                previousEngineSamples.Clear();
            }
        }

        private UtilizationSample SampleUtilization(GpuAdapterInfo adapter)
        {
            string luidToken = BuildLuidToken(
                adapter.LuidLowPart,
                adapter.LuidHighPart);

            try
            {
                PerformanceCounterCategory category =
                    new PerformanceCounterCategory(GpuEngineCategoryName);
                InstanceDataCollectionCollection categoryData =
                    category.ReadCategory();

                InstanceDataCollection utilizationData =
                    categoryData[GpuUtilizationCounterName];
                if (utilizationData == null)
                {
                    previousEngineSamples.Clear();
                    return UtilizationSample.CreateUnavailable(
                        IntelGpuDataStatus.CounterUnavailable);
                }

                Dictionary<string, CounterSample> currentSamples =
                    new Dictionary<string, CounterSample>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (DictionaryEntry entry in utilizationData)
                {
                    string instanceName = entry.Key as string;
                    InstanceData instanceData = entry.Value as InstanceData;
                    if (instanceName == null ||
                        instanceData == null ||
                        !ContainsLuidToken(instanceName, luidToken))
                    {
                        continue;
                    }

                    currentSamples[instanceName] = instanceData.Sample;
                }

                if (currentSamples.Count == 0)
                {
                    previousEngineSamples.Clear();
                    return UtilizationSample.CreateUnavailable(
                        IntelGpuDataStatus.NoMatchingInstances);
                }

                Dictionary<string, double> utilizationByEngine =
                    new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                bool calculatedAny = false;

                foreach (KeyValuePair<string, CounterSample> current in
                    currentSamples)
                {
                    CounterSample previous;
                    string engineKey;
                    if (!previousEngineSamples.TryGetValue(
                            current.Key,
                            out previous) ||
                        !TryGetEngineKey(current.Key, luidToken, out engineKey))
                    {
                        continue;
                    }

                    double value;
                    try
                    {
                        value = CounterSample.Calculate(previous, current.Value);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (double.IsNaN(value) ||
                        double.IsInfinity(value) ||
                        value < 0)
                    {
                        continue;
                    }

                    value = Math.Min(100.0, value);
                    double engineTotal;
                    utilizationByEngine.TryGetValue(engineKey, out engineTotal);
                    utilizationByEngine[engineKey] =
                        Math.Min(100.0, engineTotal + value);
                    calculatedAny = true;
                }

                previousEngineSamples = currentSamples;

                if (!calculatedAny)
                {
                    return UtilizationSample.CreateUnavailable(
                        IntelGpuDataStatus.FirstSamplePending);
                }

                double busiestEngine = 0;
                foreach (double engineUtilization in utilizationByEngine.Values)
                {
                    busiestEngine = Math.Max(busiestEngine, engineUtilization);
                }

                return UtilizationSample.CreateAvailable(
                    ClampPercentage((int)Math.Round(
                        busiestEngine,
                        MidpointRounding.AwayFromZero)));
            }
            catch (UnauthorizedAccessException)
            {
                previousEngineSamples.Clear();
                return UtilizationSample.CreateUnavailable(
                    IntelGpuDataStatus.AccessDenied);
            }
            catch (SecurityException)
            {
                previousEngineSamples.Clear();
                return UtilizationSample.CreateUnavailable(
                    IntelGpuDataStatus.AccessDenied);
            }
            catch (Win32Exception exception)
            {
                previousEngineSamples.Clear();
                return UtilizationSample.CreateUnavailable(
                    exception.NativeErrorCode == 5
                        ? IntelGpuDataStatus.AccessDenied
                        : IntelGpuDataStatus.SampleFailed);
            }
            catch (InvalidOperationException)
            {
                previousEngineSamples.Clear();
                return UtilizationSample.CreateUnavailable(
                    IntelGpuDataStatus.CounterUnavailable);
            }
            catch (Exception)
            {
                previousEngineSamples.Clear();
                return UtilizationSample.CreateUnavailable(
                    IntelGpuDataStatus.SampleFailed);
            }
        }

        private static MemorySample SampleMemory(GpuAdapterInfo adapter)
        {
            string luidToken = BuildLuidToken(
                adapter.LuidLowPart,
                adapter.LuidHighPart);

            try
            {
                PerformanceCounterCategory category =
                    new PerformanceCounterCategory(GpuAdapterMemoryCategoryName);
                InstanceDataCollectionCollection categoryData =
                    category.ReadCategory();

                long? sharedUsage;
                IntelGpuDataStatus sharedStatus = SumMatchingCounter(
                    categoryData[GpuSharedUsageCounterName],
                    luidToken,
                    out sharedUsage);

                if (sharedStatus != IntelGpuDataStatus.Available)
                {
                    return MemorySample.CreateUnavailable(sharedStatus);
                }

                long? dedicatedUsage;
                IntelGpuDataStatus dedicatedStatus = SumMatchingCounter(
                    categoryData[GpuDedicatedUsageCounterName],
                    luidToken,
                    out dedicatedUsage);

                if (dedicatedStatus != IntelGpuDataStatus.Available)
                {
                    dedicatedUsage = null;
                }

                return MemorySample.CreateAvailable(
                    dedicatedUsage,
                    sharedUsage.Value);
            }
            catch (UnauthorizedAccessException)
            {
                return MemorySample.CreateUnavailable(
                    IntelGpuDataStatus.AccessDenied);
            }
            catch (SecurityException)
            {
                return MemorySample.CreateUnavailable(
                    IntelGpuDataStatus.AccessDenied);
            }
            catch (Win32Exception exception)
            {
                return MemorySample.CreateUnavailable(
                    exception.NativeErrorCode == 5
                        ? IntelGpuDataStatus.AccessDenied
                        : IntelGpuDataStatus.SampleFailed);
            }
            catch (InvalidOperationException)
            {
                return MemorySample.CreateUnavailable(
                    IntelGpuDataStatus.CounterUnavailable);
            }
            catch (Exception)
            {
                return MemorySample.CreateUnavailable(
                    IntelGpuDataStatus.SampleFailed);
            }
        }

        private static IntelGpuDataStatus SumMatchingCounter(
            InstanceDataCollection counterData,
            string luidToken,
            out long? totalValue)
        {
            totalValue = null;
            if (counterData == null)
            {
                return IntelGpuDataStatus.CounterUnavailable;
            }

            bool found = false;
            long total = 0;

            foreach (DictionaryEntry entry in counterData)
            {
                string instanceName = entry.Key as string;
                InstanceData instanceData = entry.Value as InstanceData;
                if (instanceName == null ||
                    instanceData == null ||
                    !ContainsLuidToken(instanceName, luidToken))
                {
                    continue;
                }

                long value = instanceData.Sample.RawValue;
                if (value < 0 || total > long.MaxValue - value)
                {
                    return IntelGpuDataStatus.SampleFailed;
                }

                total += value;
                found = true;
            }

            if (!found)
            {
                return IntelGpuDataStatus.NoMatchingInstances;
            }

            totalValue = total;
            return IntelGpuDataStatus.Available;
        }

        private static AdapterProbeResult DiscoverAdapter(GpuAdapterRole role)
        {
            IDXGIFactory1 factory = null;
            List<GpuAdapterInfo> candidates = new List<GpuAdapterInfo>();
            HashSet<string> seenAdapterLuids = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            try
            {
                Guid factoryInterfaceId = typeof(IDXGIFactory1).GUID;
                int createResult = CreateDXGIFactory1(
                    ref factoryInterfaceId,
                    out factory);

                if (createResult < 0 || factory == null)
                {
                    return AdapterProbeResult.CreateUnavailable(
                        createResult == AccessDeniedHResult
                            ? IntelGpuDataStatus.AccessDenied
                            : IntelGpuDataStatus.ProbeFailed);
                }

                for (uint index = 0; ; index++)
                {
                    IDXGIAdapter1 adapter = null;

                    try
                    {
                        int enumerateResult = factory.EnumAdapters1(index, out adapter);
                        if (enumerateResult == DxgiErrorNotFound)
                        {
                            break;
                        }

                        if (enumerateResult < 0 || adapter == null)
                        {
                            return AdapterProbeResult.CreateUnavailable(
                                enumerateResult == AccessDeniedHResult
                                    ? IntelGpuDataStatus.AccessDenied
                                    : IntelGpuDataStatus.ProbeFailed);
                        }

                        DxgiAdapterDescription1 description;
                        int descriptionResult = adapter.GetDesc1(out description);
                        if (descriptionResult < 0)
                        {
                            return AdapterProbeResult.CreateUnavailable(
                                descriptionResult == AccessDeniedHResult
                                    ? IntelGpuDataStatus.AccessDenied
                                    : IntelGpuDataStatus.ProbeFailed);
                        }

                        if (!IsDxgiAdapterCandidate(
                                description.VendorId,
                                description.Flags))
                        {
                            continue;
                        }

                        if (!TryRegisterAdapterLuid(
                                seenAdapterLuids,
                                description.AdapterLuid.LowPart,
                                description.AdapterLuid.HighPart))
                        {
                            continue;
                        }

                        candidates.Add(
                            new GpuAdapterInfo(
                                description.Description == null
                                    ? string.Empty
                                    : description.Description.TrimEnd('\0').Trim(),
                                description.VendorId,
                                description.AdapterLuid.LowPart,
                                description.AdapterLuid.HighPart,
                                ToNullableInt64(description.DedicatedVideoMemory),
                                ToNullableInt64(description.SharedSystemMemory)));
                    }
                    finally
                    {
                        ReleaseComObject(adapter);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                return AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.AccessDenied);
            }
            catch (SecurityException)
            {
                return AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.AccessDenied);
            }
            catch (Exception)
            {
                return AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.ProbeFailed);
            }
            finally
            {
                ReleaseComObject(factory);
            }

            return SelectAdapter(candidates, role);
        }

        private static AdapterProbeResult SelectAdapter(
            IList<GpuAdapterInfo> candidates,
            GpuAdapterRole role)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.NotFound);
            }

            IDXCoreAdapterFactory factory = null;
            try
            {
                Guid factoryInterfaceId = typeof(IDXCoreAdapterFactory).GUID;
                int createResult = DXCoreCreateAdapterFactory(
                    ref factoryInterfaceId,
                    out factory);
                if (createResult < 0 || factory == null)
                {
                    return AdapterProbeResult.CreateUnavailable(
                        IntelGpuDataStatus.Unsupported);
                }

                List<GpuAdapterInfo> hardwareCandidates =
                    new List<GpuAdapterInfo>();
                List<DxCoreAdapterTraits> hardwareTraits =
                    new List<DxCoreAdapterTraits>();
                GpuAdapterInfo selectedAdapter = null;

                for (int index = 0; index < candidates.Count; index++)
                {
                    DxCoreAdapterTraits traits = QueryAdapterTraits(
                        factory,
                        candidates[index]);
                    if (!ShouldIncludeHardwareCandidate(traits.IsHardware))
                    {
                        continue;
                    }

                    hardwareCandidates.Add(candidates[index]);
                    hardwareTraits.Add(traits);
                }

                List<IntelAdapterIntegrationKind> kinds =
                    new List<IntelAdapterIntegrationKind>();
                for (int index = 0; index < hardwareCandidates.Count; index++)
                {
                    if (hardwareTraits[index].IntegrationKind ==
                            IntelAdapterIntegrationKind.Unknown &&
                        HasClassifiedIdentityMatch(
                            hardwareCandidates,
                            hardwareTraits,
                            index))
                    {
                        continue;
                    }

                    kinds.Add(hardwareTraits[index].IntegrationKind);
                    if (IsTargetRole(role, hardwareTraits[index].IntegrationKind))
                    {
                        selectedAdapter = hardwareCandidates[index];
                    }
                }

                IntelGpuDataStatus status = ResolveRoleStatus(
                    role,
                    kinds.ToArray());
                return status == IntelGpuDataStatus.Available && selectedAdapter != null
                    ? AdapterProbeResult.CreateAvailable(selectedAdapter)
                    : AdapterProbeResult.CreateUnavailable(status);
            }
            catch (DllNotFoundException)
            {
                return AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.Unsupported);
            }
            catch (EntryPointNotFoundException)
            {
                return AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.Unsupported);
            }
            catch (UnauthorizedAccessException)
            {
                return AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.AccessDenied);
            }
            catch (SecurityException)
            {
                return AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.AccessDenied);
            }
            catch (Exception)
            {
                return AdapterProbeResult.CreateUnavailable(
                    IntelGpuDataStatus.Unsupported);
            }
            finally
            {
                ReleaseComObject(factory);
            }
        }

        private static bool HasClassifiedIdentityMatch(
            IList<GpuAdapterInfo> candidates,
            IList<DxCoreAdapterTraits> traits,
            int unknownIndex)
        {
            for (int index = 0; index < candidates.Count; index++)
            {
                if (index == unknownIndex ||
                    traits[index].IntegrationKind == IntelAdapterIntegrationKind.Unknown)
                {
                    continue;
                }

                if (IsSameReportedAdapterIdentity(
                        candidates[unknownIndex].Name,
                        candidates[unknownIndex].VendorId,
                        candidates[unknownIndex].LuidLowPart,
                        candidates[unknownIndex].LuidHighPart,
                        candidates[index].Name,
                        candidates[index].VendorId,
                        candidates[index].LuidLowPart,
                        candidates[index].LuidHighPart))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsSameReportedAdapterIdentity(
            string firstName,
            uint firstVendorId,
            uint firstLuidLowPart,
            int firstLuidHighPart,
            string secondName,
            uint secondVendorId,
            uint secondLuidLowPart,
            int secondLuidHighPart)
        {
            // Vendor and model text can collide across real adapters. Only an
            // exact LUID match is strong enough to suppress an unknown entry.
            return firstVendorId == secondVendorId &&
                firstLuidLowPart == secondLuidLowPart &&
                firstLuidHighPart == secondLuidHighPart &&
                !string.IsNullOrWhiteSpace(firstName) &&
                !string.IsNullOrWhiteSpace(secondName) &&
                string.Equals(
                    firstName.Trim(),
                    secondName.Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static DxCoreAdapterTraits QueryAdapterTraits(
            IDXCoreAdapterFactory factory,
            GpuAdapterInfo candidate)
        {
            IDXCoreAdapter adapter = null;

            try
            {
                LocallyUniqueIdentifier luid = new LocallyUniqueIdentifier
                {
                    LowPart = candidate.LuidLowPart,
                    HighPart = candidate.LuidHighPart
                };
                Guid adapterInterfaceId = typeof(IDXCoreAdapter).GUID;
                int adapterResult = factory.GetAdapterByLuid(
                    ref luid,
                    ref adapterInterfaceId,
                    out adapter);
                if (adapterResult < 0 || adapter == null || !adapter.IsValid())
                {
                    return DxCoreAdapterTraits.CreateUnknown();
                }

                bool propertyValue;
                bool? isHardware = TryReadBooleanProperty(
                    adapter,
                    DxCoreAdapterPropertyIsHardware,
                    out propertyValue)
                        ? (bool?)propertyValue
                        : null;

                if (isHardware.HasValue && !isHardware.Value)
                {
                    return new DxCoreAdapterTraits(
                        isHardware,
                        IntelAdapterIntegrationKind.Unknown);
                }

                IntelAdapterIntegrationKind integrationKind =
                    IntelAdapterIntegrationKind.Unknown;
                if (TryReadBooleanProperty(
                        adapter,
                        DxCoreAdapterPropertyIsIntegrated,
                        out propertyValue))
                {
                    integrationKind = propertyValue
                        ? IntelAdapterIntegrationKind.Integrated
                        : IntelAdapterIntegrationKind.Discrete;
                }

                return new DxCoreAdapterTraits(isHardware, integrationKind);
            }
            catch (Exception)
            {
                return DxCoreAdapterTraits.CreateUnknown();
            }
            finally
            {
                ReleaseComObject(adapter);
            }
        }

        private static bool TryReadBooleanProperty(
            IDXCoreAdapter adapter,
            uint property,
            out bool value)
        {
            value = false;
            if (adapter == null || !adapter.IsPropertySupported(property))
            {
                return false;
            }

            UIntPtr propertySize;
            int sizeResult = adapter.GetPropertySize(property, out propertySize);
            if (sizeResult < 0 || propertySize.ToUInt64() != 1UL)
            {
                return false;
            }

            IntPtr propertyData = Marshal.AllocHGlobal(1);
            try
            {
                int propertyResult = adapter.GetProperty(
                    property,
                    new UIntPtr(1),
                    propertyData);
                if (propertyResult < 0)
                {
                    return false;
                }

                byte byteValue = Marshal.ReadByte(propertyData);
                if (byteValue > 1)
                {
                    return false;
                }

                value = byteValue == 1;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(propertyData);
            }
        }

        internal static IntelGpuDataStatus ResolveRoleStatus(
            GpuAdapterRole role,
            IntelAdapterIntegrationKind[] kinds)
        {
            if (kinds == null || kinds.Length == 0)
            {
                return IntelGpuDataStatus.NotFound;
            }

            int targetCount = 0;
            int unknownCount = 0;
            foreach (IntelAdapterIntegrationKind kind in kinds)
            {
                if (IsTargetRole(role, kind))
                {
                    targetCount++;
                }
                else if (kind == IntelAdapterIntegrationKind.Unknown)
                {
                    unknownCount++;
                }
            }

            if (targetCount > 1)
            {
                return IntelGpuDataStatus.AmbiguousAdapter;
            }

            if (targetCount == 1)
            {
                return unknownCount == 0
                    ? IntelGpuDataStatus.Available
                    : IntelGpuDataStatus.AmbiguousAdapter;
            }

            if (unknownCount == 0)
            {
                return IntelGpuDataStatus.NotFound;
            }

            return unknownCount == 1
                ? IntelGpuDataStatus.Unsupported
                : IntelGpuDataStatus.AmbiguousAdapter;
        }

        private static bool IsTargetRole(
            GpuAdapterRole role,
            IntelAdapterIntegrationKind kind)
        {
            return role == GpuAdapterRole.Integrated
                ? kind == IntelAdapterIntegrationKind.Integrated
                : kind == IntelAdapterIntegrationKind.Discrete;
        }

        internal static bool IsSupportedVendor(uint vendorId)
        {
            return vendorId == NvidiaVendorId ||
                vendorId == AmdVendorId ||
                vendorId == IntelVendorId;
        }

        internal static bool IsDxgiAdapterCandidate(uint vendorId, uint flags)
        {
            return IsSupportedVendor(vendorId) &&
                (flags & (DxgiAdapterFlagRemote | DxgiAdapterFlagSoftware)) == 0;
        }

        internal static bool ShouldIncludeHardwareCandidate(bool? isHardware)
        {
            return !isHardware.HasValue || isHardware.Value;
        }

        internal static bool TryRegisterAdapterLuid(
            ISet<string> seenAdapterLuids,
            uint lowPart,
            int highPart)
        {
            if (seenAdapterLuids == null)
            {
                throw new ArgumentNullException("seenAdapterLuids");
            }

            return seenAdapterLuids.Add(BuildLuidKey(lowPart, highPart));
        }

        private static bool IsSameAdapter(
            AdapterProbeResult first,
            AdapterProbeResult second)
        {
            if (first == null || second == null)
            {
                return false;
            }

            if (first.Adapter == null || second.Adapter == null)
            {
                return first.Adapter == null &&
                    second.Adapter == null &&
                    first.Status == second.Status;
            }

            return first.Adapter.HasSameLuid(second.Adapter);
        }

        private static string BuildLuidToken(uint lowPart, int highPart)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "luid_0x{0:X8}_0x{1:X8}_",
                unchecked((uint)highPart),
                lowPart);
        }

        private static string BuildLuidKey(uint lowPart, int highPart)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:X8}:{1:X8}",
                unchecked((uint)highPart),
                lowPart);
        }

        private static bool ContainsLuidToken(
            string instanceName,
            string luidToken)
        {
            return instanceName.IndexOf(
                luidToken,
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryGetEngineKey(
            string instanceName,
            string luidToken,
            out string engineKey)
        {
            engineKey = string.Empty;
            int luidIndex = instanceName.IndexOf(
                luidToken,
                StringComparison.OrdinalIgnoreCase);
            if (luidIndex < 0)
            {
                return false;
            }

            int physicalStart = instanceName.IndexOf(
                "phys_",
                luidIndex + luidToken.Length,
                StringComparison.OrdinalIgnoreCase);
            int engineTypeStart = instanceName.IndexOf(
                "_engtype_",
                physicalStart < 0 ? 0 : physicalStart,
                StringComparison.OrdinalIgnoreCase);

            if (physicalStart < 0 || engineTypeStart <= physicalStart)
            {
                return false;
            }

            string candidate = instanceName.Substring(
                physicalStart,
                engineTypeStart - physicalStart);
            if (candidate.IndexOf(
                    "_eng_",
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            engineKey = candidate;
            return true;
        }

        private static int ClampPercentage(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            return value > 100 ? 100 : value;
        }

        private static long? ToNullableInt64(UIntPtr value)
        {
            ulong unsignedValue = value.ToUInt64();
            return unsignedValue <= long.MaxValue ? (long?)unsignedValue : null;
        }

        private static void ReleaseComObject(object value)
        {
            if (value == null || !Marshal.IsComObject(value))
            {
                return;
            }

            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch (Exception)
            {
                // A failed cleanup must not terminate hardware monitoring.
            }
        }

        private sealed class GpuAdapterInfo
        {
            internal GpuAdapterInfo(
                string name,
                uint vendorId,
                uint luidLowPart,
                int luidHighPart,
                long? dedicatedMemoryLimitBytes,
                long? sharedMemoryLimitBytes)
            {
                Name = name;
                VendorId = vendorId;
                LuidLowPart = luidLowPart;
                LuidHighPart = luidHighPart;
                DedicatedMemoryLimitBytes = dedicatedMemoryLimitBytes;
                SharedMemoryLimitBytes = sharedMemoryLimitBytes;
            }

            internal string Name { get; private set; }
            internal uint VendorId { get; private set; }
            internal uint LuidLowPart { get; private set; }
            internal int LuidHighPart { get; private set; }
            internal long? DedicatedMemoryLimitBytes { get; private set; }
            internal long? SharedMemoryLimitBytes { get; private set; }

            internal bool HasSameLuid(GpuAdapterInfo other)
            {
                return other != null &&
                    LuidLowPart == other.LuidLowPart &&
                    LuidHighPart == other.LuidHighPart;
            }
        }

        private sealed class AdapterProbeResult
        {
            private AdapterProbeResult(
                IntelGpuDataStatus status,
                GpuAdapterInfo adapter)
            {
                Status = status;
                Adapter = adapter;
            }

            internal IntelGpuDataStatus Status { get; private set; }
            internal GpuAdapterInfo Adapter { get; private set; }

            internal static AdapterProbeResult CreateAvailable(
                GpuAdapterInfo adapter)
            {
                return new AdapterProbeResult(
                    IntelGpuDataStatus.Available,
                    adapter);
            }

            internal static AdapterProbeResult CreateUnavailable(
                IntelGpuDataStatus status)
            {
                return new AdapterProbeResult(status, null);
            }
        }

        private sealed class DxCoreAdapterTraits
        {
            internal DxCoreAdapterTraits(
                bool? isHardware,
                IntelAdapterIntegrationKind integrationKind)
            {
                IsHardware = isHardware;
                IntegrationKind = integrationKind;
            }

            internal bool? IsHardware { get; private set; }
            internal IntelAdapterIntegrationKind IntegrationKind { get; private set; }

            internal static DxCoreAdapterTraits CreateUnknown()
            {
                return new DxCoreAdapterTraits(
                    null,
                    IntelAdapterIntegrationKind.Unknown);
            }
        }

        private sealed class UtilizationSample
        {
            private UtilizationSample(
                IntelGpuDataStatus status,
                int? percent)
            {
                Status = status;
                Percent = percent;
            }

            internal IntelGpuDataStatus Status { get; private set; }
            internal int? Percent { get; private set; }

            internal static UtilizationSample CreateAvailable(int percent)
            {
                return new UtilizationSample(
                    IntelGpuDataStatus.Available,
                    percent);
            }

            internal static UtilizationSample CreateUnavailable(
                IntelGpuDataStatus status)
            {
                return new UtilizationSample(status, null);
            }
        }

        private sealed class MemorySample
        {
            private MemorySample(
                IntelGpuDataStatus status,
                long? dedicatedUsageBytes,
                long? sharedUsageBytes)
            {
                Status = status;
                DedicatedUsageBytes = dedicatedUsageBytes;
                SharedUsageBytes = sharedUsageBytes;
            }

            internal IntelGpuDataStatus Status { get; private set; }
            internal long? DedicatedUsageBytes { get; private set; }
            internal long? SharedUsageBytes { get; private set; }

            internal static MemorySample CreateAvailable(
                long? dedicatedUsageBytes,
                long sharedUsageBytes)
            {
                return new MemorySample(
                    IntelGpuDataStatus.Available,
                    dedicatedUsageBytes,
                    sharedUsageBytes);
            }

            internal static MemorySample CreateUnavailable(
                IntelGpuDataStatus status)
            {
                return new MemorySample(status, null, null);
            }
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(
            [In] ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("dxcore.dll", ExactSpelling = true)]
        private static extern int DXCoreCreateAdapterFactory(
            [In] ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IDXCoreAdapterFactory factory);

        [ComImport]
        [Guid("78EE5945-C36E-4B13-A669-005DD11C0F06")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXCoreAdapterFactory
        {
            [PreserveSig]
            int CreateAdapterList(
                uint attributeCount,
                IntPtr filterAttributes,
                ref Guid interfaceId,
                out IntPtr adapterList);

            [PreserveSig]
            int GetAdapterByLuid(
                ref LocallyUniqueIdentifier adapterLuid,
                ref Guid interfaceId,
                [MarshalAs(UnmanagedType.Interface)] out IDXCoreAdapter adapter);
        }

        [ComImport]
        [Guid("F0DB4C7F-FE5A-42A2-BD62-F2A6CF6FC83E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXCoreAdapter
        {
            [PreserveSig]
            [return: MarshalAs(UnmanagedType.U1)]
            bool IsValid();

            [PreserveSig]
            [return: MarshalAs(UnmanagedType.U1)]
            bool IsAttributeSupported(ref Guid attributeId);

            [PreserveSig]
            [return: MarshalAs(UnmanagedType.U1)]
            bool IsPropertySupported(uint property);

            [PreserveSig]
            int GetProperty(uint property, UIntPtr bufferSize, IntPtr propertyData);

            [PreserveSig]
            int GetPropertySize(uint property, out UIntPtr bufferSize);
        }

        [ComImport]
        [Guid("770AAE78-F26F-4DBA-A829-253C83D1B387")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            [PreserveSig]
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);

            [PreserveSig]
            int SetPrivateDataInterface(ref Guid name, IntPtr unknown);

            [PreserveSig]
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);

            [PreserveSig]
            int GetParent(ref Guid interfaceId, out IntPtr parent);

            [PreserveSig]
            int EnumAdapters(uint adapterIndex, out IntPtr adapter);

            [PreserveSig]
            int MakeWindowAssociation(IntPtr windowHandle, uint flags);

            [PreserveSig]
            int GetWindowAssociation(out IntPtr windowHandle);

            [PreserveSig]
            int CreateSwapChain(
                IntPtr device,
                IntPtr description,
                out IntPtr swapChain);

            [PreserveSig]
            int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);

            [PreserveSig]
            int EnumAdapters1(
                uint adapterIndex,
                [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);

            [PreserveSig]
            [return: MarshalAs(UnmanagedType.Bool)]
            bool IsCurrent();
        }

        [ComImport]
        [Guid("29038F61-3839-4626-91FD-086879011A05")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            [PreserveSig]
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);

            [PreserveSig]
            int SetPrivateDataInterface(ref Guid name, IntPtr unknown);

            [PreserveSig]
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);

            [PreserveSig]
            int GetParent(ref Guid interfaceId, out IntPtr parent);

            [PreserveSig]
            int EnumOutputs(uint outputIndex, out IntPtr output);

            [PreserveSig]
            int GetDesc(IntPtr description);

            [PreserveSig]
            int CheckInterfaceSupport(
                ref Guid interfaceId,
                out long userModeDriverVersion);

            [PreserveSig]
            int GetDesc1(out DxgiAdapterDescription1 description);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LocallyUniqueIdentifier
        {
            internal uint LowPart;
            internal int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DxgiAdapterDescription1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string Description;
            internal uint VendorId;
            internal uint DeviceId;
            internal uint SubSystemId;
            internal uint Revision;
            internal UIntPtr DedicatedVideoMemory;
            internal UIntPtr DedicatedSystemMemory;
            internal UIntPtr SharedSystemMemory;
            internal LocallyUniqueIdentifier AdapterLuid;
            internal uint Flags;
        }
    }
}
