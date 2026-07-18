using System;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace TinyHwBar
{
    internal enum GatewayLatencyStatus
    {
        Disabled,
        NetworkUnavailable,
        GatewayMissing,
        GatewayRejected,
        Waiting,
        Probing,
        Available,
        TimedOut,
        Unreachable,
        AccessDenied,
        Failed,
        Disposed
    }

    internal sealed class GatewayLatencyMetrics
    {
        internal bool IsEnabled { get; private set; }
        internal bool IsAvailable { get; private set; }
        internal bool IsProbeInProgress { get; private set; }
        internal string InterfaceId { get; private set; }
        internal string GatewayAddress { get; private set; }
        internal long? RoundtripTimeMilliseconds { get; private set; }
        internal DateTime? SampledAtUtc { get; private set; }
        internal GatewayLatencyStatus Status { get; private set; }

        internal static GatewayLatencyMetrics Create(
            bool isEnabled,
            bool isAvailable,
            bool isProbeInProgress,
            string interfaceId,
            string gatewayAddress,
            long? roundtripTimeMilliseconds,
            DateTime? sampledAtUtc,
            GatewayLatencyStatus status)
        {
            return new GatewayLatencyMetrics
            {
                IsEnabled = isEnabled,
                IsAvailable = isAvailable,
                IsProbeInProgress = isProbeInProgress,
                InterfaceId = interfaceId ?? string.Empty,
                GatewayAddress = gatewayAddress ?? string.Empty,
                RoundtripTimeMilliseconds = roundtripTimeMilliseconds,
                SampledAtUtc = sampledAtUtc,
                Status = status
            };
        }

        internal GatewayLatencyMetrics Copy()
        {
            return Create(
                IsEnabled,
                IsAvailable,
                IsProbeInProgress,
                InterfaceId,
                GatewayAddress,
                RoundtripTimeMilliseconds,
                SampledAtUtc,
                Status);
        }
    }

    internal sealed class GatewayEchoResult
    {
        private GatewayEchoResult(
            GatewayLatencyStatus status,
            long? roundtripTimeMilliseconds)
        {
            Status = status;
            RoundtripTimeMilliseconds = roundtripTimeMilliseconds;
        }

        internal GatewayLatencyStatus Status { get; private set; }
        internal long? RoundtripTimeMilliseconds { get; private set; }

        internal static GatewayEchoResult CreateAvailable(long milliseconds)
        {
            return new GatewayEchoResult(
                GatewayLatencyStatus.Available,
                Math.Max(0, milliseconds));
        }

        internal static GatewayEchoResult CreateUnavailable(
            GatewayLatencyStatus status)
        {
            return new GatewayEchoResult(status, null);
        }
    }

    internal delegate Task<GatewayEchoResult> GatewayEchoSender(
        IPAddress target,
        int timeoutMilliseconds);

    internal delegate GatewayLatencyStatus GatewayTargetValidator(
        NetworkMetrics networkMetrics,
        out IPAddress target);

    internal sealed class GatewayLatencySampler : IDisposable
    {
        private const int DefaultTimeoutMilliseconds = 750;
        private static readonly TimeSpan DefaultProbeInterval =
            TimeSpan.FromSeconds(10);

        private readonly object synchronization = new object();
        private readonly GatewayEchoSender echoSender;
        private readonly GatewayTargetValidator targetValidator;
        private readonly TimeSpan probeInterval;
        private readonly int timeoutMilliseconds;

        private bool disposed;
        private bool enabled;
        private long generation;
        private string activeTargetKey = string.Empty;
        // This is a sampler-wide send gate, not target-specific state. Keeping it
        // across route/target resets preserves the documented global probe limit
        // even while Windows is rapidly changing interfaces or gateways.
        private DateTime nextAllowedProbeUtc = DateTime.MinValue;
        private Task<GatewayEchoResult> activeProbe;
        private GatewayLatencyMetrics cachedMetrics = CreateDisabledMetrics();

        internal GatewayLatencySampler()
            : this(
                SendEchoAsync,
                DefaultProbeInterval,
                DefaultTimeoutMilliseconds)
        {
        }

        internal GatewayLatencySampler(
            GatewayEchoSender echoSender,
            TimeSpan probeInterval,
            int timeoutMilliseconds)
            : this(
                echoSender,
                probeInterval,
                timeoutMilliseconds,
                ValidateGatewayTarget)
        {
        }

        internal GatewayLatencySampler(
            GatewayEchoSender echoSender,
            TimeSpan probeInterval,
            int timeoutMilliseconds,
            GatewayTargetValidator targetValidator)
        {
            if (echoSender == null)
            {
                throw new ArgumentNullException("echoSender");
            }

            if (probeInterval < TimeSpan.FromSeconds(5))
            {
                throw new ArgumentOutOfRangeException(
                    "probeInterval",
                    "Gateway probes must be at least five seconds apart.");
            }

            if (timeoutMilliseconds < 100 || timeoutMilliseconds > 2000)
            {
                throw new ArgumentOutOfRangeException(
                    "timeoutMilliseconds",
                    "Gateway probe timeout must be between 100 and 2000 ms.");
            }

            if (targetValidator == null)
            {
                throw new ArgumentNullException("targetValidator");
            }

            this.echoSender = echoSender;
            this.targetValidator = targetValidator;
            this.probeInterval = probeInterval;
            this.timeoutMilliseconds = timeoutMilliseconds;
        }

        internal bool IsEnabled
        {
            get
            {
                lock (synchronization)
                {
                    return enabled && !disposed;
                }
            }
        }

        internal void SetEnabled(bool value)
        {
            lock (synchronization)
            {
                if (disposed || enabled == value)
                {
                    return;
                }

                enabled = value;
                ResetState();
                cachedMetrics = value
                    ? GatewayLatencyMetrics.Create(
                        true,
                        false,
                        false,
                        string.Empty,
                        string.Empty,
                        null,
                        null,
                        GatewayLatencyStatus.Waiting)
                    : CreateDisabledMetrics();
            }
        }

        internal GatewayLatencyMetrics Sample(NetworkMetrics networkMetrics)
        {
            lock (synchronization)
            {
                if (disposed)
                {
                    return GatewayLatencyMetrics.Create(
                        false,
                        false,
                        false,
                        string.Empty,
                        string.Empty,
                        null,
                        null,
                        GatewayLatencyStatus.Disposed);
                }

                // This check intentionally comes before any adapter lookup. The
                // default-disabled sampler cannot send a packet or touch DNS.
                if (!enabled)
                {
                    return CreateDisabledMetrics();
                }

                IPAddress target;
                GatewayLatencyStatus validationStatus = targetValidator(
                    networkMetrics,
                    out target);
                if (validationStatus != GatewayLatencyStatus.Available)
                {
                    ResetState();
                    cachedMetrics = GatewayLatencyMetrics.Create(
                        true,
                        false,
                        false,
                        networkMetrics == null
                            ? string.Empty
                            : networkMetrics.InterfaceId,
                        networkMetrics == null
                            ? string.Empty
                            : networkMetrics.DefaultGatewayAddress,
                        null,
                        null,
                        validationStatus);
                    return cachedMetrics.Copy();
                }

                string interfaceId = networkMetrics.InterfaceId;
                string gatewayAddress = target.ToString();
                string targetKey = interfaceId + "|" + gatewayAddress;
                if (!string.Equals(
                        activeTargetKey,
                        targetKey,
                        StringComparison.Ordinal))
                {
                    ResetState();
                    activeTargetKey = targetKey;
                    cachedMetrics = GatewayLatencyMetrics.Create(
                        true,
                        false,
                        false,
                        interfaceId,
                        gatewayAddress,
                        null,
                        null,
                        GatewayLatencyStatus.Waiting);
                }

                if (activeProbe != null)
                {
                    return GatewayLatencyMetrics.Create(
                        true,
                        cachedMetrics.IsAvailable,
                        true,
                        interfaceId,
                        gatewayAddress,
                        cachedMetrics.RoundtripTimeMilliseconds,
                        cachedMetrics.SampledAtUtc,
                        GatewayLatencyStatus.Probing);
                }

                if (DateTime.UtcNow < nextAllowedProbeUtc)
                {
                    return cachedMetrics.Copy();
                }

                StartProbe(target, interfaceId, gatewayAddress);
                if (activeProbe == null)
                {
                    // A test sender or immediate failure may complete synchronously.
                    return cachedMetrics.Copy();
                }

                return GatewayLatencyMetrics.Create(
                    true,
                    cachedMetrics.IsAvailable,
                    true,
                    interfaceId,
                    gatewayAddress,
                    cachedMetrics.RoundtripTimeMilliseconds,
                    cachedMetrics.SampledAtUtc,
                    GatewayLatencyStatus.Probing);
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
                enabled = false;
                ResetState();
                cachedMetrics = GatewayLatencyMetrics.Create(
                    false,
                    false,
                    false,
                    string.Empty,
                    string.Empty,
                    null,
                    null,
                    GatewayLatencyStatus.Disposed);
            }
        }

        private void StartProbe(
            IPAddress target,
            string interfaceId,
            string gatewayAddress)
        {
            long probeGeneration = ++generation;
            nextAllowedProbeUtc = DateTime.UtcNow.Add(probeInterval);

            Task<GatewayEchoResult> probe;
            try
            {
                probe = echoSender(target, timeoutMilliseconds);
            }
            catch (Exception exception)
            {
                cachedMetrics = CreateFailureMetrics(
                    interfaceId,
                    gatewayAddress,
                    ClassifyException(exception));
                activeProbe = null;
                return;
            }

            if (probe == null)
            {
                cachedMetrics = CreateFailureMetrics(
                    interfaceId,
                    gatewayAddress,
                    GatewayLatencyStatus.Failed);
                activeProbe = null;
                return;
            }

            activeProbe = probe;
            probe.ContinueWith(
                task => CompleteProbe(
                    task,
                    probeGeneration,
                    interfaceId,
                    gatewayAddress),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void CompleteProbe(
            Task<GatewayEchoResult> probe,
            long probeGeneration,
            string interfaceId,
            string gatewayAddress)
        {
            GatewayEchoResult result;
            if (probe.IsCanceled)
            {
                result = GatewayEchoResult.CreateUnavailable(
                    GatewayLatencyStatus.Failed);
            }
            else if (probe.IsFaulted)
            {
                result = GatewayEchoResult.CreateUnavailable(
                    ClassifyException(probe.Exception));
            }
            else
            {
                result = probe.Result ?? GatewayEchoResult.CreateUnavailable(
                    GatewayLatencyStatus.Failed);
            }

            lock (synchronization)
            {
                if (disposed ||
                    !enabled ||
                    probeGeneration != generation ||
                    !ReferenceEquals(activeProbe, probe))
                {
                    return;
                }

                activeProbe = null;
                bool isAvailable =
                    result.Status == GatewayLatencyStatus.Available &&
                    result.RoundtripTimeMilliseconds.HasValue;
                cachedMetrics = GatewayLatencyMetrics.Create(
                    true,
                    isAvailable,
                    false,
                    interfaceId,
                    gatewayAddress,
                    isAvailable ? result.RoundtripTimeMilliseconds : null,
                    DateTime.UtcNow,
                    result.Status);
            }
        }

        private void ResetState()
        {
            generation++;
            activeProbe = null;
            activeTargetKey = string.Empty;
        }

        private static GatewayLatencyStatus ValidateGatewayTarget(
            NetworkMetrics networkMetrics,
            out IPAddress target)
        {
            target = null;
            if (networkMetrics == null || !networkMetrics.IsAvailable)
            {
                return GatewayLatencyStatus.NetworkUnavailable;
            }

            if (string.IsNullOrWhiteSpace(networkMetrics.InterfaceId) ||
                string.IsNullOrWhiteSpace(networkMetrics.DefaultGatewayAddress))
            {
                return GatewayLatencyStatus.GatewayMissing;
            }

            IPAddress parsedAddress;
            if (!IPAddress.TryParse(
                    networkMetrics.DefaultGatewayAddress,
                    out parsedAddress) ||
                !IsLocalGatewayAddress(parsedAddress))
            {
                return GatewayLatencyStatus.GatewayRejected;
            }

            try
            {
                foreach (NetworkInterface networkInterface in
                    NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(
                            networkInterface.Id,
                            networkMetrics.InterfaceId,
                            StringComparison.Ordinal) ||
                        networkInterface.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    foreach (GatewayIPAddressInformation gateway in
                        networkInterface.GetIPProperties().GatewayAddresses)
                    {
                        if (gateway != null &&
                            gateway.Address != null &&
                            gateway.Address.Equals(parsedAddress))
                        {
                            target = parsedAddress;
                            return GatewayLatencyStatus.Available;
                        }
                    }

                    return GatewayLatencyStatus.GatewayRejected;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return GatewayLatencyStatus.AccessDenied;
            }
            catch (SecurityException)
            {
                return GatewayLatencyStatus.AccessDenied;
            }
            catch (Exception)
            {
                return GatewayLatencyStatus.Failed;
            }

            return GatewayLatencyStatus.GatewayRejected;
        }

        internal static bool IsLocalGatewayAddress(IPAddress address)
        {
            if (address == null || IPAddress.IsLoopback(address))
            {
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                return (bytes[0] == 10) ||
                    (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                    (bytes[0] == 169 && bytes[1] == 254) ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168);
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] bytes = address.GetAddressBytes();
                return address.IsIPv6LinkLocal ||
                    (bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC);
            }

            return false;
        }

        private static async Task<GatewayEchoResult> SendEchoAsync(
            IPAddress target,
            int timeoutMilliseconds)
        {
            using (Ping ping = new Ping())
            {
                // IPAddress overload prevents DNS. The caller already proved that
                // this literal address is the selected interface's current gateway.
                PingReply reply = await ping.SendPingAsync(
                    target,
                    timeoutMilliseconds).ConfigureAwait(false);

                if (reply.Status == IPStatus.Success)
                {
                    return GatewayEchoResult.CreateAvailable(reply.RoundtripTime);
                }

                return GatewayEchoResult.CreateUnavailable(
                    reply.Status == IPStatus.TimedOut
                        ? GatewayLatencyStatus.TimedOut
                        : GatewayLatencyStatus.Unreachable);
            }
        }

        private static GatewayLatencyStatus ClassifyException(
            Exception exception)
        {
            Exception current = exception;
            while (current is AggregateException && current.InnerException != null)
            {
                current = current.InnerException;
            }

            while (current != null)
            {
                if (current is UnauthorizedAccessException ||
                    current is SecurityException)
                {
                    return GatewayLatencyStatus.AccessDenied;
                }

                Win32Exception win32Exception = current as Win32Exception;
                if (win32Exception != null && win32Exception.NativeErrorCode == 5)
                {
                    return GatewayLatencyStatus.AccessDenied;
                }

                SocketException socketException = current as SocketException;
                if (socketException != null &&
                    socketException.SocketErrorCode == SocketError.AccessDenied)
                {
                    return GatewayLatencyStatus.AccessDenied;
                }

                current = current.InnerException;
            }

            return GatewayLatencyStatus.Failed;
        }

        private static GatewayLatencyMetrics CreateDisabledMetrics()
        {
            return GatewayLatencyMetrics.Create(
                false,
                false,
                false,
                string.Empty,
                string.Empty,
                null,
                null,
                GatewayLatencyStatus.Disabled);
        }

        private static GatewayLatencyMetrics CreateFailureMetrics(
            string interfaceId,
            string gatewayAddress,
            GatewayLatencyStatus status)
        {
            return GatewayLatencyMetrics.Create(
                true,
                false,
                false,
                interfaceId,
                gatewayAddress,
                null,
                DateTime.UtcNow,
                status);
        }
    }
}
