using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TinyHwBar
{
    internal enum UpdateCheckTrigger
    {
        Manual,
        ExplicitAutomatic
    }

    internal enum UpdateCheckStatus
    {
        Succeeded,
        AutomaticChecksDisabled,
        EndpointNotConfigured,
        EndpointRejected,
        TransportFailed,
        InvalidManifest
    }

    internal sealed class UpdateServiceOptions
    {
        internal bool AutomaticChecksEnabled { get; set; }
        internal string ManifestEndpoint { get; set; }

        internal static UpdateServiceOptions CreateDefault()
        {
            return new UpdateServiceOptions
            {
                AutomaticChecksEnabled = false,
                ManifestEndpoint = string.Empty
            };
        }
    }

    internal sealed class UpdateManifest
    {
        internal Version Version { get; private set; }
        internal Uri DownloadUri { get; private set; }
        internal string Sha256 { get; private set; }
        internal string ReleaseNotes { get; private set; }

        private UpdateManifest()
        {
        }

        internal static bool TryParse(string content, out UpdateManifest manifest)
        {
            manifest = null;
            if (string.IsNullOrWhiteSpace(content) ||
                Encoding.UTF8.GetByteCount(content) > UpdateService.MaximumManifestBytes)
            {
                return false;
            }

            Dictionary<string, string> values = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    return false;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (values.ContainsKey(key))
                {
                    return false;
                }

                values.Add(key, value);
            }

            string versionText;
            string downloadUrl;
            string sha256;
            string releaseNotes;
            Version version;
            Uri downloadUri;
            string endpointError;

            if (!values.TryGetValue("Version", out versionText) ||
                !values.TryGetValue("DownloadUrl", out downloadUrl) ||
                !values.TryGetValue("Sha256", out sha256) ||
                !Version.TryParse(versionText, out version) ||
                version == null ||
                version.Major < 0 ||
                !PublicHttpsEndpointPolicy.TryCreate(downloadUrl, out downloadUri, out endpointError) ||
                !IsSha256(sha256))
            {
                return false;
            }

            if (!values.TryGetValue("ReleaseNotes", out releaseNotes))
            {
                releaseNotes = string.Empty;
            }

            if (releaseNotes.Length > 512)
            {
                return false;
            }

            manifest = new UpdateManifest
            {
                Version = version,
                DownloadUri = downloadUri,
                Sha256 = sha256.ToUpperInvariant(),
                ReleaseNotes = releaseNotes
            };
            return true;
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool isHex = (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F');

                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class UpdateTransportResponse
    {
        internal UpdateTransportResponse(int statusCode, string content)
        {
            StatusCode = statusCode;
            Content = content ?? string.Empty;
        }

        internal int StatusCode { get; private set; }
        internal string Content { get; private set; }
    }

    internal interface IUpdateTransport
    {
        UpdateTransportResponse Get(Uri endpoint);
    }

    internal interface IEndpointAddressResolver
    {
        IPAddress[] Resolve(string hostName);
    }

    internal sealed class SystemEndpointAddressResolver : IEndpointAddressResolver
    {
        public IPAddress[] Resolve(string hostName)
        {
            return Dns.GetHostAddresses(hostName);
        }
    }

    internal sealed class HttpWebRequestUpdateTransport : IUpdateTransport
    {
        internal const string UserAgentValue = "TinyHwBar/3 update-check";

        private readonly IEndpointAddressResolver addressResolver;

        internal HttpWebRequestUpdateTransport()
            : this(new SystemEndpointAddressResolver())
        {
        }

        internal HttpWebRequestUpdateTransport(IEndpointAddressResolver addressResolver)
        {
            if (addressResolver == null)
            {
                throw new ArgumentNullException("addressResolver");
            }

            this.addressResolver = addressResolver;
        }

        public UpdateTransportResponse Get(Uri endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException("endpoint");
            }

            PublicHttpsEndpointPolicy.PreflightEndpointAddresses(endpoint, addressResolver);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.MaximumResponseHeadersLength = 8;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.PreAuthenticate = false;
            request.UserAgent = UserAgentValue;

            HttpWebResponse response = null;
            try
            {
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (WebException exception)
                {
                    if (exception.Response != null)
                    {
                        exception.Response.Dispose();
                    }

                    throw;
                }

                if (response.ContentLength > UpdateService.MaximumManifestBytes)
                {
                    throw new InvalidDataException("The update manifest is too large.");
                }

                using (Stream responseStream = response.GetResponseStream())
                {
                    string content = ReadBoundedUtf8(
                        responseStream,
                        UpdateService.MaximumManifestBytes);

                    return new UpdateTransportResponse((int)response.StatusCode, content);
                }
            }
            finally
            {
                if (response != null)
                {
                    response.Dispose();
                }
            }
        }

        private static string ReadBoundedUtf8(Stream stream, int maximumBytes)
        {
            if (stream == null)
            {
                return string.Empty;
            }

            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[4096];
                while (true)
                {
                    int read = stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (buffer.Length + read > maximumBytes)
                    {
                        throw new InvalidDataException("The update manifest is too large.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
        }
    }

    internal sealed class UpdateCheckResult
    {
        internal UpdateCheckStatus Status { get; private set; }
        internal UpdateManifest Manifest { get; private set; }
        internal bool UpdateAvailable { get; private set; }

        private UpdateCheckResult()
        {
        }

        internal static UpdateCheckResult Create(UpdateCheckStatus status)
        {
            return new UpdateCheckResult
            {
                Status = status,
                Manifest = null,
                UpdateAvailable = false
            };
        }

        internal static UpdateCheckResult Success(
            UpdateManifest manifest,
            bool updateAvailable)
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Succeeded,
                Manifest = manifest,
                UpdateAvailable = updateAvailable
            };
        }
    }

    internal sealed class UpdateService
    {
        internal const int MaximumManifestBytes = 64 * 1024;

        private readonly Version currentVersion;
        private readonly UpdateServiceOptions options;
        private readonly IUpdateTransport transport;

        internal UpdateService(Version currentVersion)
            : this(
                currentVersion,
                UpdateServiceOptions.CreateDefault(),
                new HttpWebRequestUpdateTransport())
        {
        }

        internal UpdateService(
            Version currentVersion,
            UpdateServiceOptions options,
            IUpdateTransport transport)
        {
            if (currentVersion == null)
            {
                throw new ArgumentNullException("currentVersion");
            }

            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }

            this.currentVersion = currentVersion;
            this.options = options;
            this.transport = transport;
        }

        internal UpdateCheckResult CheckNow(UpdateCheckTrigger trigger)
        {
            if (trigger != UpdateCheckTrigger.Manual &&
                trigger != UpdateCheckTrigger.ExplicitAutomatic)
            {
                throw new ArgumentOutOfRangeException("trigger");
            }

            if (trigger == UpdateCheckTrigger.ExplicitAutomatic &&
                !options.AutomaticChecksEnabled)
            {
                return UpdateCheckResult.Create(UpdateCheckStatus.AutomaticChecksDisabled);
            }

            if (string.IsNullOrWhiteSpace(options.ManifestEndpoint))
            {
                return UpdateCheckResult.Create(UpdateCheckStatus.EndpointNotConfigured);
            }

            Uri endpoint;
            string endpointError;
            if (!PublicHttpsEndpointPolicy.TryCreate(
                options.ManifestEndpoint,
                out endpoint,
                out endpointError))
            {
                return UpdateCheckResult.Create(UpdateCheckStatus.EndpointRejected);
            }

            UpdateTransportResponse response;
            try
            {
                response = transport.Get(endpoint);
            }
            catch (Exception)
            {
                return UpdateCheckResult.Create(UpdateCheckStatus.TransportFailed);
            }

            if (response == null || response.StatusCode != (int)HttpStatusCode.OK)
            {
                return UpdateCheckResult.Create(UpdateCheckStatus.TransportFailed);
            }

            UpdateManifest manifest;
            if (!UpdateManifest.TryParse(response.Content, out manifest))
            {
                return UpdateCheckResult.Create(UpdateCheckStatus.InvalidManifest);
            }

            return UpdateCheckResult.Success(
                manifest,
                manifest.Version.CompareTo(currentVersion) > 0);
        }
    }

    internal static class PublicHttpsEndpointPolicy
    {
        internal static bool TryCreate(string endpointText, out Uri endpoint, out string error)
        {
            endpoint = null;
            error = null;

            if (string.IsNullOrWhiteSpace(endpointText))
            {
                error = "An endpoint is required.";
                return false;
            }

            Uri candidate;
            if (!Uri.TryCreate(endpointText.Trim(), UriKind.Absolute, out candidate) ||
                !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = "The endpoint must be an absolute HTTPS URI.";
                return false;
            }

            if (!string.IsNullOrEmpty(candidate.UserInfo) ||
                !string.IsNullOrEmpty(candidate.Query) ||
                !string.IsNullOrEmpty(candidate.Fragment))
            {
                error = "User information, query strings and fragments are not permitted in endpoints.";
                return false;
            }

            IPAddress address;
            if (IPAddress.TryParse(candidate.DnsSafeHost, out address))
            {
                if (!IsPublicUnicast(address))
                {
                    error = "The endpoint address is not public unicast.";
                    return false;
                }
            }
            else
            {
                string host = candidate.DnsSafeHost.TrimEnd('.');
                if (Uri.CheckHostName(host) != UriHostNameType.Dns ||
                    host.IndexOf('.') <= 0 ||
                    IsLocalDnsName(host))
                {
                    error = "The endpoint host must be a public DNS name.";
                    return false;
                }
            }

            endpoint = candidate;
            return true;
        }

        internal static void PreflightEndpointAddresses(
            Uri endpoint,
            IEndpointAddressResolver addressResolver)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException("endpoint");
            }

            if (addressResolver == null)
            {
                throw new ArgumentNullException("addressResolver");
            }

            Uri validatedEndpoint;
            string validationError;
            if (!TryCreate(endpoint.AbsoluteUri, out validatedEndpoint, out validationError))
            {
                throw new InvalidOperationException(validationError);
            }

            // This is an address preflight before HttpWebRequest performs its own
            // connection lookup. It does not pin the connection address and cannot
            // completely prevent DNS rebinding.
            IPAddress literalAddress;
            if (IPAddress.TryParse(validatedEndpoint.DnsSafeHost, out literalAddress))
            {
                return;
            }

            IPAddress[] resolvedAddresses = addressResolver.Resolve(
                validatedEndpoint.DnsSafeHost);
            if (!AreAllAddressesPublic(resolvedAddresses))
            {
                throw new InvalidOperationException(
                    "The endpoint DNS name resolved to a non-public address.");
            }
        }

        internal static bool AreAllAddressesPublic(IPAddress[] addresses)
        {
            if (addresses == null || addresses.Length == 0)
            {
                return false;
            }

            for (int index = 0; index < addresses.Length; index++)
            {
                if (!IsPublicUnicast(addresses[index]))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsPublicUnicast(IPAddress address)
        {
            if (address == null || IPAddress.IsLoopback(address))
            {
                return false;
            }

            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte first = bytes[0];
                byte second = bytes[1];
                byte third = bytes[2];

                if (first == 0 ||
                    first == 10 ||
                    first == 127 ||
                    first >= 224 ||
                    (first == 100 && second >= 64 && second <= 127) ||
                    (first == 169 && second == 254) ||
                    (first == 172 && second >= 16 && second <= 31) ||
                    (first == 192 && second == 168) ||
                    (first == 192 && second == 0 && (third == 0 || third == 2)) ||
                    (first == 192 && second == 88 && third == 99) ||
                    (first == 198 && (second == 18 || second == 19)) ||
                    (first == 198 && second == 51 && third == 100) ||
                    (first == 203 && second == 0 && third == 113))
                {
                    return false;
                }

                return true;
            }

            if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
                address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.IPv6None) ||
                address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast ||
                address.IsIPv6SiteLocal)
            {
                return false;
            }

            if ((bytes[0] & 0xE0) != 0x20)
            {
                return false;
            }

            // IANA marks 2001::/23 as special-purpose and not globally
            // reachable unless a more-specific allocation says otherwise.
            // Keep only the currently registered globally reachable exceptions.
            if (IsInIpv6Prefix(bytes, "2001::", 23))
            {
                return IsInIpv6Prefix(bytes, "2001:1::1", 128) ||
                    IsInIpv6Prefix(bytes, "2001:1::2", 128) ||
                    IsInIpv6Prefix(bytes, "2001:1::3", 128) ||
                    IsInIpv6Prefix(bytes, "2001:3::", 32) ||
                    IsInIpv6Prefix(bytes, "2001:4:112::", 48) ||
                    IsInIpv6Prefix(bytes, "2001:20::", 28) ||
                    IsInIpv6Prefix(bytes, "2001:30::", 28);
            }

            // Documentation and transition prefixes are not suitable public
            // service endpoints. 6to4 has no unconditional global-reachability
            // guarantee in the IANA special-purpose registry.
            return !IsInIpv6Prefix(bytes, "2001:db8::", 32) &&
                !IsInIpv6Prefix(bytes, "2002::", 16) &&
                !IsInIpv6Prefix(bytes, "3fff::", 20);
        }

        private static bool IsInIpv6Prefix(
            byte[] addressBytes,
            string prefixText,
            int prefixLength)
        {
            byte[] prefixBytes = IPAddress.Parse(prefixText).GetAddressBytes();
            int completeBytes = prefixLength / 8;
            int remainingBits = prefixLength % 8;

            for (int index = 0; index < completeBytes; index++)
            {
                if (addressBytes[index] != prefixBytes[index])
                {
                    return false;
                }
            }

            if (remainingBits == 0)
            {
                return true;
            }

            int mask = 0xFF << (8 - remainingBits);
            return (addressBytes[completeBytes] & mask) ==
                (prefixBytes[completeBytes] & mask);
        }

        private static bool IsLocalDnsName(string host)
        {
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".home", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
        }
    }
}
