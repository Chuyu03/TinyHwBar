using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TinyHwBar
{
    internal enum NetworkSelectionStatus
    {
        Available,
        NoRoute,
        SplitRoute,
        RouteLookupFailed,
        RouteInterfaceMissing,
        CounterUnavailable,
        Disposed
    }

    [Flags]
    internal enum NetworkRouteFamilies
    {
        None = 0,
        IPv4 = 1,
        IPv6 = 2
    }

    internal enum NetworkRouteLookupState
    {
        Success,
        NoRoute,
        Failed
    }

    internal sealed class NetworkRouteLookupResult
    {
        internal NetworkRouteLookupResult(
            AddressFamily family,
            NetworkRouteLookupState state,
            uint interfaceIndex,
            uint nativeErrorCode)
        {
            Family = family;
            State = state;
            InterfaceIndex = interfaceIndex;
            NativeErrorCode = nativeErrorCode;
        }

        internal AddressFamily Family { get; private set; }
        internal NetworkRouteLookupState State { get; private set; }
        internal uint InterfaceIndex { get; private set; }
        internal uint NativeErrorCode { get; private set; }

        internal static NetworkRouteLookupResult CreateSuccess(
            AddressFamily family,
            uint interfaceIndex)
        {
            return new NetworkRouteLookupResult(
                family,
                NetworkRouteLookupState.Success,
                interfaceIndex,
                0);
        }

        internal static NetworkRouteLookupResult CreateNoRoute(
            AddressFamily family,
            uint nativeErrorCode)
        {
            return new NetworkRouteLookupResult(
                family,
                NetworkRouteLookupState.NoRoute,
                0,
                nativeErrorCode);
        }

        internal static NetworkRouteLookupResult CreateFailed(
            AddressFamily family,
            uint nativeErrorCode)
        {
            return new NetworkRouteLookupResult(
                family,
                NetworkRouteLookupState.Failed,
                0,
                nativeErrorCode);
        }
    }

    internal sealed class NetworkRouteIdentity
    {
        internal NetworkRouteIdentity(
            string interfaceId,
            uint? ipv4InterfaceIndex,
            uint? ipv6InterfaceIndex)
        {
            InterfaceId = interfaceId ?? string.Empty;
            Ipv4InterfaceIndex = ipv4InterfaceIndex;
            Ipv6InterfaceIndex = ipv6InterfaceIndex;
        }

        internal string InterfaceId { get; private set; }
        internal uint? Ipv4InterfaceIndex { get; private set; }
        internal uint? Ipv6InterfaceIndex { get; private set; }
    }

    internal sealed class NetworkRouteDecision
    {
        internal NetworkRouteDecision(
            NetworkSelectionStatus status,
            string interfaceId,
            NetworkRouteFamilies families)
        {
            Status = status;
            InterfaceId = interfaceId ?? string.Empty;
            Families = families;
        }

        internal NetworkSelectionStatus Status { get; private set; }
        internal string InterfaceId { get; private set; }
        internal NetworkRouteFamilies Families { get; private set; }
    }

    internal sealed class NetworkMetrics
    {
        internal bool IsAvailable { get; private set; }
        internal NetworkSelectionStatus SelectionStatus { get; private set; }
        internal string InterfaceId { get; private set; }
        internal string InterfaceName { get; private set; }
        internal string DefaultGatewayAddress { get; private set; }
        internal long? ReceivedBytesPerSecond { get; private set; }
        internal long? SentBytesPerSecond { get; private set; }
        internal long? LinkSpeedBitsPerSecond { get; private set; }

        internal static NetworkMetrics CreateUnavailable()
        {
            return CreateUnavailable(NetworkSelectionStatus.NoRoute);
        }

        internal static NetworkMetrics CreateUnavailable(
            NetworkSelectionStatus selectionStatus)
        {
            return new NetworkMetrics
            {
                IsAvailable = false,
                SelectionStatus = selectionStatus,
                InterfaceId = string.Empty,
                InterfaceName = string.Empty,
                DefaultGatewayAddress = string.Empty,
                ReceivedBytesPerSecond = null,
                SentBytesPerSecond = null,
                LinkSpeedBitsPerSecond = null
            };
        }

        internal static NetworkMetrics CreateAvailable(
            string interfaceId,
            string interfaceName,
            string defaultGatewayAddress,
            long? receivedBytesPerSecond,
            long? sentBytesPerSecond,
            long? linkSpeedBitsPerSecond)
        {
            return new NetworkMetrics
            {
                IsAvailable = true,
                SelectionStatus = NetworkSelectionStatus.Available,
                InterfaceId = interfaceId,
                InterfaceName = interfaceName,
                DefaultGatewayAddress = defaultGatewayAddress,
                ReceivedBytesPerSecond = receivedBytesPerSecond,
                SentBytesPerSecond = sentBytesPerSecond,
                LinkSpeedBitsPerSecond = linkSpeedBitsPerSecond
            };
        }
    }

    internal sealed class NetworkSampler : IDisposable
    {
        private const uint NoError = 0;
        private const uint ErrorNotSupported = 50;
        private const uint ErrorNotFound = 1168;
        private const uint ErrorNoNetwork = 1222;
        private const uint ErrorNetworkUnreachable = 1231;
        private const uint ErrorHostUnreachable = 1232;
        private const uint WsaAddressFamilyNotSupported = 10047;
        private const uint WsaNetworkUnreachable = 10051;
        private const uint WsaHostUnreachable = 10065;

        private static readonly IPAddress Ipv4RouteProbe =
            IPAddress.Parse("192.0.2.1");
        private static readonly IPAddress Ipv6RouteProbe =
            IPAddress.Parse("2001:db8::1");

        private readonly object synchronization = new object();

        private bool disposed;
        private bool hasBaseline;
        private string previousInterfaceId;
        private long previousReceivedBytes;
        private long previousSentBytes;
        private long previousTimestamp;

        internal NetworkMetrics Sample()
        {
            lock (synchronization)
            {
                if (disposed)
                {
                    return NetworkMetrics.CreateUnavailable(
                        NetworkSelectionStatus.Disposed);
                }

                CandidateSelection selection;
                try
                {
                    selection = SelectCandidate();
                }
                catch (Exception)
                {
                    ResetBaseline();
                    return NetworkMetrics.CreateUnavailable(
                        NetworkSelectionStatus.RouteLookupFailed);
                }

                if (selection.Status != NetworkSelectionStatus.Available ||
                    selection.Candidate == null)
                {
                    ResetBaseline();
                    return NetworkMetrics.CreateUnavailable(selection.Status);
                }

                try
                {
                    NetworkInterface networkInterface = selection.Candidate.Interface;
                    IPInterfaceStatistics statistics = networkInterface.GetIPStatistics();
                    long receivedBytes = statistics.BytesReceived;
                    long sentBytes = statistics.BytesSent;
                    long timestamp = Stopwatch.GetTimestamp();
                    long? receivedRate = null;
                    long? sentRate = null;

                    bool sameInterface = hasBaseline && string.Equals(
                        previousInterfaceId,
                        networkInterface.Id,
                        StringComparison.Ordinal);

                    if (sameInterface &&
                        receivedBytes >= previousReceivedBytes &&
                        sentBytes >= previousSentBytes &&
                        timestamp > previousTimestamp)
                    {
                        double elapsedSeconds =
                            (timestamp - previousTimestamp) / (double)Stopwatch.Frequency;

                        receivedRate = CalculateRate(
                            receivedBytes - previousReceivedBytes,
                            elapsedSeconds);
                        sentRate = CalculateRate(
                            sentBytes - previousSentBytes,
                            elapsedSeconds);
                    }

                    StoreBaseline(
                        networkInterface.Id,
                        receivedBytes,
                        sentBytes,
                        timestamp);

                    return NetworkMetrics.CreateAvailable(
                        networkInterface.Id,
                        networkInterface.Name,
                        FindGatewayAddress(networkInterface, selection.Families),
                        receivedRate,
                        sentRate,
                        ReadLinkSpeed(networkInterface));
                }
                catch (Exception)
                {
                    ResetBaseline();
                    return NetworkMetrics.CreateUnavailable(
                        NetworkSelectionStatus.CounterUnavailable);
                }
            }
        }

        public void Dispose()
        {
            lock (synchronization)
            {
                disposed = true;
                ResetBaseline();
            }
        }

        internal static NetworkRouteDecision ResolveRoute(
            IList<NetworkRouteIdentity> interfaces,
            NetworkRouteLookupResult ipv4,
            NetworkRouteLookupResult ipv6)
        {
            if (interfaces == null ||
                ipv4 == null ||
                ipv6 == null ||
                ipv4.Family != AddressFamily.InterNetwork ||
                ipv6.Family != AddressFamily.InterNetworkV6 ||
                !IsKnownLookupState(ipv4.State) ||
                !IsKnownLookupState(ipv6.State) ||
                ipv4.State == NetworkRouteLookupState.Failed ||
                ipv6.State == NetworkRouteLookupState.Failed)
            {
                return CreateRouteDecision(NetworkSelectionStatus.RouteLookupFailed);
            }

            bool ipv4Available = ipv4.State == NetworkRouteLookupState.Success;
            bool ipv6Available = ipv6.State == NetworkRouteLookupState.Success;
            if (!ipv4Available && !ipv6Available)
            {
                return CreateRouteDecision(NetworkSelectionStatus.NoRoute);
            }

            string ipv4InterfaceId = string.Empty;
            string ipv6InterfaceId = string.Empty;

            if (ipv4Available &&
                !TryFindUniqueInterfaceId(
                    interfaces,
                    AddressFamily.InterNetwork,
                    ipv4.InterfaceIndex,
                    out ipv4InterfaceId))
            {
                return CreateRouteDecision(
                    NetworkSelectionStatus.RouteInterfaceMissing);
            }

            if (ipv6Available &&
                !TryFindUniqueInterfaceId(
                    interfaces,
                    AddressFamily.InterNetworkV6,
                    ipv6.InterfaceIndex,
                    out ipv6InterfaceId))
            {
                return CreateRouteDecision(
                    NetworkSelectionStatus.RouteInterfaceMissing);
            }

            if (ipv4Available &&
                ipv6Available &&
                !string.Equals(
                    ipv4InterfaceId,
                    ipv6InterfaceId,
                    StringComparison.Ordinal))
            {
                return CreateRouteDecision(NetworkSelectionStatus.SplitRoute);
            }

            string selectedInterfaceId = ipv4Available
                ? ipv4InterfaceId
                : ipv6InterfaceId;
            NetworkRouteFamilies families = NetworkRouteFamilies.None;
            if (ipv4Available)
            {
                families |= NetworkRouteFamilies.IPv4;
            }

            if (ipv6Available)
            {
                families |= NetworkRouteFamilies.IPv6;
            }

            return new NetworkRouteDecision(
                NetworkSelectionStatus.Available,
                selectedInterfaceId,
                families);
        }

        internal static byte[] BuildSockaddrBytes(IPAddress address)
        {
            if (address == null)
            {
                throw new ArgumentNullException("address");
            }

            bool isIpv4 = address.AddressFamily == AddressFamily.InterNetwork;
            bool isIpv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
            if (!isIpv4 && !isIpv6)
            {
                throw new ArgumentException(
                    "Only IPv4 and IPv6 addresses are supported.",
                    "address");
            }

            byte[] socketAddress = new byte[isIpv4 ? 16 : 28];
            byte[] familyBytes = BitConverter.GetBytes(
                checked((ushort)address.AddressFamily));
            Buffer.BlockCopy(familyBytes, 0, socketAddress, 0, familyBytes.Length);

            byte[] addressBytes = address.GetAddressBytes();
            int addressOffset = isIpv4 ? 4 : 8;
            Buffer.BlockCopy(
                addressBytes,
                0,
                socketAddress,
                addressOffset,
                addressBytes.Length);

            if (isIpv6)
            {
                long scopeId = address.ScopeId;
                if (scopeId < 0 || scopeId > uint.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(
                        "address",
                        "The IPv6 scope identifier is outside the Windows range.");
                }

                byte[] scopeBytes = BitConverter.GetBytes((uint)scopeId);
                Buffer.BlockCopy(scopeBytes, 0, socketAddress, 24, scopeBytes.Length);
            }

            return socketAddress;
        }

        private static CandidateSelection SelectCandidate()
        {
            NetworkRouteLookupResult ipv4 = LookupBestInterface(Ipv4RouteProbe);
            NetworkRouteLookupResult ipv6 = LookupBestInterface(Ipv6RouteProbe);
            List<NetworkCandidate> candidates;

            try
            {
                candidates = EnumerateCandidates();
            }
            catch (Exception)
            {
                return CandidateSelection.CreateUnavailable(
                    NetworkSelectionStatus.RouteInterfaceMissing);
            }

            List<NetworkRouteIdentity> identities =
                new List<NetworkRouteIdentity>(candidates.Count);
            foreach (NetworkCandidate candidate in candidates)
            {
                identities.Add(new NetworkRouteIdentity(
                    candidate.Interface.Id,
                    candidate.Ipv4InterfaceIndex,
                    candidate.Ipv6InterfaceIndex));
            }

            NetworkRouteDecision decision = ResolveRoute(
                identities,
                ipv4,
                ipv6);
            if (decision.Status != NetworkSelectionStatus.Available)
            {
                return CandidateSelection.CreateUnavailable(decision.Status);
            }

            NetworkCandidate selected = null;
            foreach (NetworkCandidate candidate in candidates)
            {
                if (string.Equals(
                    candidate.Interface.Id,
                    decision.InterfaceId,
                    StringComparison.Ordinal))
                {
                    if (selected != null)
                    {
                        return CandidateSelection.CreateUnavailable(
                            NetworkSelectionStatus.RouteInterfaceMissing);
                    }

                    selected = candidate;
                }
            }

            return selected == null
                ? CandidateSelection.CreateUnavailable(
                    NetworkSelectionStatus.RouteInterfaceMissing)
                : CandidateSelection.CreateAvailable(selected, decision.Families);
        }

        private static List<NetworkCandidate> EnumerateCandidates()
        {
            List<NetworkCandidate> candidates = new List<NetworkCandidate>();
            foreach (NetworkInterface networkInterface in
                NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (!IsEligible(networkInterface) ||
                        string.IsNullOrEmpty(networkInterface.Id))
                    {
                        continue;
                    }

                    IPInterfaceProperties properties =
                        networkInterface.GetIPProperties();
                    candidates.Add(new NetworkCandidate(
                        networkInterface,
                        GetIpv4InterfaceIndex(properties),
                        GetIpv6InterfaceIndex(properties)));
                }
                catch (Exception)
                {
                    // A route that maps only to this interface will be reported as
                    // RouteInterfaceMissing rather than guessed from another adapter.
                }
            }

            return candidates;
        }

        private static NetworkRouteLookupResult LookupBestInterface(
            IPAddress destination)
        {
            AddressFamily family = destination == null
                ? AddressFamily.Unspecified
                : destination.AddressFamily;
            byte[] socketAddress;

            try
            {
                socketAddress = BuildSockaddrBytes(destination);
            }
            catch (Exception)
            {
                return NetworkRouteLookupResult.CreateFailed(family, 0);
            }

            IntPtr nativeAddress = IntPtr.Zero;
            try
            {
                nativeAddress = Marshal.AllocHGlobal(socketAddress.Length);
                Marshal.Copy(
                    socketAddress,
                    0,
                    nativeAddress,
                    socketAddress.Length);

                uint interfaceIndex;
                uint errorCode = GetBestInterfaceEx(
                    nativeAddress,
                    out interfaceIndex);
                if (errorCode == NoError && interfaceIndex != 0)
                {
                    return NetworkRouteLookupResult.CreateSuccess(
                        family,
                        interfaceIndex);
                }

                if (errorCode != NoError && IsNoRouteError(errorCode))
                {
                    return NetworkRouteLookupResult.CreateNoRoute(
                        family,
                        errorCode);
                }

                return NetworkRouteLookupResult.CreateFailed(family, errorCode);
            }
            catch (Exception)
            {
                return NetworkRouteLookupResult.CreateFailed(family, 0);
            }
            finally
            {
                if (nativeAddress != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nativeAddress);
                }
            }
        }

        internal static bool IsNoRouteError(uint errorCode)
        {
            return errorCode == ErrorNotSupported ||
                errorCode == ErrorNotFound ||
                errorCode == ErrorNoNetwork ||
                errorCode == ErrorNetworkUnreachable ||
                errorCode == ErrorHostUnreachable ||
                errorCode == WsaAddressFamilyNotSupported ||
                errorCode == WsaNetworkUnreachable ||
                errorCode == WsaHostUnreachable;
        }

        private static bool IsKnownLookupState(NetworkRouteLookupState state)
        {
            return state == NetworkRouteLookupState.Success ||
                state == NetworkRouteLookupState.NoRoute ||
                state == NetworkRouteLookupState.Failed;
        }

        private static bool TryFindUniqueInterfaceId(
            IList<NetworkRouteIdentity> interfaces,
            AddressFamily family,
            uint interfaceIndex,
            out string interfaceId)
        {
            interfaceId = string.Empty;
            int matchCount = 0;

            foreach (NetworkRouteIdentity identity in interfaces)
            {
                if (identity == null || string.IsNullOrEmpty(identity.InterfaceId))
                {
                    continue;
                }

                uint? candidateIndex = family == AddressFamily.InterNetwork
                    ? identity.Ipv4InterfaceIndex
                    : identity.Ipv6InterfaceIndex;
                if (!candidateIndex.HasValue ||
                    candidateIndex.Value != interfaceIndex)
                {
                    continue;
                }

                matchCount++;
                interfaceId = identity.InterfaceId;
                if (matchCount > 1)
                {
                    interfaceId = string.Empty;
                    return false;
                }
            }

            return matchCount == 1;
        }

        private static NetworkRouteDecision CreateRouteDecision(
            NetworkSelectionStatus status)
        {
            return new NetworkRouteDecision(
                status,
                string.Empty,
                NetworkRouteFamilies.None);
        }

        private static uint? GetIpv4InterfaceIndex(
            IPInterfaceProperties properties)
        {
            try
            {
                IPv4InterfaceProperties ipv4 = properties.GetIPv4Properties();
                return ipv4 == null || ipv4.Index <= 0
                    ? (uint?)null
                    : (uint)ipv4.Index;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static uint? GetIpv6InterfaceIndex(
            IPInterfaceProperties properties)
        {
            try
            {
                IPv6InterfaceProperties ipv6 = properties.GetIPv6Properties();
                return ipv6 == null || ipv6.Index <= 0
                    ? (uint?)null
                    : (uint)ipv6.Index;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsEligible(NetworkInterface networkInterface)
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                return false;
            }

            // Tunnel and PPP interfaces are valid only when the Windows route
            // lookup above names their interface index. Loopback is never a
            // user-facing network path for these counters.
            return networkInterface.NetworkInterfaceType !=
                NetworkInterfaceType.Loopback;
        }

        private static string FindGatewayAddress(
            NetworkInterface networkInterface,
            NetworkRouteFamilies families)
        {
            string selectedAddress = string.Empty;
            int selectedPreference = 0;
            IPInterfaceProperties properties = networkInterface.GetIPProperties();

            foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
            {
                IPAddress address = gateway.Address;
                if (address == null ||
                    IPAddress.Any.Equals(address) ||
                    IPAddress.IPv6Any.Equals(address) ||
                    IPAddress.Loopback.Equals(address) ||
                    IPAddress.IPv6Loopback.Equals(address))
                {
                    continue;
                }

                bool ipv4Allowed =
                    (families & NetworkRouteFamilies.IPv4) != 0 &&
                    address.AddressFamily == AddressFamily.InterNetwork;
                bool ipv6Allowed =
                    (families & NetworkRouteFamilies.IPv6) != 0 &&
                    address.AddressFamily == AddressFamily.InterNetworkV6;
                if (!ipv4Allowed && !ipv6Allowed)
                {
                    continue;
                }

                int preference = ipv4Allowed ? 2 : 1;
                string addressText = address.ToString();

                if (preference > selectedPreference ||
                    (preference == selectedPreference &&
                     string.CompareOrdinal(addressText, selectedAddress) < 0))
                {
                    selectedAddress = addressText;
                    selectedPreference = preference;
                }
            }

            return selectedAddress;
        }

        private static long? ReadLinkSpeed(NetworkInterface networkInterface)
        {
            try
            {
                long speed = networkInterface.Speed;
                return speed > 0 ? (long?)speed : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long? CalculateRate(long byteDelta, double elapsedSeconds)
        {
            if (byteDelta < 0 || elapsedSeconds <= 0.0)
            {
                return null;
            }

            double rate = byteDelta / elapsedSeconds;
            if (double.IsNaN(rate) ||
                double.IsInfinity(rate) ||
                rate < 0.0 ||
                rate > long.MaxValue)
            {
                return null;
            }

            return (long)Math.Round(rate, MidpointRounding.AwayFromZero);
        }

        private void StoreBaseline(
            string interfaceId,
            long receivedBytes,
            long sentBytes,
            long timestamp)
        {
            previousInterfaceId = interfaceId;
            previousReceivedBytes = receivedBytes;
            previousSentBytes = sentBytes;
            previousTimestamp = timestamp;
            hasBaseline = true;
        }

        private void ResetBaseline()
        {
            previousInterfaceId = null;
            previousReceivedBytes = 0;
            previousSentBytes = 0;
            previousTimestamp = 0;
            hasBaseline = false;
        }

        private sealed class NetworkCandidate
        {
            internal NetworkCandidate(
                NetworkInterface networkInterface,
                uint? ipv4InterfaceIndex,
                uint? ipv6InterfaceIndex)
            {
                Interface = networkInterface;
                Ipv4InterfaceIndex = ipv4InterfaceIndex;
                Ipv6InterfaceIndex = ipv6InterfaceIndex;
            }

            internal NetworkInterface Interface { get; private set; }
            internal uint? Ipv4InterfaceIndex { get; private set; }
            internal uint? Ipv6InterfaceIndex { get; private set; }
        }

        private sealed class CandidateSelection
        {
            private CandidateSelection(
                NetworkSelectionStatus status,
                NetworkCandidate candidate,
                NetworkRouteFamilies families)
            {
                Status = status;
                Candidate = candidate;
                Families = families;
            }

            internal NetworkSelectionStatus Status { get; private set; }
            internal NetworkCandidate Candidate { get; private set; }
            internal NetworkRouteFamilies Families { get; private set; }

            internal static CandidateSelection CreateAvailable(
                NetworkCandidate candidate,
                NetworkRouteFamilies families)
            {
                return new CandidateSelection(
                    NetworkSelectionStatus.Available,
                    candidate,
                    families);
            }

            internal static CandidateSelection CreateUnavailable(
                NetworkSelectionStatus status)
            {
                return new CandidateSelection(
                    status,
                    null,
                    NetworkRouteFamilies.None);
            }
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetBestInterfaceEx(
            IntPtr destinationAddress,
            out uint bestInterfaceIndex);
    }
}
