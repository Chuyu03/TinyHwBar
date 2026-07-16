using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace TinyHwBar
{
    internal enum UpdatePackageStageStatus
    {
        Succeeded,
        NotConfirmed,
        InvalidManifest,
        EndpointRejected,
        StagingDirectoryRejected,
        TransportFailed,
        UnexpectedHttpStatus,
        EmptyPackage,
        PackageTooLarge,
        HashMismatch,
        StagingFailed
    }

    internal sealed class UpdatePackageTransportResponse
    {
        internal UpdatePackageTransportResponse(int statusCode, byte[] content)
        {
            StatusCode = statusCode;
            Content = content ?? new byte[0];
        }

        internal int StatusCode { get; private set; }
        internal byte[] Content { get; private set; }
    }

    internal interface IUpdatePackageTransport
    {
        UpdatePackageTransportResponse Get(Uri endpoint, int maximumBytes);
    }

    internal sealed class UpdatePackageSizeLimitExceededException : IOException
    {
        internal UpdatePackageSizeLimitExceededException()
            : base("The update package exceeds the configured size limit.")
        {
        }
    }

    internal sealed class HttpWebRequestUpdatePackageTransport : IUpdatePackageTransport
    {
        private readonly IEndpointAddressResolver addressResolver;

        internal HttpWebRequestUpdatePackageTransport()
            : this(new SystemEndpointAddressResolver())
        {
        }

        internal HttpWebRequestUpdatePackageTransport(
            IEndpointAddressResolver addressResolver)
        {
            if (addressResolver == null)
            {
                throw new ArgumentNullException("addressResolver");
            }

            this.addressResolver = addressResolver;
        }

        public UpdatePackageTransportResponse Get(Uri endpoint, int maximumBytes)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException("endpoint");
            }

            if (maximumBytes <= 0)
            {
                throw new ArgumentOutOfRangeException("maximumBytes");
            }

            PublicHttpsEndpointPolicy.PreflightEndpointAddresses(endpoint, addressResolver);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.MaximumResponseHeadersLength = 16;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.PreAuthenticate = false;
            request.UserAgent = "TinyHwBar/2 update-package";
            request.Headers[HttpRequestHeader.AcceptEncoding] = "identity";

            HttpWebResponse response = null;
            try
            {
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (WebException exception)
                {
                    WebResponse errorResponse = exception.Response;
                    response = errorResponse as HttpWebResponse;
                    if (response == null)
                    {
                        if (errorResponse != null)
                        {
                            errorResponse.Dispose();
                        }

                        throw;
                    }
                }

                int statusCode = (int)response.StatusCode;
                if (statusCode != (int)HttpStatusCode.OK)
                {
                    return new UpdatePackageTransportResponse(statusCode, new byte[0]);
                }

                if (response.ContentLength > maximumBytes)
                {
                    throw new UpdatePackageSizeLimitExceededException();
                }

                using (Stream responseStream = response.GetResponseStream())
                {
                    return new UpdatePackageTransportResponse(
                        statusCode,
                        ReadBounded(responseStream, maximumBytes));
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

        private static byte[] ReadBounded(Stream stream, int maximumBytes)
        {
            if (stream == null)
            {
                return new byte[0];
            }

            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                while (true)
                {
                    int read = stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (buffer.Length + read > maximumBytes)
                    {
                        throw new UpdatePackageSizeLimitExceededException();
                    }

                    buffer.Write(chunk, 0, read);
                }

                return buffer.ToArray();
            }
        }
    }

    internal sealed class UpdatePackageStageResult
    {
        internal UpdatePackageStageStatus Status { get; private set; }
        internal string StagedPath { get; private set; }

        private UpdatePackageStageResult()
        {
        }

        internal static UpdatePackageStageResult Create(UpdatePackageStageStatus status)
        {
            return new UpdatePackageStageResult
            {
                Status = status,
                StagedPath = string.Empty
            };
        }

        internal static UpdatePackageStageResult Success(string stagedPath)
        {
            return new UpdatePackageStageResult
            {
                Status = UpdatePackageStageStatus.Succeeded,
                StagedPath = stagedPath ?? string.Empty
            };
        }
    }

    internal sealed class UpdatePackageService
    {
        internal const int MaximumPackageBytes = 32 * 1024 * 1024;
        internal const string StagedFileName = "TinyHwBar-update.exe";

        private readonly IUpdatePackageTransport transport;

        internal UpdatePackageService()
            : this(new HttpWebRequestUpdatePackageTransport())
        {
        }

        internal UpdatePackageService(IUpdatePackageTransport transport)
        {
            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }

            this.transport = transport;
        }

        internal UpdatePackageStageResult StageAfterConfirmation(
            UpdateManifest manifest,
            string stagingDirectory,
            bool userConfirmed)
        {
            if (!userConfirmed)
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.NotConfirmed);
            }

            if (!IsStructurallyValidManifest(manifest))
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.InvalidManifest);
            }

            Uri validatedEndpoint;
            string endpointError;
            if (!PublicHttpsEndpointPolicy.TryCreate(
                manifest.DownloadUri.AbsoluteUri,
                out validatedEndpoint,
                out endpointError))
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.EndpointRejected);
            }

            string fullStagingDirectory;
            string stagedPath;
            if (!TryResolveStagingPaths(
                stagingDirectory,
                out fullStagingDirectory,
                out stagedPath))
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.StagingDirectoryRejected);
            }

            UpdatePackageTransportResponse response;
            try
            {
                response = transport.Get(validatedEndpoint, MaximumPackageBytes);
            }
            catch (UpdatePackageSizeLimitExceededException)
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.PackageTooLarge);
            }
            catch (Exception)
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.TransportFailed);
            }

            if (response == null)
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.TransportFailed);
            }

            if (response.StatusCode != (int)HttpStatusCode.OK)
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.UnexpectedHttpStatus);
            }

            byte[] content = response.Content;
            if (content == null || content.Length == 0)
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.EmptyPackage);
            }

            if (content.Length > MaximumPackageBytes)
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.PackageTooLarge);
            }

            string actualSha256 = ComputeSha256(content);
            if (!string.Equals(
                actualSha256,
                manifest.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.HashMismatch);
            }

            if (!TryStageAtomically(
                fullStagingDirectory,
                stagedPath,
                content))
            {
                return UpdatePackageStageResult.Create(
                    UpdatePackageStageStatus.StagingFailed);
            }

            return UpdatePackageStageResult.Success(stagedPath);
        }

        private static bool IsStructurallyValidManifest(UpdateManifest manifest)
        {
            return manifest != null &&
                manifest.Version != null &&
                manifest.DownloadUri != null &&
                IsSha256(manifest.Sha256);
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
                bool isHexadecimal =
                    (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F');

                if (!isHexadecimal)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryResolveStagingPaths(
            string stagingDirectory,
            out string fullStagingDirectory,
            out string stagedPath)
        {
            fullStagingDirectory = null;
            stagedPath = null;

            if (string.IsNullOrWhiteSpace(stagingDirectory))
            {
                return false;
            }

            try
            {
                string trimmedDirectory = stagingDirectory.Trim();
                if (!Path.IsPathRooted(trimmedDirectory) ||
                    trimmedDirectory.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    return false;
                }

                string fullDirectory = Path.GetFullPath(trimmedDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string root = Path.GetPathRoot(fullDirectory);
                if (string.IsNullOrWhiteSpace(fullDirectory) ||
                    string.Equals(
                        fullDirectory,
                        root == null
                            ? string.Empty
                            : root.TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string candidatePath = Path.Combine(fullDirectory, StagedFileName);
                string candidateDirectory = Path.GetDirectoryName(candidatePath);
                if (!string.Equals(
                    candidateDirectory,
                    fullDirectory,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                fullStagingDirectory = fullDirectory;
                stagedPath = candidatePath;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ComputeSha256(byte[] content)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(content);
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }

        private static bool TryStageAtomically(
            string stagingDirectory,
            string stagedPath,
            byte[] content)
        {
            bool directoryExisted = Directory.Exists(stagingDirectory);
            bool targetExisted = false;
            bool completed = false;
            string temporaryPath = Path.Combine(
                stagingDirectory,
                ".TinyHwBar-update-" + Guid.NewGuid().ToString("N") + ".tmp");
            string backupPath = Path.Combine(
                stagingDirectory,
                ".TinyHwBar-update-" + Guid.NewGuid().ToString("N") + ".bak");

            try
            {
                Directory.CreateDirectory(stagingDirectory);

                using (FileStream output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    output.Write(content, 0, content.Length);
                    output.Flush(true);
                }

                targetExisted = File.Exists(stagedPath);
                if (targetExisted)
                {
                    File.Replace(temporaryPath, stagedPath, backupPath, true);
                }
                else
                {
                    File.Move(temporaryPath, stagedPath);
                }

                completed = true;
                TryDelete(backupPath);
                return true;
            }
            catch (Exception)
            {
                if (targetExisted)
                {
                    TryRollbackReplacement(stagedPath, backupPath);
                }

                return false;
            }
            finally
            {
                TryDelete(temporaryPath);

                if (!completed && !directoryExisted)
                {
                    TryDeleteEmptyDirectory(stagingDirectory);
                }
            }
        }

        private static void TryRollbackReplacement(
            string stagedPath,
            string backupPath)
        {
            try
            {
                if (!File.Exists(backupPath))
                {
                    return;
                }

                if (File.Exists(stagedPath))
                {
                    File.Replace(backupPath, stagedPath, null, true);
                }
                else
                {
                    File.Move(backupPath, stagedPath);
                }
            }
            catch (Exception)
            {
                // Preserve the backup file when rollback cannot be completed.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup; the staged executable remains deterministic.
            }
        }

        private static void TryDeleteEmptyDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path) &&
                    Directory.GetFileSystemEntries(path).Length == 0)
                {
                    Directory.Delete(path, false);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup only.
            }
        }
    }
}
