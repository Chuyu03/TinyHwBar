using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TinyHwBar.Tests
{
    internal static class V2ServiceTests
    {
        internal static void RunAll()
        {
            List<string> failures = new List<string>();
            Run("startup registration boundaries", TestStartupRegistrationBoundaries, failures);
            Run("startup UNC write boundary", TestStartupUncWriteBoundary, failures);
            Run("startup mapped-drive write boundary", TestStartupMappedDriveWriteBoundary, failures);
            Run("update service boundaries", TestUpdateServiceBoundaries, failures);
            Run("update package staging boundaries", TestUpdatePackageStagingBoundaries, failures);
            Run("public HTTPS address policy", TestPublicHttpsAddressPolicy, failures);
            Run("production transport address preflight", TestProductionTransportAddressPreflight, failures);
            Run("V3 transport user agents", TestV3TransportUserAgents, failures);
            Run("loopback API disabled boundary", TestLoopbackApiDisabledBoundary, failures);
            Run("loopback API enabled contract", TestLoopbackApiEnabledContract, failures);
            Run("telemetry confirmation boundaries", TestTelemetryConfirmationBoundaries, failures);
            Run("telemetry preview invalidation", TestTelemetryPreviewInvalidation, failures);
            Run("diagnostic preview schema", TestDiagnosticPreviewSchema, failures);

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    failures.Count.ToString() +
                    " advanced service test(s) failed: " +
                    string.Join(" | ", failures.ToArray()));
            }
        }

        private static void TestStartupRegistrationBoundaries()
        {
            FakeStartupRegistrationStore store = new FakeStartupRegistrationStore();
            StartupManager manager = new StartupManager(
                System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,
                "TinyHwBarServiceTest",
                store);

            Assert(
                manager.GetStatus() == StartupRegistrationState.Disabled,
                "an empty fake store was not reported as disabled");

            manager.EnableForCurrentUser();
            Assert(store.WriteCount == 1, "enable did not use the fake store");
            Assert(
                manager.GetStatus() == StartupRegistrationState.EnabledForCurrentExecutable,
                "the expected startup command was not reported as enabled");

            AssertNonMatchingStartupValue(
                manager,
                store,
                StartupStoredValue.NonString(),
                "a non-string startup value");
            AssertNonMatchingStartupValue(
                manager,
                store,
                StartupStoredValue.FromString(string.Empty),
                "an empty startup string");
            AssertNonMatchingStartupValue(
                manager,
                store,
                StartupStoredValue.FromString("   "),
                "a whitespace-only startup string");

            string caseVariant = manager.ExpectedCommandLine.ToUpperInvariant();
            if (string.Equals(caseVariant, manager.ExpectedCommandLine, StringComparison.Ordinal))
            {
                caseVariant = manager.ExpectedCommandLine.ToLowerInvariant();
            }

            Assert(
                !string.Equals(caseVariant, manager.ExpectedCommandLine, StringComparison.Ordinal),
                "the case-variant test command unexpectedly remained exact");
            AssertNonMatchingStartupValue(
                manager,
                store,
                StartupStoredValue.FromString(caseVariant),
                "a case-inexact startup command");
            AssertNonMatchingStartupValue(
                manager,
                store,
                StartupStoredValue.FromString(manager.ExpectedCommandLine + " "),
                "a space-inexact startup command");

            store.CommandLine = "\"C:\\Other\\TinyHwBar.exe\"";
            Assert(
                manager.GetStatus() == StartupRegistrationState.DifferentCommand,
                "a different startup command was not detected");
            AssertThrows<InvalidOperationException>(
                delegate { manager.EnableForCurrentUser(); },
                "enable overwrote a different startup command");
            Assert(store.ReplaceCount == 0, "enable unexpectedly called replacement");
            Assert(
                store.CommandLine == "\"C:\\Other\\TinyHwBar.exe\"",
                "the different startup command changed without confirmation");

            AssertThrows<InvalidOperationException>(
                delegate { manager.ReplaceForCurrentUserAfterConfirmation(false); },
                "replacement proceeded without explicit confirmation");
            manager.ReplaceForCurrentUserAfterConfirmation(true);
            Assert(store.ReplaceCount == 1, "explicit replacement did not use the fake store");
            Assert(
                manager.GetStatus() == StartupRegistrationState.EnabledForCurrentExecutable,
                "explicit replacement did not install the expected command");

            store.CommandLine = "\"C:\\Other\\Preserve.exe\"";
            Assert(!manager.DisableForCurrentUser(), "disable removed a different command");
            Assert(
                store.CommandLine == "\"C:\\Other\\Preserve.exe\"",
                "disable changed a different command");

            store.CommandLine = manager.ExpectedCommandLine;
            Assert(manager.DisableForCurrentUser(), "disable rejected the matching command");
            Assert(store.CommandLine == null, "disable left the matching command registered");
        }

        private static void TestStartupUncWriteBoundary()
        {
            FakeStartupRegistrationStore store = new FakeStartupRegistrationStore();
            StartupManager manager = new StartupManager(
                @"\\server.invalid\share\TinyHwBar.exe",
                "TinyHwBarUncServiceTest",
                store);

            Assert(
                manager.GetStatus() == StartupRegistrationState.Disabled,
                "a missing UNC startup registration could not be read as disabled");

            store.CommandLine = manager.ExpectedCommandLine;
            Assert(
                manager.GetStatus() == StartupRegistrationState.EnabledForCurrentExecutable,
                "an exact UNC startup registration could not be reported");
            Assert(
                manager.DisableForCurrentUser(),
                "disable could not remove an exact UNC startup registration");

            AssertThrows<InvalidOperationException>(
                delegate { manager.EnableForCurrentUser(); },
                "enable accepted a non-local UNC executable path");
            AssertThrows<InvalidOperationException>(
                delegate { manager.ReplaceForCurrentUserAfterConfirmation(true); },
                "replacement accepted a non-local UNC executable path");
            Assert(store.WriteCount == 0, "UNC enable reached the registration store");
            Assert(store.ReplaceCount == 0, "UNC replacement reached the registration store");
        }

        private static void TestStartupMappedDriveWriteBoundary()
        {
            AssertStartupDriveRejected(
                delegate { return DriveType.Network; },
                "mapped network drive");
            AssertStartupDriveRejected(
                delegate { return DriveType.Unknown; },
                "unknown drive type");
            AssertStartupDriveRejected(
                delegate { return DriveType.NoRootDirectory; },
                "missing drive root");
            AssertStartupDriveRejected(
                delegate { throw new IOException("Synthetic drive inspection failure."); },
                "unverifiable drive type");
        }

        private static void AssertStartupDriveRejected(
            Func<string, DriveType> driveTypeResolver,
            string description)
        {
            FakeStartupRegistrationStore store = new FakeStartupRegistrationStore();
            StartupManager manager = new StartupManager(
                @"Z:\TinyHwBar.exe",
                "TinyHwBarMappedDriveServiceTest",
                store,
                driveTypeResolver);

            AssertThrows<InvalidOperationException>(
                delegate { manager.EnableForCurrentUser(); },
                "enable accepted an executable on a " + description);
            AssertThrows<InvalidOperationException>(
                delegate { manager.ReplaceForCurrentUserAfterConfirmation(true); },
                "replacement accepted an executable on a " + description);
            Assert(store.WriteCount == 0, description + " enable reached the registration store");
            Assert(
                store.ReplaceCount == 0,
                description + " replacement reached the registration store");
        }

        private static void AssertNonMatchingStartupValue(
            StartupManager manager,
            FakeStartupRegistrationStore store,
            StartupStoredValue storedValue,
            string description)
        {
            store.StoredValue = storedValue;
            int writeCount = store.WriteCount;
            int replaceCount = store.ReplaceCount;

            Assert(
                manager.GetStatus() == StartupRegistrationState.DifferentCommand,
                description + " was not reported as a different command");
            AssertThrows<InvalidOperationException>(
                delegate { manager.EnableForCurrentUser(); },
                description + " was overwritten by enable");
            Assert(store.WriteCount == writeCount + 1, description + " bypassed the fake store");
            Assert(store.ReplaceCount == replaceCount, description + " triggered replacement");
            Assert(
                object.ReferenceEquals(store.StoredValue, storedValue),
                description + " changed without confirmation");
            Assert(!manager.DisableForCurrentUser(), description + " was removed by disable");
            Assert(
                object.ReferenceEquals(store.StoredValue, storedValue),
                description + " changed during disable");
        }

        private static void TestUpdateServiceBoundaries()
        {
            FakeUpdateTransport disabledTransport = new FakeUpdateTransport(
                new UpdateTransportResponse(200, ValidManifestText()));
            UpdateService disabledService = new UpdateService(
                new Version(2, 0, 0),
                UpdateServiceOptions.CreateDefault(),
                disabledTransport);

            UpdateCheckResult disabledResult = disabledService.CheckNow(
                UpdateCheckTrigger.ExplicitAutomatic);
            Assert(
                disabledResult.Status == UpdateCheckStatus.AutomaticChecksDisabled,
                "default automatic update checking was not disabled");
            Assert(disabledTransport.CallCount == 0, "disabled automatic check used transport");

            FakeUpdateTransport manualTransport = new FakeUpdateTransport(
                new UpdateTransportResponse(200, ValidManifestText()));
            UpdateServiceOptions manualOptions = UpdateServiceOptions.CreateDefault();
            manualOptions.ManifestEndpoint = "https://updates.example.com/tinyhwbar/manifest.txt";
            UpdateService manualService = new UpdateService(
                new Version(2, 0, 0),
                manualOptions,
                manualTransport);

            UpdateCheckResult manualResult = manualService.CheckNow(UpdateCheckTrigger.Manual);
            Assert(manualResult.Status == UpdateCheckStatus.Succeeded, "manual fake check failed");
            Assert(manualResult.UpdateAvailable, "newer fake manifest was not reported as available");
            Assert(manualResult.Manifest != null, "valid fake manifest was not parsed");
            Assert(
                manualResult.Manifest.Version.Equals(new Version(2, 1, 0)),
                "fake manifest version changed during parsing");
            Assert(
                manualResult.Manifest.DownloadUri.AbsoluteUri ==
                    "https://downloads.example.com/tinyhwbar/TinyHwBar.exe",
                "fake manifest download URI changed during parsing");
            Assert(manualTransport.CallCount == 1, "manual check did not use fake transport once");

            FakeUpdateTransport enabledAutomaticTransport = new FakeUpdateTransport(
                new UpdateTransportResponse(200, ValidManifestText()));
            UpdateServiceOptions enabledAutomaticOptions = UpdateServiceOptions.CreateDefault();
            enabledAutomaticOptions.AutomaticChecksEnabled = true;
            enabledAutomaticOptions.ManifestEndpoint =
                "https://updates.example.com/tinyhwbar/manifest.txt";
            UpdateService enabledAutomaticService = new UpdateService(
                new Version(2, 0, 0),
                enabledAutomaticOptions,
                enabledAutomaticTransport);

            UpdateCheckResult enabledAutomaticResult = enabledAutomaticService.CheckNow(
                UpdateCheckTrigger.ExplicitAutomatic);
            Assert(
                enabledAutomaticResult.Status == UpdateCheckStatus.Succeeded,
                "explicitly enabled automatic fake check failed");
            Assert(
                enabledAutomaticTransport.CallCount == 1,
                "explicitly enabled automatic check did not use fake transport once");

            FakeUpdateTransport unsafeTransport = new FakeUpdateTransport(
                new UpdateTransportResponse(200, ValidManifestText()));
            UpdateServiceOptions unsafeOptions = UpdateServiceOptions.CreateDefault();
            unsafeOptions.ManifestEndpoint = "https://127.0.0.1/manifest.txt";
            UpdateService unsafeService = new UpdateService(
                new Version(2, 0, 0),
                unsafeOptions,
                unsafeTransport);

            UpdateCheckResult unsafeResult = unsafeService.CheckNow(UpdateCheckTrigger.Manual);
            Assert(
                unsafeResult.Status == UpdateCheckStatus.EndpointRejected,
                "loopback update endpoint was not rejected");
            Assert(unsafeTransport.CallCount == 0, "rejected endpoint used transport");
        }

        private static void TestPublicHttpsAddressPolicy()
        {
            Uri acceptedEndpoint;
            string endpointError;
            Assert(
                PublicHttpsEndpointPolicy.TryCreate(
                    "https://updates.example.com/tinyhwbar/manifest.txt",
                    out acceptedEndpoint,
                    out endpointError),
                "a valid public HTTPS endpoint was rejected");
            Assert(
                !PublicHttpsEndpointPolicy.TryCreate(
                    "https://updates.example.com/tinyhwbar/manifest.txt?channel=preview",
                    out acceptedEndpoint,
                    out endpointError),
                "an endpoint query string passed the policy");
            Assert(
                !PublicHttpsEndpointPolicy.TryCreate(
                    "https://name:value@updates.example.com/tinyhwbar/manifest.txt",
                    out acceptedEndpoint,
                    out endpointError),
                "endpoint user information passed the policy");
            Assert(
                !PublicHttpsEndpointPolicy.TryCreate(
                    "https://updates.example.com/tinyhwbar/manifest.txt#preview",
                    out acceptedEndpoint,
                    out endpointError),
                "an endpoint fragment passed the policy");

            Assert(
                PublicHttpsEndpointPolicy.AreAllAddressesPublic(
                    new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") }),
                "public IPv4 addresses were rejected");
            Assert(
                !PublicHttpsEndpointPolicy.AreAllAddressesPublic(
                    new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("10.0.0.1") }),
                "a private IPv4 address passed the public-address policy");
            Assert(
                !PublicHttpsEndpointPolicy.AreAllAddressesPublic(
                    new[] { IPAddress.IPv6Loopback }),
                "IPv6 loopback passed the public-address policy");
            Assert(
                PublicHttpsEndpointPolicy.AreAllAddressesPublic(
                    new[]
                    {
                        IPAddress.Parse("2606:4700:4700::1111"),
                        IPAddress.Parse("2001:3::1"),
                        IPAddress.Parse("2001:20::1")
                    }),
                "globally reachable IPv6 addresses were rejected");
            Assert(
                !PublicHttpsEndpointPolicy.AreAllAddressesPublic(
                    new[] { IPAddress.Parse("2001:2::1") }),
                "IPv6 benchmarking space passed the public-address policy");
            Assert(
                !PublicHttpsEndpointPolicy.AreAllAddressesPublic(
                    new[] { IPAddress.Parse("2001:db8::1") }),
                "IPv6 documentation space passed the public-address policy");
            Assert(
                !PublicHttpsEndpointPolicy.AreAllAddressesPublic(
                    new[] { IPAddress.Parse("2002::1") }),
                "IPv6 6to4 space passed the conservative public-address policy");
            Assert(
                !PublicHttpsEndpointPolicy.AreAllAddressesPublic(
                    new[] { IPAddress.Parse("3fff::1") }),
                "new IPv6 documentation space passed the public-address policy");
            Assert(
                !PublicHttpsEndpointPolicy.AreAllAddressesPublic(
                    new[] { IPAddress.Parse("5f00::1") }),
                "non-global IPv6 SRv6 space passed the public-address policy");
            Assert(
                !PublicHttpsEndpointPolicy.AreAllAddressesPublic(new IPAddress[0]),
                "an empty resolution result passed the public-address policy");
        }

        private static void TestProductionTransportAddressPreflight()
        {
            Uri endpoint = new Uri("https://updates.example.com/tinyhwbar/test");
            FakeEndpointAddressResolver resolver = new FakeEndpointAddressResolver(
                new[] { IPAddress.Parse("127.0.0.1") });

            HttpWebRequestUpdateTransport updateTransport =
                new HttpWebRequestUpdateTransport(resolver);
            AssertThrows<InvalidOperationException>(
                delegate { updateTransport.Get(endpoint); },
                "the production update transport passed a private DNS result");

            HttpWebRequestUpdatePackageTransport packageTransport =
                new HttpWebRequestUpdatePackageTransport(resolver);
            AssertThrows<InvalidOperationException>(
                delegate { packageTransport.Get(endpoint, 1024); },
                "the production package transport passed a private DNS result");

            HttpWebRequestTelemetryTransport telemetryTransport =
                new HttpWebRequestTelemetryTransport(resolver);
            AssertThrows<InvalidOperationException>(
                delegate { telemetryTransport.PostJson(endpoint, "{}"); },
                "the production telemetry transport passed a private DNS result");

            Assert(
                resolver.CallCount == 3,
                "a production transport skipped the injected DNS preflight");
        }

        private static void TestV3TransportUserAgents()
        {
            Assert(
                HttpWebRequestUpdateTransport.UserAgentValue ==
                    "TinyHwBar/3 update-check",
                "the update-check transport did not identify V3");
            Assert(
                HttpWebRequestUpdatePackageTransport.UserAgentValue ==
                    "TinyHwBar/3 update-package",
                "the update-package transport did not identify V3");
            Assert(
                HttpWebRequestTelemetryTransport.UserAgentValue ==
                    "TinyHwBar/3 telemetry",
                "the telemetry transport did not identify V3");
        }

        private static void TestUpdatePackageStagingBoundaries()
        {
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "TinyHwBar-package-tests-" + Guid.NewGuid().ToString("N"));
            string stagingDirectory = Path.Combine(temporaryRoot, "stage");
            byte[] firstPackage = Encoding.ASCII.GetBytes("first verified package");
            byte[] secondPackage = Encoding.ASCII.GetBytes("second verified package");

            try
            {
                UpdateManifest firstManifest = CreateManifestForPackage(firstPackage, "2.1.0");
                FakeUpdatePackageTransport unconfirmedTransport =
                    new FakeUpdatePackageTransport(
                        new UpdatePackageTransportResponse(200, firstPackage));
                UpdatePackageService unconfirmedService =
                    new UpdatePackageService(unconfirmedTransport);
                UpdatePackageStageResult unconfirmed =
                    unconfirmedService.StageAfterConfirmation(
                        firstManifest,
                        stagingDirectory,
                        false);
                Assert(
                    unconfirmed.Status == UpdatePackageStageStatus.NotConfirmed,
                    "unconfirmed package staging did not stop at the confirmation gate");
                Assert(unconfirmedTransport.CallCount == 0, "unconfirmed staging used transport");
                Assert(!Directory.Exists(stagingDirectory), "unconfirmed staging created a directory");

                FakeUpdatePackageTransport firstTransport = new FakeUpdatePackageTransport(
                    new UpdatePackageTransportResponse(200, firstPackage));
                UpdatePackageStageResult firstResult = new UpdatePackageService(firstTransport)
                    .StageAfterConfirmation(firstManifest, stagingDirectory, true);
                Assert(firstResult.Status == UpdatePackageStageStatus.Succeeded, "fake package did not stage");
                Assert(firstTransport.CallCount == 1, "confirmed staging did not use transport once");
                AssertBytes(
                    File.ReadAllBytes(firstResult.StagedPath),
                    firstPackage,
                    "staged package bytes changed");

                UpdateManifest secondManifest = CreateManifestForPackage(secondPackage, "2.2.0");
                FakeUpdatePackageTransport secondTransport = new FakeUpdatePackageTransport(
                    new UpdatePackageTransportResponse(200, secondPackage));
                UpdatePackageStageResult secondResult = new UpdatePackageService(secondTransport)
                    .StageAfterConfirmation(secondManifest, stagingDirectory, true);
                Assert(secondResult.Status == UpdatePackageStageStatus.Succeeded, "atomic replacement failed");
                AssertBytes(
                    File.ReadAllBytes(secondResult.StagedPath),
                    secondPackage,
                    "atomic replacement retained the wrong package");

                FakeUpdatePackageTransport mismatchTransport = new FakeUpdatePackageTransport(
                    new UpdatePackageTransportResponse(200, firstPackage));
                UpdatePackageStageResult mismatch = new UpdatePackageService(mismatchTransport)
                    .StageAfterConfirmation(secondManifest, stagingDirectory, true);
                Assert(
                    mismatch.Status == UpdatePackageStageStatus.HashMismatch,
                    "hash mismatch was not rejected");
                AssertBytes(
                    File.ReadAllBytes(secondResult.StagedPath),
                    secondPackage,
                    "hash mismatch replaced the existing staged package");

                FakeUpdatePackageTransport oversizedTransport = new FakeUpdatePackageTransport(null);
                oversizedTransport.ExceptionToThrow =
                    new UpdatePackageSizeLimitExceededException();
                UpdatePackageStageResult oversized = new UpdatePackageService(oversizedTransport)
                    .StageAfterConfirmation(secondManifest, stagingDirectory, true);
                Assert(
                    oversized.Status == UpdatePackageStageStatus.PackageTooLarge,
                    "transport size limit was not reported");
                AssertBytes(
                    File.ReadAllBytes(secondResult.StagedPath),
                    secondPackage,
                    "oversized package damaged the existing staged package");

                FakeUpdatePackageTransport rejectedPathTransport =
                    new FakeUpdatePackageTransport(
                        new UpdatePackageTransportResponse(200, firstPackage));
                UpdatePackageStageResult rejectedPath =
                    new UpdatePackageService(rejectedPathTransport)
                        .StageAfterConfirmation(firstManifest, "relative-stage", true);
                Assert(
                    rejectedPath.Status == UpdatePackageStageStatus.StagingDirectoryRejected,
                    "relative staging directory was accepted");
                Assert(rejectedPathTransport.CallCount == 0, "rejected path used transport");
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }
        }

        private static void TestLoopbackApiDisabledBoundary()
        {
            LoopbackApiOptions options = LoopbackApiOptions.CreateDefault();
            Assert(!options.Enabled, "loopback API default was enabled");

            using (LoopbackApiServer server = new LoopbackApiServer(
                options,
                delegate { return "{}"; }))
            {
                AssertThrows<InvalidOperationException>(
                    delegate { server.Start(); },
                    "disabled loopback API started a listener");
                Assert(!server.IsRunning, "disabled loopback API reported a running listener");
            }

            Assert(
                LoopbackApiServer.IsAllowedBindAddress(IPAddress.Loopback),
                "IPv4 loopback was rejected");
            Assert(
                LoopbackApiServer.IsAllowedBindAddress(IPAddress.IPv6Loopback),
                "IPv6 loopback was rejected");
            Assert(
                !LoopbackApiServer.IsAllowedBindAddress(IPAddress.Any),
                "IPv4 wildcard bind was accepted");
            Assert(
                !LoopbackApiServer.IsAllowedBindAddress(IPAddress.Parse("192.168.1.2")),
                "private non-loopback bind was accepted");
            Assert(LoopbackApiServer.FixedTimeEquals("token", "token"), "equal tokens differed");
            Assert(!LoopbackApiServer.FixedTimeEquals("token", "Token"), "token comparison ignored case");
            Assert(!LoopbackApiServer.FixedTimeEquals("token", "token2"), "different token lengths matched");
        }

        private static void TestLoopbackApiEnabledContract()
        {
            const string expectedJson =
                "{\"application\":\"TinyHwBar\",\"version\":\"2.0.0-test\",\"status\":\"ok\"}";
            int statusProviderCallCount = 0;
            LoopbackApiOptions options = new LoopbackApiOptions
            {
                Enabled = true,
                BindAddress = IPAddress.Loopback
            };

            using (LoopbackApiServer server = new LoopbackApiServer(
                options,
                delegate
                {
                    statusProviderCallCount++;
                    return expectedJson;
                }))
            {
                LoopbackApiSession session = server.Start();
                Assert(server.IsRunning, "enabled loopback API did not report running");
                Assert(
                    session.Address.Equals(IPAddress.Loopback),
                    "enabled IPv4 session did not bind to 127.0.0.1");
                Assert(
                    session.Port >= LoopbackApiServer.MinimumPort &&
                    session.Port <= LoopbackApiServer.MaximumPort,
                    "enabled loopback API did not use a random high port");
                Assert(
                    !string.IsNullOrEmpty(session.BearerToken),
                    "enabled loopback API did not return a bearer token");
                AssertOnlyLoopbackListener(session.Address, session.Port);

                LoopbackHttpResponse missingToken = SendLoopbackGet(
                    session.Address,
                    session.Port,
                    "/v1/status",
                    null);
                Assert(
                    missingToken.StatusCode == 401,
                    "missing bearer token did not receive HTTP 401");

                LoopbackHttpResponse wrongToken = SendLoopbackGet(
                    session.Address,
                    session.Port,
                    "/v1/status",
                    "wrong-local-test-token");
                Assert(
                    wrongToken.StatusCode == 401,
                    "wrong bearer token did not receive HTTP 401");

                LoopbackHttpResponse wrongPath = SendLoopbackGet(
                    session.Address,
                    session.Port,
                    "/v1/not-status",
                    session.BearerToken);
                Assert(
                    wrongPath.StatusCode == 404,
                    "wrong loopback API path did not receive HTTP 404");
                Assert(
                    statusProviderCallCount == 0,
                    "unauthorized or wrong-path requests reached the status provider");

                LoopbackHttpResponse authorized = SendLoopbackGet(
                    session.Address,
                    session.Port,
                    "/v1/status",
                    session.BearerToken);
                Assert(
                    authorized.StatusCode == 200,
                    "authorized loopback API request did not receive HTTP 200");
                Assert(
                    string.Equals(authorized.Body, expectedJson, StringComparison.Ordinal),
                    "authorized loopback API response JSON was not deterministic");
                Assert(
                    statusProviderCallCount == 1,
                    "authorized status provider call count was not exactly one");

                server.Stop();
                Assert(!server.IsRunning, "stopped loopback API still reported running");
                Assert(
                    !CanConnectToLoopback(session.Address, session.Port),
                    "stopped loopback API still accepted connections");
            }

            LoopbackApiOptions wildcardOptions = new LoopbackApiOptions
            {
                Enabled = true,
                BindAddress = IPAddress.Any
            };
            using (LoopbackApiServer wildcardServer = new LoopbackApiServer(
                wildcardOptions,
                delegate { return expectedJson; }))
            {
                AssertThrows<InvalidOperationException>(
                    delegate { wildcardServer.Start(); },
                    "enabled loopback API accepted a wildcard bind address");
                Assert(
                    !wildcardServer.IsRunning,
                    "rejected wildcard loopback API reported running");
            }
        }

        private static void TestTelemetryConfirmationBoundaries()
        {
            FakeTelemetryTransport disabledTransport = new FakeTelemetryTransport();
            TelemetryOptions disabledOptions = TelemetryOptions.CreateDefault();
            disabledOptions.Endpoint = "https://telemetry.example.com/v1/events";
            TelemetryService disabledService = new TelemetryService(
                disabledOptions,
                disabledTransport);
            TelemetryPreview disabledPreview = disabledService.BuildPreview(
                new TelemetryDraft("manual_preview", "2.0.0", "metrics_available"));

            TelemetrySendResult unconfirmed = disabledService.ConfirmAndSend(
                disabledPreview,
                false);
            Assert(
                unconfirmed.Status == TelemetrySendStatus.UserConfirmationRequired,
                "unconfirmed telemetry did not stop at the confirmation gate");
            Assert(disabledTransport.CallCount == 0, "unconfirmed telemetry used transport");

            TelemetrySendResult disabled = disabledService.ConfirmAndSend(
                disabledPreview,
                true);
            Assert(
                disabled.Status == TelemetrySendStatus.TelemetryDisabled,
                "disabled telemetry did not stop at the enablement gate");
            Assert(disabledTransport.CallCount == 0, "disabled telemetry used transport");

            FakeTelemetryTransport enabledTransport = new FakeTelemetryTransport();
            TelemetryOptions enabledOptions = new TelemetryOptions
            {
                Enabled = true,
                Endpoint = "https://telemetry.example.com/v1/events"
            };
            TelemetryService enabledService = new TelemetryService(
                enabledOptions,
                enabledTransport);
            TelemetryPreview enabledPreview = enabledService.BuildPreview(
                new TelemetryDraft("manual_preview", "2.0.0", "metrics_available"));

            TelemetrySendResult sent = enabledService.ConfirmAndSend(enabledPreview, true);
            Assert(sent.Status == TelemetrySendStatus.Succeeded, "confirmed fake telemetry failed");
            Assert(enabledTransport.CallCount == 1, "confirmed telemetry did not send exactly once");
            Assert(
                enabledTransport.LastEndpoint.AbsoluteUri ==
                    "https://telemetry.example.com/v1/events",
                "confirmed telemetry used the wrong endpoint");
            Assert(
                enabledTransport.LastPayload == enabledPreview.PayloadJson,
                "confirmed telemetry changed the preview payload");

            TelemetrySendResult repeated = enabledService.ConfirmAndSend(enabledPreview, true);
            Assert(repeated.Status == TelemetrySendStatus.PreviewInvalid, "sent preview was reusable");
            Assert(enabledTransport.CallCount == 1, "reused preview caused a second send");
        }

        private static void TestTelemetryPreviewInvalidation()
        {
            FakeTelemetryTransport transport = new FakeTelemetryTransport();
            TelemetryOptions options = new TelemetryOptions
            {
                Enabled = true,
                Endpoint = "https://telemetry.example.com/v1/events"
            };
            TelemetryService service = new TelemetryService(options, transport);
            TelemetryPreview preview = service.BuildPreview(
                new TelemetryDraft("manual_preview", "2.0.0", "metrics_available"));

            options.Endpoint = "https://telemetry.example.com/v2/events";
            TelemetrySendResult result = service.ConfirmAndSend(preview, true);
            Assert(
                result.Status == TelemetrySendStatus.PreviewInvalid,
                "endpoint change did not invalidate the preview");
            Assert(transport.CallCount == 0, "invalidated preview used transport");
        }

        private static void TestDiagnosticPreviewSchema()
        {
            TelemetryOptions options = new TelemetryOptions
            {
                Enabled = true,
                Endpoint = "https://telemetry.example.com/v1/diagnostics"
            };
            TelemetryService service = new TelemetryService(options, new FakeTelemetryTransport());
            DiagnosticLogDraft draft = new DiagnosticLogDraft(
                "2.0.0",
                new[]
                {
                    new DiagnosticLogEntry(
                        DiagnosticLogSeverity.Information,
                        "metrics_available",
                        1),
                    new DiagnosticLogEntry(
                        DiagnosticLogSeverity.Warning,
                        "intel_probe_failed",
                        2)
                });

            TelemetryPreview preview = service.BuildDiagnosticLogPreview(draft);
            string[] fieldNames = preview.GetFieldNames();
            string[] expectedFields =
            {
                "schemaVersion",
                "payloadType",
                "appVersion",
                "entries.severity",
                "entries.diagnosticCode",
                "entries.occurrenceCount"
            };

            Assert(fieldNames.Length == expectedFields.Length, "diagnostic field count changed");
            for (int index = 0; index < expectedFields.Length; index++)
            {
                Assert(fieldNames[index] == expectedFields[index], "diagnostic field allowlist changed");
            }

            Assert(
                preview.Utf8ByteCount <= TelemetryService.MaximumPayloadBytes,
                "diagnostic preview exceeded the payload bound");
            Assert(
                preview.PayloadJson.IndexOf("\"message\"", StringComparison.Ordinal) < 0 &&
                preview.PayloadJson.IndexOf("\"details\"", StringComparison.Ordinal) < 0 &&
                preview.PayloadJson.IndexOf("\"stackTrace\"", StringComparison.Ordinal) < 0,
                "diagnostic preview exposed a raw free-text field");

            DiagnosticLogEntry[] tooManyEntries = new DiagnosticLogEntry[
                TelemetryService.MaximumDiagnosticLogEntries + 1];
            for (int index = 0; index < tooManyEntries.Length; index++)
            {
                tooManyEntries[index] = new DiagnosticLogEntry(
                    DiagnosticLogSeverity.Information,
                    "bounded_code",
                    1);
            }

            AssertThrows<ArgumentException>(
                delegate
                {
                    service.BuildDiagnosticLogPreview(
                        new DiagnosticLogDraft("2.0.0", tooManyEntries));
                },
                "diagnostic preview accepted too many entries");
            AssertThrows<ArgumentException>(
                delegate
                {
                    service.BuildDiagnosticLogPreview(
                        new DiagnosticLogDraft(
                            "2.0.0",
                            new[]
                            {
                                new DiagnosticLogEntry(
                                    DiagnosticLogSeverity.Error,
                                    "raw exception text",
                                    1)
                            }));
                },
                "diagnostic preview accepted raw free text");
        }

        private static void AssertOnlyLoopbackListener(IPAddress address, int port)
        {
            Assert(
                LoopbackApiServer.IsAllowedBindAddress(address),
                "listener inspection was asked to use a non-loopback address");

            bool foundExpectedListener = false;
            IPEndPoint[] activeListeners =
                IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            for (int index = 0; index < activeListeners.Length; index++)
            {
                IPEndPoint listener = activeListeners[index];
                if (listener.Port != port ||
                    listener.AddressFamily != address.AddressFamily)
                {
                    continue;
                }

                Assert(
                    LoopbackApiServer.IsAllowedBindAddress(listener.Address),
                    "loopback API port was also exposed on a non-loopback address");
                if (listener.Address.Equals(address))
                {
                    foundExpectedListener = true;
                }
            }

            Assert(
                foundExpectedListener,
                "active TCP listeners did not contain the reported loopback endpoint");
        }

        private static LoopbackHttpResponse SendLoopbackGet(
            IPAddress address,
            int port,
            string path,
            string bearerToken)
        {
            Assert(
                LoopbackApiServer.IsAllowedBindAddress(address),
                "test request attempted to leave the loopback interface");
            Assert(
                !string.IsNullOrEmpty(path) && path[0] == '/',
                "loopback test path was invalid");
            Assert(
                bearerToken == null ||
                (bearerToken.IndexOf('\r') < 0 && bearerToken.IndexOf('\n') < 0),
                "loopback test token contained a header delimiter");

            string host = address.Equals(IPAddress.IPv6Loopback)
                ? "[::1]"
                : "127.0.0.1";
            string authorization = bearerToken == null
                ? string.Empty
                : "Authorization: Bearer " + bearerToken + "\r\n";
            string request =
                "GET " + path + " HTTP/1.1\r\n" +
                "Host: " + host + "\r\n" +
                authorization +
                "Connection: close\r\n\r\n";

            using (TcpClient client = ConnectToLoopback(address, port))
            {
                client.ReceiveTimeout = 1500;
                client.SendTimeout = 1500;
                client.NoDelay = true;

                using (NetworkStream stream = client.GetStream())
                {
                    byte[] requestBytes = Encoding.ASCII.GetBytes(request);
                    stream.Write(requestBytes, 0, requestBytes.Length);
                    stream.Flush();

                    Stopwatch deadline = Stopwatch.StartNew();
                    using (MemoryStream responseBytes = new MemoryStream())
                    {
                        byte[] buffer = new byte[4096];
                        while (true)
                        {
                            int remainingMilliseconds =
                                3000 - (int)deadline.ElapsedMilliseconds;
                            if (remainingMilliseconds <= 0)
                            {
                                throw new TimeoutException(
                                    "Loopback API response exceeded the test deadline.");
                            }

                            stream.ReadTimeout = Math.Min(1500, remainingMilliseconds);
                            int bytesRead = stream.Read(buffer, 0, buffer.Length);
                            if (bytesRead == 0)
                            {
                                break;
                            }

                            responseBytes.Write(buffer, 0, bytesRead);
                            if (responseBytes.Length > 48 * 1024)
                            {
                                throw new InvalidOperationException(
                                    "Loopback API response exceeded the test size limit.");
                            }
                        }

                        return ParseLoopbackHttpResponse(responseBytes.ToArray());
                    }
                }
            }
        }

        private static TcpClient ConnectToLoopback(IPAddress address, int port)
        {
            if (!LoopbackApiServer.IsAllowedBindAddress(address))
            {
                throw new InvalidOperationException(
                    "The test client can connect only to IPv4 or IPv6 loopback.");
            }

            TcpClient client = new TcpClient(address.AddressFamily);
            try
            {
                IAsyncResult pendingConnect = client.BeginConnect(address, port, null, null);
                System.Threading.WaitHandle completion = pendingConnect.AsyncWaitHandle;
                try
                {
                    if (!completion.WaitOne(1500))
                    {
                        throw new TimeoutException(
                            "Loopback connection exceeded the test deadline.");
                    }

                    client.EndConnect(pendingConnect);
                }
                finally
                {
                    completion.Close();
                }

                return client;
            }
            catch
            {
                client.Close();
                throw;
            }
        }

        private static bool CanConnectToLoopback(IPAddress address, int port)
        {
            try
            {
                using (TcpClient client = ConnectToLoopback(address, port))
                {
                    return true;
                }
            }
            catch (SocketException)
            {
                return false;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private static LoopbackHttpResponse ParseLoopbackHttpResponse(byte[] responseBytes)
        {
            string response = Encoding.UTF8.GetString(responseBytes ?? new byte[0]);
            int headerEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert(headerEnd >= 0, "loopback API response did not contain complete headers");

            int statusLineEnd = response.IndexOf("\r\n", StringComparison.Ordinal);
            Assert(
                statusLineEnd > 0 && statusLineEnd < headerEnd,
                "loopback API response did not contain a valid status line");
            string[] statusParts = response.Substring(0, statusLineEnd).Split(' ');
            int statusCode = 0;
            Assert(
                statusParts.Length >= 2 &&
                string.Equals(statusParts[0], "HTTP/1.1", StringComparison.Ordinal) &&
                int.TryParse(statusParts[1], out statusCode),
                "loopback API response status line was malformed");

            return new LoopbackHttpResponse(
                statusCode,
                response.Substring(headerEnd + 4));
        }

        private static string ValidManifestText()
        {
            return
                "Version=2.1.0\n" +
                "DownloadUrl=https://downloads.example.com/tinyhwbar/TinyHwBar.exe\n" +
                "Sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n" +
                "ReleaseNotes=Bounded local fake manifest";
        }

        private static UpdateManifest CreateManifestForPackage(
            byte[] content,
            string version)
        {
            string manifestText =
                "Version=" + version + "\n" +
                "DownloadUrl=https://downloads.example.com/tinyhwbar/TinyHwBar.exe\n" +
                "Sha256=" + ComputeSha256(content) + "\n" +
                "ReleaseNotes=Local fake package";
            UpdateManifest manifest;
            Assert(
                UpdateManifest.TryParse(manifestText, out manifest),
                "fake package manifest did not parse");
            return manifest;
        }

        private static string ComputeSha256(byte[] content)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(content))
                    .Replace("-", string.Empty);
            }
        }

        private static void AssertBytes(byte[] actual, byte[] expected, string message)
        {
            if (actual == null || expected == null || actual.Length != expected.Length)
            {
                throw new InvalidOperationException(message);
            }

            for (int index = 0; index < actual.Length; index++)
            {
                if (actual[index] != expected[index])
                {
                    throw new InvalidOperationException(message);
                }
            }
        }

        private static void Run(
            string name,
            Action test,
            IList<string> failures)
        {
            try
            {
                test();
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception ex)
            {
                failures.Add(name + " - " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private sealed class LoopbackHttpResponse
        {
            internal LoopbackHttpResponse(int statusCode, string body)
            {
                StatusCode = statusCode;
                Body = body;
            }

            internal int StatusCode { get; private set; }
            internal string Body { get; private set; }
        }

        private sealed class FakeStartupRegistrationStore : IStartupRegistrationStore
        {
            internal StartupStoredValue StoredValue = StartupStoredValue.Missing();
            internal int WriteCount;
            internal int ReplaceCount;

            internal string CommandLine
            {
                get
                {
                    return StoredValue != null &&
                        StoredValue.Kind == StartupStoredValueKind.String
                        ? StoredValue.CommandLine
                        : null;
                }
                set
                {
                    StoredValue = value == null
                        ? StartupStoredValue.Missing()
                        : StartupStoredValue.FromString(value);
                }
            }

            public StartupStoredValue Read(string valueName)
            {
                return StoredValue;
            }

            public bool WriteIfMissingOrMatching(string valueName, string commandLine)
            {
                WriteCount++;
                if (StoredValue == null ||
                    (StoredValue.Kind != StartupStoredValueKind.Missing &&
                     (StoredValue.Kind != StartupStoredValueKind.String ||
                      !string.Equals(
                          StoredValue.CommandLine,
                          commandLine,
                          StringComparison.Ordinal))))
                {
                    return false;
                }

                StoredValue = StartupStoredValue.FromString(commandLine);
                return true;
            }

            public void Replace(string valueName, string commandLine)
            {
                ReplaceCount++;
                StoredValue = StartupStoredValue.FromString(commandLine);
            }

            public bool DeleteIfMatches(string valueName, string expectedCommandLine)
            {
                if (StoredValue != null &&
                    StoredValue.Kind == StartupStoredValueKind.Missing)
                {
                    return true;
                }

                if (StoredValue == null ||
                    StoredValue.Kind != StartupStoredValueKind.String ||
                    !string.Equals(
                        StoredValue.CommandLine,
                        expectedCommandLine,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                StoredValue = StartupStoredValue.Missing();
                return true;
            }
        }

        private sealed class FakeUpdateTransport : IUpdateTransport
        {
            private readonly UpdateTransportResponse response;

            internal FakeUpdateTransport(UpdateTransportResponse response)
            {
                this.response = response;
            }

            internal int CallCount { get; private set; }

            public UpdateTransportResponse Get(Uri endpoint)
            {
                CallCount++;
                return response;
            }
        }

        private sealed class FakeEndpointAddressResolver : IEndpointAddressResolver
        {
            private readonly IPAddress[] addresses;

            internal FakeEndpointAddressResolver(IPAddress[] addresses)
            {
                this.addresses = addresses;
            }

            internal int CallCount { get; private set; }

            public IPAddress[] Resolve(string hostName)
            {
                CallCount++;
                return addresses;
            }
        }

        private sealed class FakeTelemetryTransport : ITelemetryTransport
        {
            internal int CallCount { get; private set; }
            internal Uri LastEndpoint { get; private set; }
            internal string LastPayload { get; private set; }

            public int PostJson(Uri endpoint, string payloadJson)
            {
                CallCount++;
                LastEndpoint = endpoint;
                LastPayload = payloadJson;
                return 204;
            }
        }

        private sealed class FakeUpdatePackageTransport : IUpdatePackageTransport
        {
            private readonly UpdatePackageTransportResponse response;

            internal FakeUpdatePackageTransport(UpdatePackageTransportResponse response)
            {
                this.response = response;
            }

            internal int CallCount { get; private set; }
            internal Exception ExceptionToThrow { get; set; }

            public UpdatePackageTransportResponse Get(Uri endpoint, int maximumBytes)
            {
                CallCount++;
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return response;
            }
        }
    }
}
