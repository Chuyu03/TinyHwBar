using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TinyHwBar
{
    internal sealed class LoopbackApiOptions
    {
        internal bool Enabled { get; set; }
        internal IPAddress BindAddress { get; set; }

        internal static LoopbackApiOptions CreateDefault()
        {
            return new LoopbackApiOptions
            {
                Enabled = false,
                BindAddress = IPAddress.Loopback
            };
        }
    }

    internal sealed class LoopbackApiSession
    {
        internal LoopbackApiSession(IPAddress address, int port, string bearerToken)
        {
            Address = address;
            Port = port;
            BearerToken = bearerToken;
        }

        internal IPAddress Address { get; private set; }
        internal int Port { get; private set; }
        internal string BearerToken { get; private set; }
    }

    internal sealed class LoopbackApiServer : IDisposable
    {
        internal const int MinimumPort = 49152;
        internal const int MaximumPort = 65535;
        internal const int MaximumRequestHeaderBytes = 8 * 1024;
        internal const int MaximumResponseBytes = 32 * 1024;
        internal const int RequestHeaderDeadlineMilliseconds = 5000;

        private const int SocketTimeoutMilliseconds = 2000;
        private const int MaximumBindAttempts = 32;

        private readonly object synchronization = new object();
        private readonly LoopbackApiOptions options;
        private readonly Func<string> statusJsonProvider;

        private ServerRun currentRun;
        private bool disposed;

        internal LoopbackApiServer(
            LoopbackApiOptions options,
            Func<string> statusJsonProvider)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            if (statusJsonProvider == null)
            {
                throw new ArgumentNullException("statusJsonProvider");
            }

            this.options = options;
            this.statusJsonProvider = statusJsonProvider;
        }

        internal bool IsRunning
        {
            get
            {
                lock (synchronization)
                {
                    return currentRun != null && !currentRun.Stopping;
                }
            }
        }

        internal LoopbackApiSession Start()
        {
            lock (synchronization)
            {
                ThrowIfDisposed();

                if (!options.Enabled)
                {
                    throw new InvalidOperationException(
                        "The loopback API must be explicitly enabled before it can start.");
                }

                IPAddress address = options.BindAddress ?? IPAddress.Loopback;
                if (!IsAllowedBindAddress(address))
                {
                    throw new InvalidOperationException(
                        "The API can bind only to IPv4 or IPv6 loopback.");
                }

                if (currentRun != null)
                {
                    throw new InvalidOperationException(
                        currentRun.Stopping
                            ? "The previous loopback API worker has not exited yet."
                            : "The loopback API is already running.");
                }

                TcpListener newListener = BindRandomHighPort(address);
                IPEndPoint localEndpoint = (IPEndPoint)newListener.LocalEndpoint;
                string newBearerToken = CreateBearerToken();
                ServerRun newRun = new ServerRun(newListener, newBearerToken);
                Thread newWorker = new Thread(
                    new ThreadStart(delegate { AcceptLoop(newRun); }));
                newWorker.IsBackground = true;
                newWorker.Name = "TinyHwBar loopback API";
                newRun.Worker = newWorker;

                currentRun = newRun;

                try
                {
                    newWorker.Start();
                }
                catch
                {
                    currentRun = null;
                    newRun.Stopping = true;
                    newRun.BearerToken = null;
                    newListener.Stop();
                    throw;
                }

                return new LoopbackApiSession(address, localEndpoint.Port, newBearerToken);
            }
        }

        internal void Stop()
        {
            ServerRun run;
            TcpClient clientToClose;

            lock (synchronization)
            {
                run = currentRun;
                if (run == null)
                {
                    return;
                }

                run.Stopping = true;
                run.BearerToken = null;
                clientToClose = run.ActiveClient;
                run.ActiveClient = null;
            }

            TryStopListener(run.Listener);
            TryCloseClient(clientToClose);

            if (run.Worker != null &&
                run.Worker != Thread.CurrentThread &&
                run.Worker.IsAlive)
            {
                run.Worker.Join(SocketTimeoutMilliseconds + 500);
            }

            lock (synchronization)
            {
                if (object.ReferenceEquals(currentRun, run) &&
                    (run.Worker == null || !run.Worker.IsAlive))
                {
                    currentRun = null;
                }
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
            }

            Stop();
        }

        internal static bool IsAllowedBindAddress(IPAddress address)
        {
            return address != null &&
                (address.Equals(IPAddress.Loopback) ||
                 address.Equals(IPAddress.IPv6Loopback));
        }

        internal static bool FixedTimeEquals(string expected, string supplied)
        {
            string safeExpected = expected ?? string.Empty;
            string safeSupplied = supplied ?? string.Empty;
            int difference = safeExpected.Length ^ safeSupplied.Length;

            for (int index = 0; index < safeExpected.Length; index++)
            {
                char suppliedCharacter = index < safeSupplied.Length
                    ? safeSupplied[index]
                    : '\0';

                difference |= safeExpected[index] ^ suppliedCharacter;
            }

            return difference == 0;
        }

        internal static int CreateRandomHighPort()
        {
            byte[] randomBytes = new byte[4];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(randomBytes);
            }

            uint value = BitConverter.ToUInt32(randomBytes, 0);
            uint range = (uint)(MaximumPort - MinimumPort + 1);
            return MinimumPort + (int)(value % range);
        }

        private static TcpListener BindRandomHighPort(IPAddress address)
        {
            SocketException lastError = null;

            for (int attempt = 0; attempt < MaximumBindAttempts; attempt++)
            {
                TcpListener candidate = new TcpListener(address, CreateRandomHighPort());
                try
                {
                    candidate.Server.ExclusiveAddressUse = true;
                    if (address.Equals(IPAddress.IPv6Loopback))
                    {
                        candidate.Server.DualMode = false;
                    }

                    candidate.Start(8);
                    return candidate;
                }
                catch (SocketException exception)
                {
                    lastError = exception;
                    candidate.Stop();
                }
            }

            throw new InvalidOperationException(
                "Could not reserve a random high loopback port.",
                lastError);
        }

        private static string CreateBearerToken()
        {
            byte[] tokenBytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(tokenBytes);
            }

            return BitConverter.ToString(tokenBytes).Replace("-", string.Empty);
        }

        private void AcceptLoop(ServerRun run)
        {
            try
            {
                while (!run.Stopping)
                {
                    TcpClient client = null;
                    try
                    {
                        client = run.Listener.AcceptTcpClient();
                        lock (synchronization)
                        {
                            if (run.Stopping)
                            {
                                return;
                            }

                            run.ActiveClient = client;
                        }

                        ProcessClient(client, run);
                    }
                    catch (ObjectDisposedException)
                    {
                        if (run.Stopping)
                        {
                            return;
                        }
                    }
                    catch (SocketException)
                    {
                        if (run.Stopping)
                        {
                            return;
                        }
                    }
                    catch (IOException)
                    {
                        // A malformed or timed-out local request must not stop the server.
                    }
                    finally
                    {
                        if (client != null)
                        {
                            lock (synchronization)
                            {
                                if (object.ReferenceEquals(run.ActiveClient, client))
                                {
                                    run.ActiveClient = null;
                                }
                            }

                            TryCloseClient(client);
                        }
                    }
                }
            }
            finally
            {
                TcpClient clientToClose;
                run.Stopping = true;
                run.BearerToken = null;
                TryStopListener(run.Listener);

                lock (synchronization)
                {
                    clientToClose = run.ActiveClient;
                    run.ActiveClient = null;
                    if (object.ReferenceEquals(currentRun, run))
                    {
                        currentRun = null;
                    }
                }

                TryCloseClient(clientToClose);
            }
        }

        private void ProcessClient(TcpClient client, ServerRun run)
        {
            client.ReceiveTimeout = SocketTimeoutMilliseconds;
            client.SendTimeout = SocketTimeoutMilliseconds;
            client.NoDelay = true;

            using (NetworkStream stream = client.GetStream())
            {
                string headerBlock = ReadHeaderBlock(stream);
                if (headerBlock == null)
                {
                    WriteResponse(stream, 400, "Bad Request", string.Empty);
                    return;
                }

                ParsedRequest request;
                if (!ParsedRequest.TryParse(headerBlock, out request))
                {
                    WriteResponse(stream, 400, "Bad Request", string.Empty);
                    return;
                }

                if (!string.Equals(request.Method, "GET", StringComparison.Ordinal) ||
                    !string.Equals(request.Path, "/v1/status", StringComparison.Ordinal))
                {
                    WriteResponse(stream, 404, "Not Found", string.Empty);
                    return;
                }

                string expectedToken = run.BearerToken;

                if (string.IsNullOrEmpty(expectedToken) ||
                    !FixedTimeEquals(expectedToken, request.BearerToken))
                {
                    WriteResponse(stream, 401, "Unauthorized", string.Empty);
                    return;
                }

                string responseJson;
                try
                {
                    responseJson = statusJsonProvider() ?? "{}";
                }
                catch (Exception)
                {
                    WriteResponse(stream, 500, "Internal Server Error", string.Empty);
                    return;
                }

                if (Encoding.UTF8.GetByteCount(responseJson) > MaximumResponseBytes)
                {
                    WriteResponse(stream, 500, "Internal Server Error", string.Empty);
                    return;
                }

                WriteResponse(stream, 200, "OK", responseJson);
            }
        }

        internal static int GetHeaderReadTimeoutMilliseconds(long elapsedMilliseconds)
        {
            if (elapsedMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException("elapsedMilliseconds");
            }

            long remaining = RequestHeaderDeadlineMilliseconds - elapsedMilliseconds;
            if (remaining <= 0)
            {
                return 0;
            }

            return (int)Math.Min(SocketTimeoutMilliseconds, remaining);
        }

        private static string ReadHeaderBlock(NetworkStream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            Stopwatch deadline = Stopwatch.StartNew();
            using (MemoryStream buffer = new MemoryStream())
            {
                int state = 0;
                while (buffer.Length < MaximumRequestHeaderBytes)
                {
                    int readTimeout = GetHeaderReadTimeoutMilliseconds(
                        deadline.ElapsedMilliseconds);
                    if (readTimeout <= 0)
                    {
                        return null;
                    }

                    stream.ReadTimeout = readTimeout;
                    int value = stream.ReadByte();
                    if (value < 0)
                    {
                        return null;
                    }

                    if (deadline.ElapsedMilliseconds >= RequestHeaderDeadlineMilliseconds)
                    {
                        return null;
                    }

                    buffer.WriteByte((byte)value);
                    if ((state == 0 || state == 2) && value == '\r')
                    {
                        state++;
                    }
                    else if ((state == 1 || state == 3) && value == '\n')
                    {
                        state++;
                        if (state == 4)
                        {
                            return Encoding.ASCII.GetString(buffer.ToArray());
                        }
                    }
                    else
                    {
                        state = value == '\r' ? 1 : 0;
                    }
                }

                return null;
            }
        }

        private static void WriteResponse(
            NetworkStream stream,
            int statusCode,
            string reason,
            string responseJson)
        {
            byte[] body = Encoding.UTF8.GetBytes(responseJson ?? string.Empty);
            string headers = string.Format(
                CultureInfo.InvariantCulture,
                "HTTP/1.1 {0} {1}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: {2}\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Connection: close\r\n\r\n",
                statusCode,
                reason,
                body.Length);

            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0)
            {
                stream.Write(body, 0, body.Length);
            }

            stream.Flush();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("LoopbackApiServer");
            }
        }

        private static void TryCloseClient(TcpClient client)
        {
            if (client == null)
            {
                return;
            }

            try
            {
                client.Close();
            }
            catch (SocketException)
            {
                // Closing an already-failed local socket is best effort.
            }
            catch (ObjectDisposedException)
            {
                // Closing an already-closed local socket is harmless.
            }
        }

        private static void TryStopListener(TcpListener listener)
        {
            if (listener == null)
            {
                return;
            }

            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
                // Stopping an already-failed local listener is best effort.
            }
            catch (ObjectDisposedException)
            {
                // Stopping an already-closed local listener is harmless.
            }
        }

        private sealed class ServerRun
        {
            internal ServerRun(TcpListener listener, string bearerToken)
            {
                Listener = listener;
                BearerToken = bearerToken;
            }

            internal TcpListener Listener { get; private set; }
            internal Thread Worker { get; set; }
            internal string BearerToken { get; set; }
            internal TcpClient ActiveClient { get; set; }
            internal volatile bool Stopping;
        }

        private sealed class ParsedRequest
        {
            internal string Method { get; private set; }
            internal string Path { get; private set; }
            internal string BearerToken { get; private set; }

            internal static bool TryParse(string headerBlock, out ParsedRequest request)
            {
                request = null;
                string[] lines = headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length < 2)
                {
                    return false;
                }

                string[] requestParts = lines[0].Split(' ');
                if (requestParts.Length != 3 ||
                    !string.Equals(requestParts[2], "HTTP/1.1", StringComparison.Ordinal))
                {
                    return false;
                }

                string bearerToken = null;
                bool hasAuthorization = false;
                for (int index = 1; index < lines.Length; index++)
                {
                    string line = lines[index];
                    if (line.Length == 0)
                    {
                        break;
                    }

                    int separator = line.IndexOf(':');
                    if (separator <= 0)
                    {
                        return false;
                    }

                    string name = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        if (hasAuthorization)
                        {
                            return false;
                        }

                        hasAuthorization = true;
                        int schemeSeparator = value.IndexOf(' ');
                        if (schemeSeparator <= 0 ||
                            !string.Equals(
                                value.Substring(0, schemeSeparator),
                                "Bearer",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        bearerToken = value.Substring(schemeSeparator + 1).Trim();
                    }
                    else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    else if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "0", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                request = new ParsedRequest
                {
                    Method = requestParts[0],
                    Path = requestParts[1],
                    BearerToken = bearerToken
                };
                return true;
            }
        }
    }
}
