using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace TinyHwBar
{
    internal enum TelemetrySendStatus
    {
        Succeeded,
        UserConfirmationRequired,
        TelemetryDisabled,
        EndpointNotConfigured,
        EndpointRejected,
        PreviewInvalid,
        TransportFailed
    }

    internal sealed class TelemetryOptions
    {
        internal bool Enabled { get; set; }
        internal string Endpoint { get; set; }

        internal static TelemetryOptions CreateDefault()
        {
            return new TelemetryOptions
            {
                Enabled = false,
                Endpoint = string.Empty
            };
        }
    }

    internal sealed class TelemetryDraft
    {
        internal TelemetryDraft(string eventName, string appVersion, string outcomeCode)
        {
            EventName = eventName;
            AppVersion = appVersion;
            OutcomeCode = outcomeCode;
        }

        internal string EventName { get; private set; }
        internal string AppVersion { get; private set; }
        internal string OutcomeCode { get; private set; }
    }

    internal enum DiagnosticLogSeverity
    {
        Information,
        Warning,
        Error
    }

    internal sealed class DiagnosticLogEntry
    {
        internal DiagnosticLogEntry(
            DiagnosticLogSeverity severity,
            string diagnosticCode,
            int occurrenceCount)
        {
            Severity = severity;
            DiagnosticCode = diagnosticCode;
            OccurrenceCount = occurrenceCount;
        }

        internal DiagnosticLogSeverity Severity { get; private set; }
        internal string DiagnosticCode { get; private set; }
        internal int OccurrenceCount { get; private set; }
    }

    internal sealed class DiagnosticLogDraft
    {
        private readonly DiagnosticLogEntry[] entries;

        internal DiagnosticLogDraft(
            string appVersion,
            DiagnosticLogEntry[] entries)
        {
            AppVersion = appVersion;
            this.entries = entries == null
                ? null
                : (DiagnosticLogEntry[])entries.Clone();
        }

        internal string AppVersion { get; private set; }

        internal DiagnosticLogEntry[] GetEntries()
        {
            return entries == null
                ? null
                : (DiagnosticLogEntry[])entries.Clone();
        }
    }

    internal sealed class TelemetryPreview
    {
        private readonly object synchronization = new object();
        private readonly object owner;
        private readonly string[] fieldNames;
        private bool sending;
        private bool sent;

        internal TelemetryPreview(
            object owner,
            string payloadJson,
            string[] fieldNames,
            string destinationEndpoint)
        {
            this.owner = owner;
            PayloadJson = payloadJson;
            this.fieldNames = (string[])fieldNames.Clone();
            DestinationEndpoint = destinationEndpoint ?? string.Empty;
            Utf8ByteCount = Encoding.UTF8.GetByteCount(payloadJson);
        }

        internal string PayloadJson { get; private set; }
        internal int Utf8ByteCount { get; private set; }
        internal string DestinationEndpoint { get; private set; }

        internal string[] GetFieldNames()
        {
            return (string[])fieldNames.Clone();
        }

        internal bool BelongsTo(object expectedOwner)
        {
            return object.ReferenceEquals(owner, expectedOwner);
        }

        internal bool TryBeginSend()
        {
            lock (synchronization)
            {
                if (sending || sent)
                {
                    return false;
                }

                sending = true;
                return true;
            }
        }

        internal void FinishSend(bool succeeded)
        {
            lock (synchronization)
            {
                sending = false;
                if (succeeded)
                {
                    sent = true;
                }
            }
        }
    }

    internal interface ITelemetryTransport
    {
        int PostJson(Uri endpoint, string payloadJson);
    }

    internal sealed class HttpWebRequestTelemetryTransport : ITelemetryTransport
    {
        private readonly IEndpointAddressResolver addressResolver;

        internal HttpWebRequestTelemetryTransport()
            : this(new SystemEndpointAddressResolver())
        {
        }

        internal HttpWebRequestTelemetryTransport(IEndpointAddressResolver addressResolver)
        {
            if (addressResolver == null)
            {
                throw new ArgumentNullException("addressResolver");
            }

            this.addressResolver = addressResolver;
        }

        public int PostJson(Uri endpoint, string payloadJson)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException("endpoint");
            }

            PublicHttpsEndpointPolicy.PreflightEndpointAddresses(endpoint, addressResolver);

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson ?? string.Empty);
            if (payloadBytes.Length > TelemetryService.MaximumPayloadBytes)
            {
                throw new InvalidDataException("The telemetry payload is too large.");
            }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = payloadBytes.Length;
            request.AllowAutoRedirect = false;
            request.AllowWriteStreamBuffering = false;
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.MaximumResponseHeadersLength = 8;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.PreAuthenticate = false;
            request.UserAgent = "TinyHwBar/2 telemetry";

            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(payloadBytes, 0, payloadBytes.Length);
            }

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

                return (int)response.StatusCode;
            }
            finally
            {
                if (response != null)
                {
                    response.Dispose();
                }
            }
        }
    }

    internal sealed class TelemetrySendResult
    {
        internal TelemetrySendResult(TelemetrySendStatus status)
        {
            Status = status;
        }

        internal TelemetrySendStatus Status { get; private set; }
    }

    internal sealed class TelemetryService
    {
        internal const int MaximumPayloadBytes = 2048;
        internal const int MaximumDiagnosticLogEntries = 8;

        private readonly object previewOwner = new object();
        private readonly TelemetryOptions options;
        private readonly ITelemetryTransport transport;

        internal TelemetryService()
            : this(TelemetryOptions.CreateDefault(), new HttpWebRequestTelemetryTransport())
        {
        }

        internal TelemetryService(
            TelemetryOptions options,
            ITelemetryTransport transport)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }

            this.options = options;
            this.transport = transport;
        }

        internal TelemetryPreview BuildPreview(TelemetryDraft draft)
        {
            if (draft == null)
            {
                throw new ArgumentNullException("draft");
            }

            ValidateSafeToken(draft.EventName, "eventName", 32);
            ValidateSafeToken(draft.AppVersion, "appVersion", 32);
            ValidateSafeToken(draft.OutcomeCode, "outcomeCode", 32);

            string payload = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"schemaVersion\":1,\"eventName\":\"{0}\",\"appVersion\":\"{1}\",\"outcomeCode\":\"{2}\"}}",
                draft.EventName,
                draft.AppVersion,
                draft.OutcomeCode);

            if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
            {
                throw new InvalidOperationException("The telemetry preview is too large.");
            }

            return new TelemetryPreview(
                previewOwner,
                payload,
                new[] { "schemaVersion", "eventName", "appVersion", "outcomeCode" },
                GetEndpointIdentity(options.Endpoint));
        }

        internal TelemetryPreview BuildDiagnosticLogPreview(DiagnosticLogDraft draft)
        {
            if (draft == null)
            {
                throw new ArgumentNullException("draft");
            }

            ValidateSafeToken(draft.AppVersion, "appVersion", 32);
            DiagnosticLogEntry[] entries = draft.GetEntries();
            if (entries == null ||
                entries.Length == 0 ||
                entries.Length > MaximumDiagnosticLogEntries)
            {
                throw new ArgumentException(
                    "A diagnostic log preview requires between one and eight entries.",
                    "draft");
            }

            StringBuilder payload = new StringBuilder();
            payload.Append("{\"schemaVersion\":1,");
            payload.Append("\"payloadType\":\"diagnostic-log-summary\",");
            payload.Append("\"appVersion\":\"");
            payload.Append(draft.AppVersion);
            payload.Append("\",\"entries\":[");

            for (int index = 0; index < entries.Length; index++)
            {
                DiagnosticLogEntry entry = entries[index];
                if (entry == null)
                {
                    throw new ArgumentException(
                        "Diagnostic log entries cannot be null.",
                        "draft");
                }

                ValidateSafeToken(entry.DiagnosticCode, "diagnosticCode", 48);
                if (entry.OccurrenceCount < 1 || entry.OccurrenceCount > 9999)
                {
                    throw new ArgumentException(
                        "Diagnostic occurrence counts must be between 1 and 9999.",
                        "draft");
                }

                string severity = GetSeverityName(entry.Severity);
                if (index > 0)
                {
                    payload.Append(',');
                }

                payload.Append("{\"severity\":\"");
                payload.Append(severity);
                payload.Append("\",\"diagnosticCode\":\"");
                payload.Append(entry.DiagnosticCode);
                payload.Append("\",\"occurrenceCount\":");
                payload.Append(entry.OccurrenceCount.ToString(CultureInfo.InvariantCulture));
                payload.Append('}');
            }

            payload.Append("]}");
            string payloadJson = payload.ToString();
            if (Encoding.UTF8.GetByteCount(payloadJson) > MaximumPayloadBytes)
            {
                throw new InvalidOperationException(
                    "The diagnostic log preview is too large.");
            }

            return new TelemetryPreview(
                previewOwner,
                payloadJson,
                new[]
                {
                    "schemaVersion",
                    "payloadType",
                    "appVersion",
                    "entries.severity",
                    "entries.diagnosticCode",
                    "entries.occurrenceCount"
                },
                GetEndpointIdentity(options.Endpoint));
        }

        internal TelemetrySendResult ConfirmAndSend(
            TelemetryPreview preview,
            bool userConfirmed)
        {
            if (!userConfirmed)
            {
                return new TelemetrySendResult(
                    TelemetrySendStatus.UserConfirmationRequired);
            }

            if (!options.Enabled)
            {
                return new TelemetrySendResult(TelemetrySendStatus.TelemetryDisabled);
            }

            if (string.IsNullOrWhiteSpace(options.Endpoint))
            {
                return new TelemetrySendResult(TelemetrySendStatus.EndpointNotConfigured);
            }

            Uri endpoint;
            string endpointError;
            if (!PublicHttpsEndpointPolicy.TryCreate(
                options.Endpoint,
                out endpoint,
                out endpointError))
            {
                return new TelemetrySendResult(TelemetrySendStatus.EndpointRejected);
            }

            if (preview == null ||
                !preview.BelongsTo(previewOwner) ||
                !string.Equals(
                    preview.DestinationEndpoint,
                    endpoint.AbsoluteUri,
                    StringComparison.Ordinal) ||
                preview.Utf8ByteCount > MaximumPayloadBytes ||
                !preview.TryBeginSend())
            {
                return new TelemetrySendResult(TelemetrySendStatus.PreviewInvalid);
            }

            bool sent = false;
            try
            {
                int statusCode = transport.PostJson(endpoint, preview.PayloadJson);
                sent = statusCode >= 200 && statusCode <= 299;
                return new TelemetrySendResult(
                    sent
                        ? TelemetrySendStatus.Succeeded
                        : TelemetrySendStatus.TransportFailed);
            }
            catch (Exception)
            {
                return new TelemetrySendResult(TelemetrySendStatus.TransportFailed);
            }
            finally
            {
                preview.FinishSend(sent);
            }
        }

        private static void ValidateSafeToken(string value, string parameterName, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            {
                throw new ArgumentException(
                    "A short non-empty value is required.",
                    parameterName);
            }

            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool isSafe = (character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '.' ||
                    character == '_' ||
                    character == '-';

                if (!isSafe)
                {
                    throw new ArgumentException(
                        "Only ASCII letters, digits, dots, underscores and hyphens are allowed.",
                        parameterName);
                }
            }
        }

        private static string GetEndpointIdentity(string endpointText)
        {
            Uri endpoint;
            string endpointError;
            return PublicHttpsEndpointPolicy.TryCreate(
                endpointText,
                out endpoint,
                out endpointError)
                ? endpoint.AbsoluteUri
                : string.Empty;
        }

        private static string GetSeverityName(DiagnosticLogSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticLogSeverity.Information:
                    return "information";
                case DiagnosticLogSeverity.Warning:
                    return "warning";
                case DiagnosticLogSeverity.Error:
                    return "error";
                default:
                    throw new ArgumentOutOfRangeException("severity");
            }
        }
    }
}
