using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TinyHwBar.Tests
{
    internal static class TestProgram
    {
        private static readonly List<string> Failures = new List<string>();

        [STAThread]
        private static int Main()
        {
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "TinyHwBar-tests-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(temporaryRoot);
            try
            {
                Run("settings v1 migration", delegate { TestSettingsV1Migration(temporaryRoot); });
                Run("settings v2 round trip", delegate { TestSettingsV2RoundTrip(temporaryRoot); });
                Run("settings rejects unsafe endpoints", delegate { TestUnsafeEndpoints(temporaryRoot); });
                Run("settings save reports failure", delegate { TestSettingsSaveFailure(temporaryRoot); });
                Run("history ring buffer", TestHistoryRingBuffer);
                Run("history enforces 24 hour in-memory limit", TestHistoryMaximumAge);
                Run("bounded persistent history", delegate { TestPersistentHistory(temporaryRoot); });
                Run("GPU history schema compatibility", delegate { TestHistoryGpuSchemaCompatibility(temporaryRoot); });
                Run("history save reports failure", delegate { TestHistorySaveFailure(temporaryRoot); });
                Run("history rejects oversized files", delegate { TestOversizedHistory(temporaryRoot); });
                Run("gateway sampler remains passive while disabled", TestGatewayDisabledBoundary);
                Run("gateway target local-only policy", TestGatewayTargetPolicy);
                Run("network route selection decisions", TestNetworkRouteSelection);
                Run("Windows sockaddr layout", TestSockaddrLayout);
                Run("GPU role adapter classification", TestGpuRoleClassification);
                Run("GPU role snapshot mapping", TestGpuRoleSnapshotMapping);
                Run("loopback GPU status schema", TestLoopbackGpuStatusSchema);
                Run("display bounds include taskbar-reserved space", TestDisplayBoundsPositioning);
                Run("dashboard defaults and accessibility structure", TestDashboardStructure);
                Run("dashboard GPU values remain primary", TestDashboardGpuInformationHierarchy);
                Run("advanced service boundaries", V2ServiceTests.RunAll);
            }
            finally
            {
                DeleteTemporaryRoot(temporaryRoot);
            }

            if (Failures.Count == 0)
            {
                Console.WriteLine("PASS: all TinyHwBar local tests passed.");
                return 0;
            }

            foreach (string failure in Failures)
            {
                Console.Error.WriteLine("FAIL: " + failure);
            }

            return 1;
        }

        private static void TestSettingsV1Migration(string temporaryRoot)
        {
            string path = Path.Combine(temporaryRoot, "settings-v1.ini");
            File.WriteAllLines(
                path,
                new[]
                {
                    "Version=1",
                    "Left=120",
                    "Top=-40",
                    "Locked=True",
                    "ClickThrough=False",
                    "OpacityPercent=70",
                    "PersistHistory=False",
                    "GatewayLatencyEnabled=False",
                    "StartupEnabled=True",
                    "AutomaticUpdateEnabled=True",
                    "UpdateManifestUrl=https://example.com/manifest.json",
                    "LoopbackApiEnabled=True",
                    "TelemetryEnabled=True",
                    "TelemetryEndpoint=https://example.com/telemetry"
                },
                new UTF8Encoding(false));

            AppSettings settings = SettingsStore.Load(path);
            Assert(settings.HasSavedPosition, "v1 position was not retained");
            Assert(settings.Left == 120 && settings.Top == -40, "v1 coordinates changed");
            Assert(settings.Locked && !settings.ClickThrough, "v1 flags changed");
            Assert(settings.OpacityPercent == 70, "v1 opacity changed");
            Assert(settings.PersistHistory, "history should default on during migration");
            Assert(settings.GatewayLatencyEnabled, "gateway latency should default on during migration");
            Assert(!settings.StartupEnabled, "startup must default off");
            Assert(!settings.AutomaticUpdateEnabled, "automatic update must default off");
            Assert(settings.UpdateManifestUrl.Length == 0, "v1 update endpoint was retained");
            Assert(!settings.LoopbackApiEnabled, "loopback API must default off");
            Assert(!settings.TelemetryEnabled, "telemetry must default off");
            Assert(settings.TelemetryEndpoint.Length == 0, "v1 telemetry endpoint was retained");
        }

        private static void TestSettingsV2RoundTrip(string temporaryRoot)
        {
            string path = Path.Combine(temporaryRoot, "settings-v2.ini");
            AppSettings settings = AppSettings.CreateDefault();
            settings.HasSavedPosition = true;
            settings.Left = -200;
            settings.Top = 320;
            settings.Locked = true;
            settings.ClickThrough = true;
            settings.OpacityPercent = 60;
            settings.PersistHistory = false;
            settings.GatewayLatencyEnabled = false;
            settings.StartupEnabled = true;
            settings.AutomaticUpdateEnabled = true;
            settings.UpdateManifestUrl = "https://example.com/tinyhwbar/manifest.json";
            settings.LoopbackApiEnabled = true;
            settings.TelemetryEnabled = true;
            settings.TelemetryEndpoint = "https://example.com/tinyhwbar/telemetry";

            Assert(SettingsStore.Save(settings, path), "v2 settings were not saved");
            AppSettings loaded = SettingsStore.Load(path);

            Assert(loaded.Left == settings.Left && loaded.Top == settings.Top, "v2 coordinates changed");
            Assert(loaded.Locked && loaded.ClickThrough, "v2 flags changed");
            Assert(loaded.OpacityPercent == 60, "v2 opacity changed");
            Assert(!loaded.PersistHistory && !loaded.GatewayLatencyEnabled, "local options changed");
            Assert(loaded.StartupEnabled, "explicit startup setting was lost");
            Assert(loaded.AutomaticUpdateEnabled, "valid explicit update configuration was lost");
            Assert(
                loaded.UpdateManifestUrl == "https://example.com/tinyhwbar/manifest.json",
                "update URL changed");
            Assert(loaded.LoopbackApiEnabled, "explicit loopback API setting was lost");
            Assert(loaded.TelemetryEnabled, "valid explicit telemetry configuration was lost");
            Assert(
                loaded.TelemetryEndpoint == "https://example.com/tinyhwbar/telemetry",
                "telemetry URL changed");
            Assert(Directory.GetFiles(temporaryRoot, "*.tmp").Length == 0, "temporary setting file remained");
            Assert(Directory.GetFiles(temporaryRoot, "*.bak").Length == 0, "backup setting file remained");
        }

        private static void TestUnsafeEndpoints(string temporaryRoot)
        {
            string path = Path.Combine(temporaryRoot, "settings-unsafe.ini");
            AppSettings settings = AppSettings.CreateDefault();
            settings.AutomaticUpdateEnabled = true;
            settings.UpdateManifestUrl = "http://example.com/manifest.json";
            settings.TelemetryEnabled = true;
            settings.TelemetryEndpoint = "https://name:secret@example.com/upload";

            Assert(SettingsStore.Save(settings, path), "unsafe-endpoint settings were not saved");
            AppSettings loaded = SettingsStore.Load(path);

            Assert(!loaded.AutomaticUpdateEnabled, "HTTP update endpoint remained enabled");
            Assert(loaded.UpdateManifestUrl.Length == 0, "HTTP update endpoint was retained");
            Assert(!loaded.TelemetryEnabled, "credential-bearing telemetry endpoint remained enabled");
            Assert(loaded.TelemetryEndpoint.Length == 0, "credential-bearing endpoint was retained");
        }

        private static void TestSettingsSaveFailure(string temporaryRoot)
        {
            AppSettings settings = AppSettings.CreateDefault();
            Assert(
                !SettingsStore.Save(settings, string.Empty),
                "empty settings path was reported as saved");
            Assert(
                !SettingsStore.Save(null, Path.Combine(temporaryRoot, "settings-null.ini")),
                "null settings were reported as saved");

            string blockingFile = Path.Combine(temporaryRoot, "settings-path-blocker");
            File.WriteAllText(blockingFile, "not a directory", Encoding.UTF8);
            string invalidPath = Path.Combine(blockingFile, "settings.ini");

            Assert(
                !SettingsStore.Save(settings, invalidPath),
                "path below an existing file was reported as saved");
            Assert(!File.Exists(invalidPath), "failed save left an unexpected settings file");
        }

        private static void TestHistoryRingBuffer()
        {
            MetricHistory history = new MetricHistory(3);
            DateTime now = DateTime.UtcNow;
            for (int index = 0; index < 5; index++)
            {
                history.Add(CreatePoint(now.AddSeconds(index), index));
            }

            HistoryPoint[] points = history.Snapshot();
            Assert(points.Length == 3, "ring buffer did not enforce capacity");
            Assert(points[0].CpuPercent == 2, "ring buffer oldest point is incorrect");
            Assert(points[2].CpuPercent == 4, "ring buffer newest point is incorrect");

            history.Clear();
            Assert(history.Count == 0 && history.Snapshot().Length == 0, "ring buffer did not clear");
        }

        private static void TestPersistentHistory(string temporaryRoot)
        {
            string path = Path.Combine(temporaryRoot, "history.csv");
            HistoryStore store = new HistoryStore(path);
            string emptyPath = Path.Combine(temporaryRoot, "history-empty.csv");
            HistoryStore emptyStore = new HistoryStore(emptyPath);
            HistorySaveFailure emptyFailure;
            Assert(
                emptyStore.Save(new HistoryPoint[0], out emptyFailure),
                "empty history save failed");
            Assert(emptyFailure == null, "successful empty history save reported a failure");
            Assert(File.Exists(emptyPath), "empty history save did not create a file");
            Assert(
                File.ReadAllLines(emptyPath, Encoding.UTF8)[0] == "TinyHwBarHistory=1",
                "empty history save did not write the history header");

            DateTime now = DateTime.UtcNow;
            HistoryPoint[] points = new HistoryPoint[HistoryStore.MaximumPointCount + 10];
            for (int index = 0; index < points.Length; index++)
            {
                points[index] = CreatePoint(now.AddSeconds(index - points.Length), index % 101);
            }

            Assert(store.Save(points), "persistent history save failed");
            HistoryPoint[] loaded = store.Load();

            Assert(loaded.Length == HistoryStore.MaximumPointCount, "persistent history exceeded point cap");
            Assert(loaded[0].CpuPercent == 10, "persistent history retained the wrong oldest point");
            Assert(loaded[loaded.Length - 1].CpuPercent == 0, "persistent history retained the wrong newest point");

            string agePath = Path.Combine(temporaryRoot, "history-age.csv");
            HistoryStore ageStore = new HistoryStore(agePath);
            DateTime ageNow = DateTime.UtcNow;
            Assert(
                ageStore.Save(new[]
                {
                    CreatePoint(ageNow - HistoryStore.MaximumAge - TimeSpan.FromMinutes(1.0), 11),
                    CreatePoint(ageNow, 22)
                }),
                "age-bounded history save failed");
            HistoryPoint[] ageLimited = ageStore.Load();
            Assert(ageLimited.Length == 1, "persistent history retained a point older than 24 hours");
            Assert(ageLimited[0].CpuPercent == 22, "persistent history discarded the current point");

            string ageLoadPath = Path.Combine(temporaryRoot, "history-age-load.csv");
            File.WriteAllLines(
                ageLoadPath,
                new[]
                {
                    "TinyHwBarHistory=1",
                    CreateHistoryCsvLine(
                        ageNow - HistoryStore.MaximumAge - TimeSpan.FromMinutes(1.0),
                        33),
                    CreateHistoryCsvLine(ageNow, 44)
                },
                new UTF8Encoding(false));
            HistoryPoint[] ageLoadedFromDisk = new HistoryStore(ageLoadPath).Load();
            Assert(
                ageLoadedFromDisk.Length == 1,
                "history load retained a point older than 24 hours");
            Assert(
                ageLoadedFromDisk[0].CpuPercent == 44,
                "history load discarded the current point");

            store.Clear();
            Assert(!File.Exists(path), "history clear left the primary file");
            Assert(!File.Exists(path + ".bak"), "history clear left the backup file");

            string staleTemporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string unrelatedTemporary = path + ".not-owned.tmp";
            File.WriteAllText(staleTemporary, "stale", Encoding.ASCII);
            File.WriteAllText(unrelatedTemporary, "preserve", Encoding.ASCII);
            Assert(store.Clear(), "history clear reported a failure for owned temporary files");
            Assert(!File.Exists(staleTemporary), "history clear left an owned crash temporary file");
            Assert(File.Exists(unrelatedTemporary), "history clear deleted a non-owned temporary file");
        }

        private static void TestHistoryMaximumAge()
        {
            MetricHistory history = new MetricHistory(10);
            DateTime initialUtc = DateTime.UtcNow;
            history.Add(CreatePoint(initialUtc, 25));

            Assert(history.Snapshot(initialUtc).Length == 1, "new history point was discarded");
            Assert(
                history.Snapshot(initialUtc.Add(MetricHistory.MaximumAge).AddTicks(1)).Length == 0,
                "history older than 24 hours remained in memory");
        }

        private static void TestHistoryGpuSchemaCompatibility(string temporaryRoot)
        {
            string path = Path.Combine(temporaryRoot, "history-gpu-schema.csv");
            HistoryStore store = new HistoryStore(path);
            long timestampTicks = DateTime.UtcNow.AddMinutes(-1.0).Ticks;
            File.WriteAllLines(
                path,
                new[]
                {
                    "TinyHwBarHistory=1",
                    timestampTicks.ToString(CultureInfo.InvariantCulture) +
                    ",11,22,33,44,55,66,77,88"
                },
                Encoding.UTF8);

            HistoryPoint[] legacyPoints = store.Load();
            Assert(legacyPoints.Length == 1, "legacy nine-column GPU history did not load");
            Assert(
                legacyPoints[0].DiscreteGpuPercent == 33 &&
                legacyPoints[0].VideoMemoryPercent == 44 &&
                legacyPoints[0].TemperatureCelsius == 55 &&
                legacyPoints[0].IntegratedGpuPercent == 88 &&
                !legacyPoints[0].GatewayLatencyMilliseconds.HasValue,
                "legacy GPU history columns changed meaning");

            Assert(store.Save(legacyPoints), "legacy GPU history did not round trip");
            string[] roundTripLines = File.ReadAllLines(path, Encoding.UTF8);
            Assert(
                roundTripLines.Length == 2 &&
                roundTripLines[0] == "TinyHwBarHistory=1",
                "history schema header changed during round trip");
            HistoryPoint[] roundTripPoints = store.Load();
            Assert(
                roundTripPoints.Length == 1 &&
                roundTripPoints[0].DiscreteGpuPercent == 33 &&
                roundTripPoints[0].IntegratedGpuPercent == 88,
                "GPU history values moved during round trip");

            File.WriteAllLines(
                path,
                new[]
                {
                    "TinyHwBarHistory=1",
                    timestampTicks.ToString(CultureInfo.InvariantCulture) +
                    ",12,23,34,45,56,67,78,89,19"
                },
                Encoding.UTF8);
            HistoryPoint[] currentPoints = store.Load();
            Assert(
                currentPoints.Length == 1 &&
                currentPoints[0].DiscreteGpuPercent == 34 &&
                currentPoints[0].VideoMemoryPercent == 45 &&
                currentPoints[0].TemperatureCelsius == 56 &&
                currentPoints[0].IntegratedGpuPercent == 89 &&
                currentPoints[0].GatewayLatencyMilliseconds == 19,
                "current ten-column GPU history did not preserve its column order");
        }

        private static void TestHistorySaveFailure(string temporaryRoot)
        {
            string blockingFile = Path.Combine(temporaryRoot, "history-path-blocker");
            File.WriteAllText(blockingFile, "not a directory", Encoding.UTF8);
            HistoryStore store = new HistoryStore(Path.Combine(blockingFile, "history.csv"));
            HistorySaveFailure failure;
            Assert(
                !store.Save(
                    new[] { CreatePoint(DateTime.UtcNow, 1) },
                    out failure),
                "history write failure was reported as successful");
            Assert(failure != null, "history write failure did not report diagnostics");
            Assert(
                failure.Stage == HistorySaveFailureStage.CreateDirectory,
                "history write failure reported the wrong stage");
            Assert(
                failure.ExceptionType == "IOException",
                "history write failure reported the wrong exception type");
            Assert(
                failure.ToSafeDisplayText().IndexOf(
                    temporaryRoot,
                    StringComparison.OrdinalIgnoreCase) < 0,
                "history diagnostics exposed a local path");
        }

        private static void TestOversizedHistory(string temporaryRoot)
        {
            string path = Path.Combine(temporaryRoot, "history-oversized.csv");
            File.WriteAllText(path, new string('x', (2 * 1024 * 1024) + 1), Encoding.ASCII);
            HistoryStore store = new HistoryStore(path);
            Assert(store.Load().Length == 0, "oversized history file was parsed");
            store.Clear();
        }

        private static void TestGatewayDisabledBoundary()
        {
            int sendCount = 0;
            GatewayEchoSender sender = delegate(IPAddress target, int timeout)
            {
                sendCount++;
                return Task.FromResult(GatewayEchoResult.CreateAvailable(1));
            };

            using (GatewayLatencySampler sampler = new GatewayLatencySampler(
                sender,
                TimeSpan.FromSeconds(10),
                750))
            {
                GatewayLatencyMetrics disabled = sampler.Sample(
                    NetworkMetrics.CreateAvailable(
                        "test-interface",
                        "test",
                        "192.0.2.1",
                        null,
                        null,
                        null));
                Assert(disabled.Status == GatewayLatencyStatus.Disabled, "disabled sampler changed state");
                Assert(sendCount == 0, "disabled sampler invoked the sender");

                sampler.SetEnabled(true);
                GatewayLatencyMetrics unavailable = sampler.Sample(
                    NetworkMetrics.CreateUnavailable());
                Assert(
                    unavailable.Status == GatewayLatencyStatus.NetworkUnavailable,
                    "unavailable network was not reported");
                Assert(sendCount == 0, "unavailable network invoked the sender");
            }
        }

        private static void TestGatewayTargetPolicy()
        {
            Assert(
                GatewayLatencySampler.IsLocalGatewayAddress(IPAddress.Parse("192.168.1.1")),
                "private IPv4 gateway was rejected");
            Assert(
                GatewayLatencySampler.IsLocalGatewayAddress(IPAddress.Parse("100.64.0.1")),
                "carrier-grade NAT gateway was rejected");
            Assert(
                GatewayLatencySampler.IsLocalGatewayAddress(IPAddress.Parse("169.254.1.1")),
                "link-local IPv4 gateway was rejected");
            Assert(
                GatewayLatencySampler.IsLocalGatewayAddress(IPAddress.Parse("fe80::1")),
                "link-local IPv6 gateway was rejected");
            Assert(
                GatewayLatencySampler.IsLocalGatewayAddress(IPAddress.Parse("fd00::1")),
                "IPv6 unique-local gateway was rejected");
            Assert(
                !GatewayLatencySampler.IsLocalGatewayAddress(IPAddress.Parse("8.8.8.8")),
                "public IPv4 address passed the local-gateway policy");
            Assert(
                !GatewayLatencySampler.IsLocalGatewayAddress(
                    IPAddress.Parse("2606:4700:4700::1111")),
                "global IPv6 address passed the local-gateway policy");
        }

        private static void TestDisplayBoundsPositioning()
        {
            Rectangle screenBounds = new Rectangle(0, 0, 1920, 1080);
            Rectangle workingArea = new Rectangle(0, 0, 1920, 1020);
            Rectangle taskbarReservedPosition = new Rectangle(1200, 1020, 330, 36);

            Assert(
                !workingArea.Contains(taskbarReservedPosition),
                "regression fixture is unexpectedly inside the working area");
            Assert(
                MonitorForm.IsFullyInsideBounds(taskbarReservedPosition, screenBounds),
                "taskbar-reserved position was rejected by the full display bounds");

            Rectangle unchanged = MonitorForm.ConstrainToBounds(
                taskbarReservedPosition,
                screenBounds);
            Assert(
                unchanged == taskbarReservedPosition,
                "valid taskbar-reserved position was moved during screen correction");

            Rectangle outsideScreen = new Rectangle(1800, 1060, 330, 36);
            Rectangle corrected = MonitorForm.ConstrainToBounds(outsideScreen, screenBounds);
            Assert(
                corrected == new Rectangle(1590, 1044, 330, 36),
                "off-screen position was not kept fully visible");

            Rectangle bottomOfTaskbarReservedSpace =
                new Rectangle(1200, 1044, 330, 36);
            Assert(
                MonitorForm.IsFullyInsideBounds(
                    bottomOfTaskbarReservedSpace,
                    screenBounds),
                "bottom-most taskbar-reserved position was rejected");
            Assert(
                MonitorForm.ConstrainToBounds(
                    bottomOfTaskbarReservedSpace,
                    screenBounds) == bottomOfTaskbarReservedSpace,
                "bottom-most taskbar-reserved position was moved");

            Rectangle negativeScreenBounds = new Rectangle(-2560, -200, 2560, 1440);
            Rectangle negativeOffscreen = new Rectangle(-2700, 1250, 330, 36);
            Assert(
                MonitorForm.ConstrainToBounds(
                    negativeOffscreen,
                    negativeScreenBounds) == new Rectangle(-2560, 1204, 330, 36),
                "negative-coordinate display bounds were clamped incorrectly");
            Assert(
                !MonitorForm.IsFullyInsideBounds(
                    new Rectangle(int.MaxValue, 0, 330, 36),
                    screenBounds),
                "overflowing positive saved position was accepted");
            Assert(
                !MonitorForm.IsFullyInsideBounds(
                    new Rectangle(int.MinValue, 0, 330, 36),
                    screenBounds),
                "overflowing negative saved position was accepted");

            Point locationAfterSystemCorrection = new Point(300, 300);
            Point nextLocation = MonitorForm.CalculateDragLocation(
                new Point(200, 200),
                locationAfterSystemCorrection,
                new Point(210, 215));
            Assert(
                nextLocation == new Point(310, 315),
                "rebased cross-display drag anchor caused a location jump");
        }

        private static void TestNetworkRouteSelection()
        {
            Assert(
                NetworkSampler.IsNoRouteError(50),
                "missing IPv4/IPv6 protocol stack was not classified as no route");
            Assert(
                !NetworkSampler.IsNoRouteError(87),
                "invalid native route query parameters were hidden as no route");

            Assert(
                (int)NetworkSelectionStatus.Available == 0 &&
                (int)NetworkSelectionStatus.NoRoute == 1 &&
                (int)NetworkSelectionStatus.SplitRoute == 2 &&
                (int)NetworkSelectionStatus.RouteLookupFailed == 3 &&
                (int)NetworkSelectionStatus.RouteInterfaceMissing == 4 &&
                (int)NetworkSelectionStatus.CounterUnavailable == 5 &&
                (int)NetworkSelectionStatus.Disposed == 6,
                "network selection status numeric contract changed");

            List<NetworkRouteIdentity> interfaces = new List<NetworkRouteIdentity>
            {
                new NetworkRouteIdentity("vpn-tunnel", 4, 14),
                new NetworkRouteIdentity("ethernet", 18, 28),
                new NetworkRouteIdentity("ipv6-only", null, 30)
            };

            NetworkRouteDecision tunnel = NetworkSampler.ResolveRoute(
                interfaces,
                NetworkRouteLookupResult.CreateSuccess(AddressFamily.InterNetwork, 4),
                NetworkRouteLookupResult.CreateNoRoute(AddressFamily.InterNetworkV6, 1231));
            Assert(
                tunnel.Status == NetworkSelectionStatus.Available &&
                tunnel.InterfaceId == "vpn-tunnel" &&
                tunnel.Families == NetworkRouteFamilies.IPv4,
                "route-selected Tunnel interface was not retained");

            NetworkRouteDecision ipv6Only = NetworkSampler.ResolveRoute(
                interfaces,
                NetworkRouteLookupResult.CreateNoRoute(AddressFamily.InterNetwork, 1231),
                NetworkRouteLookupResult.CreateSuccess(AddressFamily.InterNetworkV6, 30));
            Assert(
                ipv6Only.Status == NetworkSelectionStatus.Available &&
                ipv6Only.InterfaceId == "ipv6-only" &&
                ipv6Only.Families == NetworkRouteFamilies.IPv6,
                "IPv6-only route was not selected");

            NetworkRouteDecision dualStack = NetworkSampler.ResolveRoute(
                interfaces,
                NetworkRouteLookupResult.CreateSuccess(AddressFamily.InterNetwork, 18),
                NetworkRouteLookupResult.CreateSuccess(AddressFamily.InterNetworkV6, 28));
            Assert(
                dualStack.Status == NetworkSelectionStatus.Available &&
                dualStack.InterfaceId == "ethernet" &&
                dualStack.Families ==
                    (NetworkRouteFamilies.IPv4 | NetworkRouteFamilies.IPv6),
                "same-interface dual-stack route was not selected");

            NetworkRouteDecision splitRoute = NetworkSampler.ResolveRoute(
                interfaces,
                NetworkRouteLookupResult.CreateSuccess(AddressFamily.InterNetwork, 4),
                NetworkRouteLookupResult.CreateSuccess(AddressFamily.InterNetworkV6, 28));
            Assert(
                splitRoute.Status == NetworkSelectionStatus.SplitRoute,
                "split IPv4/IPv6 routes were not degraded explicitly");

            NetworkRouteDecision noRoute = NetworkSampler.ResolveRoute(
                interfaces,
                NetworkRouteLookupResult.CreateNoRoute(AddressFamily.InterNetwork, 1231),
                NetworkRouteLookupResult.CreateNoRoute(AddressFamily.InterNetworkV6, 1231));
            Assert(
                noRoute.Status == NetworkSelectionStatus.NoRoute,
                "missing IPv4/IPv6 routes were not reported");

            NetworkRouteDecision lookupFailed = NetworkSampler.ResolveRoute(
                interfaces,
                NetworkRouteLookupResult.CreateSuccess(AddressFamily.InterNetwork, 18),
                NetworkRouteLookupResult.CreateFailed(AddressFamily.InterNetworkV6, 87));
            Assert(
                lookupFailed.Status == NetworkSelectionStatus.RouteLookupFailed,
                "route API failure was hidden by another successful family");

            NetworkRouteDecision missingInterface = NetworkSampler.ResolveRoute(
                interfaces,
                NetworkRouteLookupResult.CreateSuccess(AddressFamily.InterNetwork, 99),
                NetworkRouteLookupResult.CreateNoRoute(AddressFamily.InterNetworkV6, 1231));
            Assert(
                missingInterface.Status == NetworkSelectionStatus.RouteInterfaceMissing,
                "unknown route interface index was not reported");

            List<NetworkRouteIdentity> duplicateInterfaces =
                new List<NetworkRouteIdentity>
                {
                    new NetworkRouteIdentity("first", 18, null),
                    new NetworkRouteIdentity("second", 18, null)
                };
            NetworkRouteDecision duplicateInterface = NetworkSampler.ResolveRoute(
                duplicateInterfaces,
                NetworkRouteLookupResult.CreateSuccess(AddressFamily.InterNetwork, 18),
                NetworkRouteLookupResult.CreateNoRoute(AddressFamily.InterNetworkV6, 1231));
            Assert(
                duplicateInterface.Status == NetworkSelectionStatus.RouteInterfaceMissing,
                "duplicate route interface index was selected ambiguously");
        }

        private static void TestSockaddrLayout()
        {
            IPAddress ipv4Address = IPAddress.Parse("192.0.2.1");
            byte[] ipv4 = NetworkSampler.BuildSockaddrBytes(ipv4Address);
            Assert(ipv4.Length == 16, "IPv4 sockaddr length changed");
            Assert(
                BitConverter.ToUInt16(ipv4, 0) == (ushort)AddressFamily.InterNetwork,
                "IPv4 sockaddr family is incorrect");
            Assert(ipv4[2] == 0 && ipv4[3] == 0, "IPv4 sockaddr port is not zero");
            AssertBytesEqual(ipv4Address.GetAddressBytes(), ipv4, 4, "IPv4 sockaddr address");
            for (int index = 8; index < ipv4.Length; index++)
            {
                Assert(ipv4[index] == 0, "IPv4 sockaddr padding is not zero");
            }

            IPAddress ipv6Address = new IPAddress(
                IPAddress.Parse("fe80::1234").GetAddressBytes(),
                27);
            byte[] ipv6 = NetworkSampler.BuildSockaddrBytes(ipv6Address);
            Assert(ipv6.Length == 28, "IPv6 sockaddr length changed");
            Assert(
                BitConverter.ToUInt16(ipv6, 0) == (ushort)AddressFamily.InterNetworkV6,
                "IPv6 sockaddr family is incorrect");
            for (int index = 2; index < 8; index++)
            {
                Assert(ipv6[index] == 0, "IPv6 sockaddr port or flow info is not zero");
            }

            AssertBytesEqual(ipv6Address.GetAddressBytes(), ipv6, 8, "IPv6 sockaddr address");
            Assert(
                BitConverter.ToUInt32(ipv6, 24) == 27,
                "IPv6 sockaddr scope identifier is incorrect");
        }

        private static void AssertBytesEqual(
            byte[] expected,
            byte[] actual,
            int actualOffset,
            string description)
        {
            for (int index = 0; index < expected.Length; index++)
            {
                Assert(
                    actual[actualOffset + index] == expected[index],
                    description + " differs at byte " + index.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void TestGpuRoleClassification()
        {
            Assert(
                GpuRoleSampler.ResolveRoleStatus(
                    GpuAdapterRole.Integrated,
                    new[]
                {
                    IntelAdapterIntegrationKind.Integrated
                }) == IntelGpuDataStatus.Available,
                "single integrated adapter was not selected");
            Assert(
                GpuRoleSampler.ResolveRoleStatus(
                    GpuAdapterRole.Discrete,
                    new[]
                {
                    IntelAdapterIntegrationKind.Discrete
                }) == IntelGpuDataStatus.Available,
                "single discrete adapter was not selected");
            Assert(
                GpuRoleSampler.ResolveRoleStatus(
                    GpuAdapterRole.Integrated,
                    new[]
                {
                    IntelAdapterIntegrationKind.Discrete
                }) == IntelGpuDataStatus.NotFound,
                "single discrete adapter was reported as an integrated GPU");
            Assert(
                GpuRoleSampler.ResolveRoleStatus(
                    GpuAdapterRole.Integrated,
                    new[]
                {
                    IntelAdapterIntegrationKind.Integrated,
                    IntelAdapterIntegrationKind.Discrete
                }) == IntelGpuDataStatus.Available,
                "integrated adapter was not selected alongside a discrete adapter");
            Assert(
                GpuRoleSampler.ResolveRoleStatus(
                    GpuAdapterRole.Integrated,
                    new[]
                {
                    IntelAdapterIntegrationKind.Integrated,
                    IntelAdapterIntegrationKind.Integrated
                }) == IntelGpuDataStatus.AmbiguousAdapter,
                "multiple integrated adapters were not reported as ambiguous");
            Assert(
                GpuRoleSampler.ResolveRoleStatus(
                    GpuAdapterRole.Integrated,
                    new[]
                {
                    IntelAdapterIntegrationKind.Integrated,
                    IntelAdapterIntegrationKind.Unknown
                }) == IntelGpuDataStatus.AmbiguousAdapter,
                "unknown adapter was ignored alongside an integrated adapter");
            Assert(
                GpuRoleSampler.ResolveRoleStatus(
                    GpuAdapterRole.Integrated,
                    new[]
                {
                    IntelAdapterIntegrationKind.Discrete,
                    IntelAdapterIntegrationKind.Unknown
                }) == IntelGpuDataStatus.Unsupported,
                "single unclassified adapter did not degrade explicitly");
            Assert(
                GpuRoleSampler.ResolveRoleStatus(
                    GpuAdapterRole.Integrated,
                    new[]
                {
                    IntelAdapterIntegrationKind.Unknown,
                    IntelAdapterIntegrationKind.Unknown
                }) == IntelGpuDataStatus.AmbiguousAdapter,
                "multiple unknown adapters were not reported as ambiguous");

            Assert(GpuRoleSampler.IsSupportedVendor(GpuRoleSampler.NvidiaVendorId), "NVIDIA vendor was rejected");
            Assert(GpuRoleSampler.IsSupportedVendor(GpuRoleSampler.AmdVendorId), "AMD vendor was rejected");
            Assert(GpuRoleSampler.IsSupportedVendor(GpuRoleSampler.IntelVendorId), "Intel vendor was rejected");
            Assert(!GpuRoleSampler.IsSupportedVendor(0x1414), "unsupported vendor was accepted");
            Assert(
                GpuRoleSampler.IsDxgiAdapterCandidate(GpuRoleSampler.AmdVendorId, 0),
                "physical AMD adapter was rejected");
            Assert(
                !GpuRoleSampler.IsDxgiAdapterCandidate(
                    GpuRoleSampler.NvidiaVendorId,
                    GpuRoleSampler.DxgiAdapterFlagRemote),
                "remote adapter was accepted");
            Assert(
                !GpuRoleSampler.IsDxgiAdapterCandidate(
                    GpuRoleSampler.IntelVendorId,
                    GpuRoleSampler.DxgiAdapterFlagSoftware),
                "software adapter was accepted");
            Assert(!GpuRoleSampler.ShouldIncludeHardwareCandidate(false), "non-hardware adapter was accepted");
            Assert(GpuRoleSampler.ShouldIncludeHardwareCandidate(true), "hardware adapter was rejected");
            Assert(GpuRoleSampler.ShouldIncludeHardwareCandidate(null), "unknown hardware trait was dropped instead of degraded");

            HashSet<string> seenLuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert(GpuRoleSampler.TryRegisterAdapterLuid(seenLuids, 1, 2), "first LUID was rejected");
            Assert(!GpuRoleSampler.TryRegisterAdapterLuid(seenLuids, 1, 2), "duplicate LUID was not removed");
            Assert(GpuRoleSampler.TryRegisterAdapterLuid(seenLuids, 2, 2), "different low LUID was removed");
            Assert(GpuRoleSampler.TryRegisterAdapterLuid(seenLuids, 1, 3), "different high LUID was removed");
            Assert(
                GpuRoleSampler.IsSameReportedAdapterIdentity(
                    " Intel(R) Graphics ",
                    GpuRoleSampler.IntelVendorId,
                    1,
                    2,
                    "intel(r) graphics",
                    GpuRoleSampler.IntelVendorId,
                    1,
                    2),
                "unclassified mirrored adapter identity was not recognized");
            Assert(
                !GpuRoleSampler.IsSameReportedAdapterIdentity(
                    "Intel(R) Graphics",
                    GpuRoleSampler.IntelVendorId,
                    1,
                    2,
                    "Intel(R) Graphics",
                    GpuRoleSampler.NvidiaVendorId,
                    1,
                    2),
                "different vendors were treated as the same adapter identity");
            Assert(
                !GpuRoleSampler.IsSameReportedAdapterIdentity(
                    string.Empty,
                    GpuRoleSampler.IntelVendorId,
                    1,
                    2,
                    string.Empty,
                    GpuRoleSampler.IntelVendorId,
                    1,
                    2),
                "empty adapter descriptions were collapsed");
            Assert(
                !GpuRoleSampler.IsSameReportedAdapterIdentity(
                    "NVIDIA GeForce RTX 5070 Ti Laptop GPU",
                    GpuRoleSampler.NvidiaVendorId,
                    1,
                    2,
                    "NVIDIA GeForce RTX 5070 Ti Laptop GPU",
                    GpuRoleSampler.NvidiaVendorId,
                    3,
                    4),
                "same-vendor same-model adapters with distinct LUIDs were collapsed");
        }

        private static void TestGpuRoleSnapshotMapping()
        {
            HardwareSnapshot snapshot = new HardwareSnapshot();
            IntelGpuMetrics discreteMetrics = IntelGpuMetrics.CreateSampled(
                "AMD discrete test adapter",
                GpuRoleSampler.AmdVendorId,
                GpuAdapterRole.Discrete,
                1,
                2,
                42,
                512L * 1024L * 1024L,
                2L * 1024L * 1024L * 1024L,
                null,
                8L * 1024L * 1024L * 1024L,
                IntelGpuDataStatus.Available,
                IntelGpuDataStatus.Available);
            HardwareSampler.ApplyDiscreteGpuMetrics(snapshot, discreteMetrics);
            Assert(snapshot.DiscreteGpuDetected, "discrete adapter mapping lost detection");
            Assert(snapshot.GpuMode == GpuDisplayMode.Available, "discrete adapter mapping lost availability");
            Assert(snapshot.GpuPercent == 42, "discrete utilization mapping changed");
            Assert(snapshot.VideoMemoryPercent == 25, "discrete memory percentage mapping changed");
            Assert(!snapshot.TemperatureCelsius.HasValue, "non-NVIDIA temperature was fabricated");

            IntelGpuMetrics integratedMetrics = IntelGpuMetrics.CreateSampled(
                "AMD integrated test adapter",
                GpuRoleSampler.AmdVendorId,
                GpuAdapterRole.Integrated,
                3,
                4,
                17,
                128L * 1024L * 1024L,
                512L * 1024L * 1024L,
                256L * 1024L * 1024L,
                8L * 1024L * 1024L * 1024L,
                IntelGpuDataStatus.Available,
                IntelGpuDataStatus.Available);
            HardwareSampler.ApplyIntegratedGpuMetrics(snapshot, integratedMetrics);
            Assert(snapshot.IntegratedGpuDetected, "integrated adapter mapping lost detection");
            Assert(snapshot.IntegratedGpuPercent == 17, "integrated utilization mapping changed");
            Assert(
                snapshot.IntegratedSharedMemoryBytes == 256L * 1024L * 1024L,
                "integrated shared memory mapping changed");

            HardwareSampler.ApplyDiscreteGpuMetrics(
                snapshot,
                IntelGpuMetrics.CreateUnavailable(
                    IntelGpuDataStatus.AmbiguousAdapter,
                    GpuAdapterRole.Discrete));
            Assert(
                !snapshot.DiscreteGpuDetected &&
                snapshot.DiscreteAdapterStatus == IntelGpuDataStatus.AmbiguousAdapter,
                "discrete ambiguity was not preserved");
            Assert(
                snapshot.IntegratedGpuDetected && snapshot.IntegratedGpuPercent == 17,
                "discrete failure cleared integrated GPU data");
        }

        private static void TestLoopbackGpuStatusSchema()
        {
            HardwareSnapshot snapshot = new HardwareSnapshot
            {
                GpuMode = GpuDisplayMode.Available,
                GpuPercent = 41,
                VideoMemoryPercent = 23,
                DiscreteGpuVendorId = GpuRoleSampler.AmdVendorId,
                IntegratedGpuPercent = 19,
                IntegratedSharedMemoryBytes = 123456,
                IntegratedGpuVendorId = GpuRoleSampler.AmdVendorId,
                IntegratedAdapterStatus = IntelGpuDataStatus.Available
            };
            string genericJson = MonitorForm.BuildLoopbackStatusJson(snapshot);
            Assert(genericJson.IndexOf("\"schemaVersion\":2", StringComparison.Ordinal) >= 0, "GPU API schema version changed");
            Assert(genericJson.IndexOf("\"discreteGpuPercent\":41", StringComparison.Ordinal) >= 0, "generic discrete GPU API field is missing");
            Assert(genericJson.IndexOf("\"integratedGpuPercent\":19", StringComparison.Ordinal) >= 0, "generic integrated GPU API field is missing");
            Assert(genericJson.IndexOf("\"nvidiaGpuPercent\":null", StringComparison.Ordinal) >= 0, "AMD data leaked into NVIDIA compatibility field");
            Assert(genericJson.IndexOf("\"intelGpuPercent\":null", StringComparison.Ordinal) >= 0, "AMD data leaked into Intel compatibility field");

            snapshot.DiscreteGpuVendorId = GpuRoleSampler.NvidiaVendorId;
            snapshot.IntegratedGpuVendorId = GpuRoleSampler.IntelVendorId;
            string compatibleJson = MonitorForm.BuildLoopbackStatusJson(snapshot);
            Assert(compatibleJson.IndexOf("\"nvidiaGpuPercent\":41", StringComparison.Ordinal) >= 0, "NVIDIA compatibility field was not retained");
            Assert(compatibleJson.IndexOf("\"intelGpuPercent\":19", StringComparison.Ordinal) >= 0, "Intel compatibility field was not retained");
        }

        private static void TestDashboardStructure()
        {
            AppSettings settings = AppSettings.CreateDefault();
            MetricHistory history = new MetricHistory();

            using (DashboardForm dashboard = new DashboardForm(settings, history))
            {
                Assert(
                    dashboard.AccessibleName == "TinyHwBar control center",
                    "dashboard accessible name changed");
                Assert(
                    dashboard.Text == "TinyHwBar 控制中心",
                    "dashboard title includes a version label or changed unexpectedly");
                Assert(
                    FindFirstControl<Label>(
                        dashboard,
                        delegate(Label control)
                        {
                            return control.AccessibleName == "独立显卡";
                        }) != null,
                    "discrete GPU role card is missing");
                Assert(
                    FindFirstControl<Label>(
                        dashboard,
                        delegate(Label control)
                        {
                            return control.AccessibleName == "核显";
                        }) != null,
                    "integrated GPU role card is missing");
                Assert(
                    GetContrastRatio(dashboard.ForeColor, dashboard.BackColor) >= 4.5,
                    "dashboard text contrast is below 4.5:1");
                Assert(
                    dashboard.AcceptButton != null && dashboard.CancelButton != null,
                    "dashboard keyboard accept/cancel actions are missing");

                TabControl tabs = FindFirstControl<TabControl>(dashboard, null);
                Assert(tabs != null, "dashboard tab control is missing");
                Assert(tabs.TabPages.Count == 4, "dashboard does not contain four pages");
                Assert(tabs.TabIndex == 0, "dashboard tab control is not first in keyboard order");

                ComboBox opacity = FindFirstControl<ComboBox>(
                    dashboard,
                    delegate(ComboBox control)
                    {
                        return control.AccessibleName == "监控条透明度";
                    });
                Assert(opacity != null && opacity.TabIndex == 0, "opacity keyboard control is missing");

                Assert(
                    FindCheckBox(dashboard, "保存最近 900 个指标点到本机").Checked,
                    "history persistence is not enabled in the default dashboard");
                Assert(
                    FindCheckBox(
                        dashboard,
                        "测量所选接口报告的本地网关延迟").Checked,
                    "gateway latency is not enabled in the default dashboard");
                Assert(
                    !FindCheckBox(dashboard, "登录 Windows 后启动 TinyHwBar").Checked,
                    "startup is enabled in the default dashboard");
                Assert(
                    !FindCheckBox(dashboard, "启动后自动检查一次更新").Checked,
                    "automatic update checks are enabled in the default dashboard");
                Assert(
                    !FindCheckBox(dashboard, "启用仅限本机的状态 API").Checked,
                    "loopback API is enabled in the default dashboard");
                Assert(
                    !FindCheckBox(dashboard, "允许逐次确认的遥测/诊断摘要发送").Checked,
                    "telemetry is enabled in the default dashboard");

                TextBox updateEndpoint = FindFirstControl<TextBox>(
                    dashboard,
                    delegate(TextBox control)
                    {
                        return control.AccessibleName == "更新清单 HTTPS 地址";
                    });
                TextBox telemetryEndpoint = FindFirstControl<TextBox>(
                    dashboard,
                    delegate(TextBox control)
                    {
                        return control.AccessibleName == "遥测 HTTPS 地址";
                    });
                Assert(
                    updateEndpoint != null && updateEndpoint.Text.Length == 0,
                    "default update endpoint is not empty");
                Assert(
                    telemetryEndpoint != null && telemetryEndpoint.Text.Length == 0,
                    "default telemetry endpoint is not empty");

                HistoryChartControl chart = FindFirstControl<HistoryChartControl>(dashboard, null);
                Assert(chart != null, "history chart is missing");
                Assert(!chart.TabStop, "history graphic unexpectedly consumes keyboard focus");
                Assert(
                    chart.AccessibleRole == AccessibleRole.Graphic,
                    "history chart accessible role changed");
                Assert(
                    chart.AccessibleDescription.IndexOf(
                        "discrete GPU",
                        StringComparison.Ordinal) >= 0 &&
                    chart.AccessibleDescription.IndexOf(
                        "integrated GPU",
                        StringComparison.Ordinal) >= 0,
                    "history chart still exposes vendor-specific GPU categories");
                Assert(
                    HistoryChartControl.DiscreteGpuLegendLabel == "独立显卡" &&
                    HistoryChartControl.IntegratedGpuLegendLabel == "核显",
                    "history chart GPU legends changed");
                AssertChartSeriesContrast(chart, "cpuPen");
                AssertChartSeriesContrast(chart, "memoryPen");
                AssertChartSeriesContrast(chart, "nvidiaPen");
                AssertChartSeriesContrast(chart, "intelPen");

                Label adapterStatus = FindFirstControl<Label>(
                    dashboard,
                    delegate(Label control)
                    {
                        return control.AccessibleName == "监测网络适配器";
                    });
                Assert(adapterStatus != null, "network adapter status label is missing");
                AssertNetworkAdapterStatus(
                    dashboard,
                    adapterStatus,
                    NetworkSelectionStatus.NoRoute,
                    "无可用路由");
                AssertNetworkAdapterStatus(
                    dashboard,
                    adapterStatus,
                    NetworkSelectionStatus.SplitRoute,
                    "IPv4 / IPv6 使用不同适配器");
                AssertNetworkAdapterStatus(
                    dashboard,
                    adapterStatus,
                    NetworkSelectionStatus.RouteLookupFailed,
                    "无法确认当前路由");
                AssertNetworkAdapterStatus(
                    dashboard,
                    adapterStatus,
                    NetworkSelectionStatus.RouteInterfaceMissing,
                    "路由接口暂不可用");
                AssertNetworkAdapterStatus(
                    dashboard,
                    adapterStatus,
                    NetworkSelectionStatus.CounterUnavailable,
                    "网络计数器不可用");
                AssertNetworkAdapterStatus(
                    dashboard,
                    adapterStatus,
                    NetworkSelectionStatus.Disposed,
                    "网络采样已停止");
            }
        }

        private static void TestDashboardGpuInformationHierarchy()
        {
            const string longDiscreteName =
                "NVIDIA GeForce RTX 5070 Ti Laptop GPU with an intentionally long model suffix";
            const string integratedName =
                "Intel(R) Graphics integrated adapter with an intentionally long model suffix";
            AppSettings settings = AppSettings.CreateDefault();
            MetricHistory history = new MetricHistory();

            using (DashboardForm dashboard = new DashboardForm(settings, history))
            {
                Label discreteValue = FindFirstControl<Label>(
                    dashboard,
                    delegate(Label control)
                    {
                        return control.AccessibleName == "独立显卡";
                    });
                Label discreteName = FindFirstControl<Label>(
                    dashboard,
                    delegate(Label control)
                    {
                        return control.AccessibleName == "独立显卡型号";
                    });
                Label integratedValue = FindFirstControl<Label>(
                    dashboard,
                    delegate(Label control)
                    {
                        return control.AccessibleName == "核显";
                    });
                Label integratedNameLabel = FindFirstControl<Label>(
                    dashboard,
                    delegate(Label control)
                    {
                        return control.AccessibleName == "核显型号";
                    });

                Assert(discreteValue != null, "discrete GPU core value label is missing");
                Assert(discreteName != null, "discrete GPU model label is missing");
                Assert(integratedValue != null, "integrated GPU core value label is missing");
                Assert(integratedNameLabel != null, "integrated GPU model label is missing");

                dashboard.UpdateSnapshot(new HardwareSnapshot
                {
                    GpuMode = GpuDisplayMode.Available,
                    DiscreteGpuDetected = true,
                    DiscreteGpuName = longDiscreteName,
                    GpuPercent = 91,
                    VideoMemoryPercent = 87,
                    TemperatureCelsius = 79,
                    IntegratedGpuDetected = true,
                    IntegratedGpuName = integratedName,
                    IntegratedGpuPercent = 42,
                    IntegratedSharedMemoryBytes = 61L * 1024L * 1024L + 512L * 1024L
                });

                Assert(
                    discreteValue.Text == "91% · 显存 87% · 79°C",
                    "discrete GPU core values were displaced by the adapter name");
                Assert(
                    integratedValue.Text == "42% · 共享 " +
                    61.5.ToString("0.0", CultureInfo.CurrentCulture) + " MB",
                    "integrated GPU core values were displaced by the adapter name");
                Assert(
                    !ContainsLineBreak(discreteValue.Text) &&
                    !ContainsLineBreak(integratedValue.Text),
                    "GPU core values are no longer single-line");
                Assert(
                    discreteName.Text == longDiscreteName &&
                    integratedNameLabel.Text == integratedName,
                    "GPU adapter names were not kept in their secondary labels");
                Assert(
                    discreteName.AutoEllipsis && integratedNameLabel.AutoEllipsis,
                    "GPU adapter names cannot be shortened independently");
                Assert(
                    !discreteValue.AutoEllipsis && !integratedValue.AutoEllipsis,
                    "GPU core values can still be shortened or hidden with ellipsis");
                Assert(
                    Math.Abs(discreteName.Font.SizeInPoints - 9.0f) < 0.01f &&
                    Math.Abs(integratedNameLabel.Font.SizeInPoints - 9.0f) < 0.01f,
                    "GPU adapter names no longer use the compact secondary font");
                Assert(
                    discreteValue.Font.SizeInPoints > discreteName.Font.SizeInPoints &&
                    integratedValue.Font.SizeInPoints > integratedNameLabel.Font.SizeInPoints,
                    "GPU adapter names are visually competing with the core values");

                LayoutDashboardAtMinimumSize(dashboard);
                Assert(
                    TextRenderer.MeasureText(discreteName.Text, discreteName.Font).Width >
                    discreteName.ClientSize.Width &&
                    TextRenderer.MeasureText(integratedNameLabel.Text, integratedNameLabel.Font).Width >
                    integratedNameLabel.ClientSize.Width,
                    "long GPU models no longer exercise both ellipsis paths");
                AssertGpuMetricFullyVisible(discreteValue, discreteName);
                AssertGpuMetricFullyVisible(integratedValue, integratedNameLabel);
                Assert(
                    discreteValue.Text == "91% · 显存 87% · 79°C" &&
                    integratedValue.Text == "42% · 共享 " +
                    61.5.ToString("0.0", CultureInfo.CurrentCulture) + " MB",
                    "GPU model truncation changed the core values");
            }
        }

        private static void AssertGpuMetricFullyVisible(Label metric, Label adapterName)
        {
            Assert(metric.Parent != null, "GPU core value label has no layout parent");
            Assert(
                metric.ClientSize.Width > 0 && metric.Height >= metric.Font.Height,
                "minimum dashboard size clips a GPU core value line");

            Size measured = TextRenderer.MeasureText(
                metric.Text,
                metric.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            Assert(
                measured.Width <= metric.ClientSize.Width &&
                measured.Height <= metric.ClientSize.Height,
                "minimum dashboard size cannot display a complete GPU core value");
            Assert(
                metric.Left >= 0 &&
                metric.Top >= 0 &&
                metric.Right <= metric.Parent.ClientSize.Width &&
                metric.Bottom <= metric.Parent.ClientSize.Height,
                "GPU core value label extends beyond its layout parent");
            Assert(
                !metric.Bounds.IntersectsWith(adapterName.Bounds),
                "GPU adapter name overlaps the core value line");
        }

        private static bool ContainsLineBreak(string text)
        {
            return text != null && (text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0);
        }

        private static void LayoutDashboardAtMinimumSize(DashboardForm dashboard)
        {
            dashboard.Size = dashboard.MinimumSize;
            Assert(dashboard.Handle != IntPtr.Zero, "dashboard handle was not created");
            TabControl tabs = FindFirstControl<TabControl>(dashboard, null);
            Assert(tabs != null, "dashboard tab control is missing during layout validation");
            Assert(tabs.Handle != IntPtr.Zero, "dashboard tab handle was not created");
            tabs.SelectedIndex = 0;
            Assert(
                tabs.SelectedTab != null && tabs.SelectedTab.Handle != IntPtr.Zero,
                "dashboard overview handle was not created");
            PerformLayoutTree(dashboard);
        }

        private static void PerformLayoutTree(Control root)
        {
            root.PerformLayout();
            foreach (Control child in root.Controls)
            {
                PerformLayoutTree(child);
            }
        }

        private static void AssertNetworkAdapterStatus(
            DashboardForm dashboard,
            Label adapterStatus,
            NetworkSelectionStatus status,
            string expectedText)
        {
            dashboard.UpdateSnapshot(new HardwareSnapshot
            {
                NetworkSelectionStatus = status
            });
            Assert(
                adapterStatus.Text == expectedText,
                "network adapter degradation text changed for " + status.ToString());
        }

        private static void AssertChartSeriesContrast(
            HistoryChartControl chart,
            string fieldName)
        {
            FieldInfo field = typeof(HistoryChartControl).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Pen pen = field == null ? null : field.GetValue(chart) as Pen;
            Assert(pen != null, "history chart series pen is missing: " + fieldName);
            Assert(
                GetContrastRatio(pen.Color, Color.White) >= 3.0,
                "history chart series contrast is below 3:1: " + fieldName);
        }

        private static double GetContrastRatio(Color first, Color second)
        {
            double firstLuminance = GetRelativeLuminance(first);
            double secondLuminance = GetRelativeLuminance(second);
            double lighter = Math.Max(firstLuminance, secondLuminance);
            double darker = Math.Min(firstLuminance, secondLuminance);
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static double GetRelativeLuminance(Color color)
        {
            return (0.2126 * GetLinearChannel(color.R)) +
                (0.7152 * GetLinearChannel(color.G)) +
                (0.0722 * GetLinearChannel(color.B));
        }

        private static double GetLinearChannel(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        private static CheckBox FindCheckBox(Control root, string text)
        {
            CheckBox checkBox = FindFirstControl<CheckBox>(
                root,
                delegate(CheckBox control)
                {
                    return string.Equals(control.Text, text, StringComparison.Ordinal);
                });
            Assert(checkBox != null, "dashboard checkbox is missing: " + text);
            return checkBox;
        }

        private static TControl FindFirstControl<TControl>(
            Control root,
            Predicate<TControl> predicate)
            where TControl : Control
        {
            if (root == null)
            {
                return null;
            }

            TControl typedRoot = root as TControl;
            if (typedRoot != null && (predicate == null || predicate(typedRoot)))
            {
                return typedRoot;
            }

            foreach (Control child in root.Controls)
            {
                TControl match = FindFirstControl(child, predicate);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static HistoryPoint CreatePoint(DateTime timestampUtc, int cpuPercent)
        {
            return new HistoryPoint(
                timestampUtc,
                cpuPercent,
                50,
                null,
                null,
                null,
                1024,
                512,
                null,
                null);
        }

        private static string CreateHistoryCsvLine(DateTime timestampUtc, int cpuPercent)
        {
            return string.Join(
                ",",
                new[]
                {
                    timestampUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    cpuPercent.ToString(CultureInfo.InvariantCulture),
                    "50",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "1024",
                    "512",
                    string.Empty,
                    string.Empty
                });
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception ex)
            {
                Failures.Add(name + " - " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void DeleteTemporaryRoot(string temporaryRoot)
        {
            string resolvedRoot = Path.GetFullPath(temporaryRoot);
            string resolvedBase = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!resolvedRoot.StartsWith(resolvedBase, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove test path outside TEMP.");
            }

            if (Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, true);
            }
        }
    }
}
