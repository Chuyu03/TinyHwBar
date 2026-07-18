using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
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
                Run("settings load failures protect source files", delegate { TestSettingsLoadProtection(temporaryRoot); });
                Run("history ring buffer", TestHistoryRingBuffer);
                Run("history persistence toggles preserve saved points", TestHistoryPersistenceToggles);
                Run("history enforces 24 hour in-memory limit", TestHistoryMaximumAge);
                Run("bounded persistent history", delegate { TestPersistentHistory(temporaryRoot); });
                Run("GPU history schema compatibility", delegate { TestHistoryGpuSchemaCompatibility(temporaryRoot); });
                Run("history save reports failure", delegate { TestHistorySaveFailure(temporaryRoot); });
                Run("history rejects oversized files", delegate { TestOversizedHistory(temporaryRoot); });
                Run("history load failures protect source files", delegate { TestHistoryLoadProtection(temporaryRoot); });
                Run("history backup recovery remains non-destructive", delegate { TestHistoryBackupRecovery(temporaryRoot); });
                Run("history protection warning preserves later save warnings", TestHistoryProtectionWarningGate);
                Run("history points retain sampler timestamps", TestHistorySnapshotTimestamp);
                Run("history chart uses real timestamps and gaps", TestHistoryChartTimeline);
                Run("gateway sampler remains passive while disabled", TestGatewayDisabledBoundary);
                Run("gateway probe interval survives target changes", TestGatewayGlobalProbeInterval);
                Run("gateway target local-only policy", TestGatewayTargetPolicy);
                Run("network route selection decisions", TestNetworkRouteSelection);
                Run("Windows sockaddr layout", TestSockaddrLayout);
                Run("GPU role adapter classification", TestGpuRoleClassification);
                Run("GPU role snapshot mapping", TestGpuRoleSnapshotMapping);
                Run("GPU automatic and manual selection decisions", TestGpuSelectionDecisions);
                Run("loopback GPU status schema", TestLoopbackGpuStatusSchema);
                Run("singleton mutex spans sessions per user", TestSingletonMutexName);
                Run("singleton mutex SID and ACL fail closed", TestSingletonMutexSecurity);
                Run("singleton mutex blocks legacy and current instances", TestSingletonMutexCompatibility);
                Run("display bounds include taskbar-reserved space", TestDisplayBoundsPositioning);
                Run("dashboard fits inside the working area", TestDashboardWorkingAreaConstraint);
                Run("dashboard defaults and accessibility structure", TestDashboardStructure);
                Run("telemetry text rendering avoids RGB fringes", TestTelemetryTextRendering);
                Run("dashboard preserves only dirty settings", TestDashboardDirtySettingsPreserved);
                Run("dashboard retains a missing manual GPU choice", TestDashboardMissingManualGpuFallback);
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
            Assert(
                settings.GpuSelectionMode == GpuSelectionMode.Automatic &&
                settings.SelectedGpuAdapterId.Length == 0,
                "v1 settings did not migrate to automatic GPU selection");
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
            settings.GpuSelectionMode = GpuSelectionMode.Manual;
            settings.SelectedGpuAdapterId = "00000001:00000002";
            settings.SelectedGpuAdapterName = "Test Discrete GPU";
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
            Assert(
                loaded.GpuSelectionMode == GpuSelectionMode.Manual &&
                loaded.SelectedGpuAdapterId == "00000001:00000002" &&
                loaded.SelectedGpuAdapterName == "Test Discrete GPU",
                "manual GPU selection did not round trip");
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

            string protectedPath = Path.Combine(
                temporaryRoot,
                "settings-replace-failure.ini");
            AppSettings first = AppSettings.CreateDefault();
            first.OpacityPercent = 80;
            Assert(SettingsStore.Save(first, protectedPath), "first backup fixture save failed");
            AppSettings second = AppSettings.CreateDefault();
            second.OpacityPercent = 70;
            Assert(SettingsStore.Save(second, protectedPath), "second backup fixture save failed");
            AppSettings third = AppSettings.CreateDefault();
            third.OpacityPercent = 60;
            Assert(
                SettingsStore.Save(third, protectedPath),
                "settings save could not rotate an existing backup");
            string protectedBackupPath = protectedPath + ".bak";
            string protectedBackupBytes = Convert.ToBase64String(
                File.ReadAllBytes(protectedBackupPath));
            File.SetAttributes(
                protectedPath,
                File.GetAttributes(protectedPath) | FileAttributes.ReadOnly);
            try
            {
                AppSettings replacement = AppSettings.CreateDefault();
                replacement.OpacityPercent = 50;
                Assert(
                    !SettingsStore.Save(replacement, protectedPath),
                    "read-only primary settings were reported as replaced");
                Assert(
                    Convert.ToBase64String(File.ReadAllBytes(protectedBackupPath)) ==
                        protectedBackupBytes,
                    "failed settings replacement destroyed or changed the existing backup");
            }
            finally
            {
                File.SetAttributes(
                    protectedPath,
                    File.GetAttributes(protectedPath) & ~FileAttributes.ReadOnly);
            }
        }

        private static void TestSettingsLoadProtection(string temporaryRoot)
        {
            string missingPath = Path.Combine(temporaryRoot, "settings-missing.ini");
            SettingsLoadResult missing = SettingsStore.LoadWithStatus(missingPath);
            Assert(missing.Status == SettingsLoadStatus.Missing, "missing settings status changed");
            Assert(missing.AllowsAutomaticSave, "missing settings should allow first save");
            Assert(
                missing.Settings.GpuSelectionMode == GpuSelectionMode.Automatic,
                "missing settings did not default to automatic GPU selection");
            AppSettings missingSession = missing.CreateSessionSettings();
            Assert(
                missingSession.PersistHistory &&
                missingSession.GatewayLatencyEnabled,
                "fresh-install defaults were incorrectly treated as recovered side effects");
            Assert(
                SettingsStore.SaveAfterLoad(missing.Settings, missing, missingPath),
                "first settings save after a missing file failed");
            Assert(File.Exists(missingPath), "first settings save did not create the file");

            string corruptPath = Path.Combine(temporaryRoot, "settings-corrupt.ini");
            const string corruptContents = "this is not a settings file";
            File.WriteAllText(corruptPath, corruptContents, Encoding.UTF8);
            SettingsLoadResult corrupt = SettingsStore.LoadWithStatus(corruptPath);
            Assert(corrupt.Status == SettingsLoadStatus.Corrupt, "corrupt settings status changed");
            Assert(!corrupt.AllowsAutomaticSave, "corrupt settings were not write-protected");
            Assert(
                !SettingsStore.SaveAfterLoad(corrupt.Settings, corrupt, corruptPath),
                "automatic save overwrote corrupt settings");
            Assert(
                File.ReadAllText(corruptPath, Encoding.UTF8) == corruptContents,
                "corrupt settings contents changed after a blocked save");

            string unsupportedPath = Path.Combine(temporaryRoot, "settings-unsupported.ini");
            const string unsupportedContents = "Version=99\r\n";
            File.WriteAllText(unsupportedPath, unsupportedContents, Encoding.UTF8);
            SettingsLoadResult unsupported = SettingsStore.LoadWithStatus(unsupportedPath);
            Assert(
                unsupported.Status == SettingsLoadStatus.Unsupported,
                "unsupported settings version was not distinguished from corruption");
            Assert(!unsupported.AllowsAutomaticSave, "unsupported settings were not write-protected");
            Assert(
                !SettingsStore.SaveAfterLoad(
                    unsupported.Settings,
                    unsupported,
                    unsupportedPath),
                "automatic save overwrote unsupported settings");
            Assert(
                File.ReadAllText(unsupportedPath, Encoding.UTF8) == unsupportedContents,
                "unsupported settings contents changed after a blocked save");

            string unknownKeyPath = Path.Combine(temporaryRoot, "settings-unknown-key.ini");
            Assert(
                SettingsStore.Save(AppSettings.CreateDefault(), unknownKeyPath),
                "unknown-key settings fixture save failed");
            File.AppendAllText(unknownKeyPath, "FutureSetting=keep-me\r\n", Encoding.UTF8);
            string unknownKeyContents = File.ReadAllText(unknownKeyPath, Encoding.UTF8);
            SettingsLoadResult unknownKey = SettingsStore.LoadWithStatus(unknownKeyPath);
            Assert(
                unknownKey.Status == SettingsLoadStatus.Unsupported,
                "unknown settings field was not protected as a future schema");
            Assert(!unknownKey.AllowsAutomaticSave, "unknown settings field was not write-protected");
            Assert(
                !SettingsStore.SaveAfterLoad(unknownKey.Settings, unknownKey, unknownKeyPath),
                "automatic save overwrote an unknown settings field");
            Assert(
                File.ReadAllText(unknownKeyPath, Encoding.UTF8) == unknownKeyContents,
                "unknown settings field changed after a blocked save");

            string invalidOptionalPath = Path.Combine(
                temporaryRoot,
                "settings-invalid-optional.ini");
            Assert(
                SettingsStore.Save(AppSettings.CreateDefault(), invalidOptionalPath),
                "invalid-optional settings fixture save failed");
            string invalidOptionalContents = File.ReadAllText(
                invalidOptionalPath,
                Encoding.UTF8).Replace(
                    "GpuSelectionMode=Automatic",
                    "GpuSelectionMode=Surprise");
            File.WriteAllText(invalidOptionalPath, invalidOptionalContents, Encoding.UTF8);
            SettingsLoadResult invalidOptional = SettingsStore.LoadWithStatus(
                invalidOptionalPath);
            Assert(
                invalidOptional.Status == SettingsLoadStatus.Corrupt,
                "invalid optional settings value was silently normalized");
            Assert(
                !invalidOptional.AllowsAutomaticSave,
                "invalid optional settings value was not write-protected");
            Assert(
                !SettingsStore.SaveAfterLoad(
                    invalidOptional.Settings,
                    invalidOptional,
                    invalidOptionalPath),
                "automatic save overwrote an invalid optional settings value");
            Assert(
                File.ReadAllText(invalidOptionalPath, Encoding.UTF8) ==
                    invalidOptionalContents,
                "invalid optional settings value changed after a blocked save");

            string ioFailurePath = Path.Combine(temporaryRoot, "settings-io-failure");
            Directory.CreateDirectory(ioFailurePath);
            SettingsLoadResult ioFailure = SettingsStore.LoadWithStatus(ioFailurePath);
            Assert(ioFailure.Status == SettingsLoadStatus.IoFailure, "settings I/O failure was not reported");
            Assert(!ioFailure.AllowsAutomaticSave, "settings I/O failure was not write-protected");

            string recoveryPath = Path.Combine(temporaryRoot, "settings-recovery.ini");
            AppSettings first = AppSettings.CreateDefault();
            first.Left = 11;
            Assert(SettingsStore.Save(first, recoveryPath), "first recovery settings save failed");
            AppSettings second = AppSettings.CreateDefault();
            second.Left = 22;
            Assert(SettingsStore.Save(second, recoveryPath), "second recovery settings save failed");
            Assert(File.Exists(recoveryPath + ".bak"), "settings backup was not retained");
            const string damagedPrimaryContents = "Version=2\r\nLeft=not-an-integer\r\n";
            File.WriteAllText(recoveryPath, damagedPrimaryContents, Encoding.UTF8);

            SettingsLoadResult recovered = SettingsStore.LoadWithStatus(recoveryPath);
            Assert(recovered.Status == SettingsLoadStatus.Corrupt, "damaged primary status changed");
            Assert(recovered.LoadedFromBackup, "valid settings backup was not loaded");
            Assert(
                recovered.BackupStatus == SettingsLoadStatus.Success,
                "settings backup success was not reported");
            Assert(recovered.Settings.Left == 11, "recovered settings did not come from the backup");
            Assert(!recovered.AllowsAutomaticSave, "backup recovery allowed destructive automatic save");
            Assert(
                !SettingsStore.SaveAfterLoad(recovered.Settings, recovered, recoveryPath),
                "automatic save replaced a damaged primary after backup recovery");
            Assert(
                File.ReadAllText(recoveryPath, Encoding.UTF8) == damagedPrimaryContents,
                "damaged primary changed after non-destructive backup recovery");

            recovered.Settings.PersistHistory = true;
            recovered.Settings.GatewayLatencyEnabled = true;
            recovered.Settings.AutomaticUpdateEnabled = true;
            recovered.Settings.LoopbackApiEnabled = true;
            recovered.Settings.TelemetryEnabled = true;
            recovered.Settings.GpuSelectionMode = GpuSelectionMode.Manual;
            recovered.Settings.SelectedGpuAdapterId = "luid:recovered";
            recovered.Settings.UpdateManifestUrl = "https://updates.example.test/manifest.json";
            recovered.Settings.TelemetryEndpoint = "https://telemetry.example.test/collect";
            AppSettings recoveredSession = recovered.CreateSessionSettings();
            Assert(
                !recoveredSession.PersistHistory &&
                !recoveredSession.GatewayLatencyEnabled &&
                !recoveredSession.AutomaticUpdateEnabled &&
                !recoveredSession.LoopbackApiEnabled &&
                !recoveredSession.TelemetryEnabled,
                "recovered settings silently re-enabled local or network side effects");
            Assert(
                recoveredSession.GpuSelectionMode == GpuSelectionMode.Manual &&
                recoveredSession.SelectedGpuAdapterId == "luid:recovered" &&
                recoveredSession.UpdateManifestUrl ==
                    "https://updates.example.test/manifest.json" &&
                recoveredSession.TelemetryEndpoint ==
                    "https://telemetry.example.test/collect",
                "fail-closed recovery discarded safe preferences or endpoint drafts");
            Assert(
                recovered.Settings.PersistHistory &&
                recovered.Settings.GatewayLatencyEnabled &&
                recovered.Settings.AutomaticUpdateEnabled &&
                recovered.Settings.LoopbackApiEnabled &&
                recovered.Settings.TelemetryEnabled,
                "session safety mutated the protected backup settings");

            string missingPrimaryPath = Path.Combine(
                temporaryRoot,
                "settings-missing-primary.ini");
            AppSettings backupOnlySettings = AppSettings.CreateDefault();
            backupOnlySettings.AutomaticUpdateEnabled = true;
            backupOnlySettings.UpdateManifestUrl =
                "https://updates.example.test/manifest.json";
            Assert(
                SettingsStore.Save(backupOnlySettings, missingPrimaryPath + ".bak"),
                "backup-only settings fixture save failed");
            SettingsLoadResult backupOnly = SettingsStore.LoadWithStatus(
                missingPrimaryPath);
            Assert(
                backupOnly.Status == SettingsLoadStatus.Missing &&
                backupOnly.LoadedFromBackup,
                "missing primary did not report backup recovery");
            Assert(
                !backupOnly.AllowsAutomaticSave &&
                backupOnly.NeedsUserAttention,
                "backup-only recovery bypassed explicit review");
            AppSettings backupOnlySession = backupOnly.CreateSessionSettings();
            Assert(
                !backupOnlySession.PersistHistory &&
                !backupOnlySession.GatewayLatencyEnabled &&
                !backupOnlySession.AutomaticUpdateEnabled,
                "backup-only recovery silently re-enabled side effects");
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

        private static void TestHistoryPersistenceToggles()
        {
            MetricHistory sessionHistory = new MetricHistory(10);
            MetricHistory persistentHistory = new MetricHistory(10);
            DateTime now = DateTime.UtcNow;

            MonitorForm.AddHistoryPoint(
                sessionHistory,
                persistentHistory,
                CreatePoint(now.AddSeconds(-5), 10),
                true);
            MonitorForm.AddHistoryPoint(
                sessionHistory,
                persistentHistory,
                CreatePoint(now.AddSeconds(-4), 20),
                false);
            MonitorForm.AddHistoryPoint(
                sessionHistory,
                persistentHistory,
                CreatePoint(now.AddSeconds(-3), 30),
                true);
            MonitorForm.AddHistoryPoint(
                sessionHistory,
                persistentHistory,
                CreatePoint(now.AddSeconds(-2), 40),
                false);
            MonitorForm.AddHistoryPoint(
                sessionHistory,
                persistentHistory,
                CreatePoint(now.AddSeconds(-1), 50),
                true);

            HistoryPoint[] sessionPoints = sessionHistory.Snapshot(now);
            HistoryPoint[] persistentPoints = persistentHistory.Snapshot(now);
            Assert(sessionPoints.Length == 5, "disabled persistence stopped the live history curve");
            Assert(persistentPoints.Length == 3, "disabled intervals leaked into persistent history");
            Assert(
                persistentPoints[0].CpuPercent == 10 &&
                persistentPoints[1].CpuPercent == 30 &&
                persistentPoints[2].CpuPercent == 50,
                "persistence toggles discarded old points or retained disabled-interval points");
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

            string protectedPath = Path.Combine(
                temporaryRoot,
                "history-replace-failure.csv");
            HistoryStore protectedStore = new HistoryStore(protectedPath);
            DateTime nowUtc = DateTime.UtcNow;
            Assert(
                protectedStore.Save(new[] { CreatePoint(nowUtc.AddSeconds(-3), 10) }),
                "first history backup fixture save failed");
            Assert(
                protectedStore.Save(new[] { CreatePoint(nowUtc.AddSeconds(-2), 20) }),
                "second history backup fixture save failed");
            Assert(
                protectedStore.Save(new[] { CreatePoint(nowUtc.AddSeconds(-1), 30) }),
                "history save could not rotate an existing backup");
            string protectedBackupPath = protectedPath + ".bak";
            string protectedBackupBytes = Convert.ToBase64String(
                File.ReadAllBytes(protectedBackupPath));
            File.SetAttributes(
                protectedPath,
                File.GetAttributes(protectedPath) | FileAttributes.ReadOnly);
            try
            {
                HistorySaveFailure replacementFailure;
                Assert(
                    !protectedStore.Save(
                        new[] { CreatePoint(nowUtc, 40) },
                        out replacementFailure),
                    "read-only primary history was reported as replaced");
                Assert(
                    replacementFailure != null &&
                    replacementFailure.Stage ==
                        HistorySaveFailureStage.ReplacePrimaryFile,
                    "read-only history failure reported the wrong stage");
                Assert(
                    Convert.ToBase64String(File.ReadAllBytes(protectedBackupPath)) ==
                        protectedBackupBytes,
                    "failed history replacement destroyed or changed the existing backup");
            }
            finally
            {
                File.SetAttributes(
                    protectedPath,
                    File.GetAttributes(protectedPath) & ~FileAttributes.ReadOnly);
            }
        }

        private static void TestOversizedHistory(string temporaryRoot)
        {
            string path = Path.Combine(temporaryRoot, "history-oversized.csv");
            File.WriteAllText(path, new string('x', (2 * 1024 * 1024) + 1), Encoding.ASCII);
            HistoryStore store = new HistoryStore(path);
            Assert(store.Load().Length == 0, "oversized history file was parsed");
            store.Clear();
        }

        private static void TestHistoryLoadProtection(string temporaryRoot)
        {
            DateTime nowUtc = DateTime.UtcNow;
            HistoryPoint[] replacement = new[] { CreatePoint(nowUtc, 77) };

            string missingPath = Path.Combine(temporaryRoot, "history-missing.csv");
            HistoryStore missingStore = new HistoryStore(missingPath);
            HistoryLoadResult missing = missingStore.LoadWithStatus();
            Assert(
                missing.Status == HistoryLoadStatus.Missing &&
                !missing.LoadedFromBackup &&
                missing.BackupStatus == HistoryLoadStatus.Missing &&
                missing.AllowsAutomaticSave &&
                !missing.NeedsUserAttention,
                "missing history did not allow first-run creation");
            Assert(missingStore.Save(replacement), "missing history could not be created");
            Assert(
                new HistoryStore(missingPath).LoadWithStatus().Status ==
                HistoryLoadStatus.Success,
                "newly created history did not load successfully");

            string corruptPath = Path.Combine(temporaryRoot, "history-corrupt.csv");
            const string corruptText = "not-a-history-file\r\n";
            File.WriteAllText(corruptPath, corruptText, new UTF8Encoding(false));
            HistoryStore corruptStore = new HistoryStore(corruptPath);
            HistoryLoadResult corrupt = corruptStore.LoadWithStatus();
            Assert(
                corrupt.Status == HistoryLoadStatus.Corrupt &&
                !corrupt.AllowsAutomaticSave &&
                corrupt.NeedsUserAttention,
                "corrupt history was treated as an empty first run");
            HistorySaveFailure corruptFailure;
            Assert(
                !corruptStore.Save(replacement, out corruptFailure) &&
                corruptFailure != null &&
                corruptFailure.Stage == HistorySaveFailureStage.ProtectExistingFile,
                "corrupt history did not block automatic replacement");
            Assert(
                File.ReadAllText(corruptPath, Encoding.UTF8) == corruptText,
                "corrupt history bytes were changed by a blocked save");

            string clearRecoveryPath = Path.Combine(
                temporaryRoot,
                "history-clear-recovery.csv");
            File.WriteAllText(
                clearRecoveryPath,
                corruptText,
                new UTF8Encoding(false));
            HistoryStore clearRecoveryStore = new HistoryStore(clearRecoveryPath);
            Assert(
                clearRecoveryStore.LoadWithStatus().NeedsUserAttention,
                "clear recovery fixture did not enable load protection");
            Assert(
                clearRecoveryStore.Clear(),
                "a fully deletable history set was not cleared");
            Assert(
                !File.Exists(clearRecoveryPath) &&
                clearRecoveryStore.Save(replacement),
                "successful history clear did not re-enable persistence");

            string unsupportedPath = Path.Combine(temporaryRoot, "history-future.csv");
            const string unsupportedText = "TinyHwBarHistory=99\r\n";
            File.WriteAllText(
                unsupportedPath,
                unsupportedText,
                new UTF8Encoding(false));
            HistoryStore unsupportedStore = new HistoryStore(unsupportedPath);
            HistoryLoadResult unsupported = unsupportedStore.LoadWithStatus();
            Assert(
                unsupported.Status == HistoryLoadStatus.Unsupported &&
                !unsupported.AllowsAutomaticSave,
                "future history schema was accepted for automatic replacement");
            HistorySaveFailure unsupportedFailure;
            Assert(
                !unsupportedStore.Save(replacement, out unsupportedFailure) &&
                unsupportedFailure.Stage == HistorySaveFailureStage.ProtectExistingFile,
                "future history schema was not write-protected");
            Assert(
                File.ReadAllText(unsupportedPath, Encoding.UTF8) == unsupportedText,
                "future history schema was changed by a blocked save");

            string ioFailurePath = Path.Combine(temporaryRoot, "history-is-directory");
            Directory.CreateDirectory(ioFailurePath);
            HistoryStore ioFailureStore = new HistoryStore(ioFailurePath);
            HistoryLoadResult ioFailure = ioFailureStore.LoadWithStatus();
            Assert(
                ioFailure.Status == HistoryLoadStatus.IoFailure &&
                !ioFailure.AllowsAutomaticSave,
                "history I/O failure was treated as missing data");
            HistorySaveFailure ioSaveFailure;
            Assert(
                !ioFailureStore.Save(replacement, out ioSaveFailure) &&
                ioSaveFailure.Stage == HistorySaveFailureStage.ProtectExistingFile,
                "history I/O failure did not enable write protection");

            Assert(
                !ioFailureStore.Clear(),
                "a directory at the history path was reported as cleared");
            Assert(
                Directory.Exists(ioFailurePath),
                "history clear removed an unexpected directory");
            HistorySaveFailure ioSaveAfterClearFailure;
            Assert(
                !ioFailureStore.Save(replacement, out ioSaveAfterClearFailure) &&
                ioSaveAfterClearFailure.Stage ==
                    HistorySaveFailureStage.ProtectExistingFile,
                "failed history clear disabled load-failure protection");

            string readOnlyPath = Path.Combine(
                temporaryRoot,
                "history-read-only.csv");
            File.WriteAllText(
                readOnlyPath,
                corruptText,
                new UTF8Encoding(false));
            File.SetAttributes(
                readOnlyPath,
                File.GetAttributes(readOnlyPath) | FileAttributes.ReadOnly);
            try
            {
                HistoryStore readOnlyStore = new HistoryStore(readOnlyPath);
                HistoryLoadResult readOnly = readOnlyStore.LoadWithStatus();
                Assert(
                    readOnly.Status == HistoryLoadStatus.Corrupt &&
                    !readOnly.AllowsAutomaticSave,
                    "read-only corrupt history was not protected");
                Assert(
                    !readOnlyStore.Clear(),
                    "an undeletable history file was reported as cleared");
                Assert(
                    File.Exists(readOnlyPath),
                    "failed history clear removed the protected source file");
                HistorySaveFailure readOnlySaveFailure;
                Assert(
                    !readOnlyStore.Save(replacement, out readOnlySaveFailure) &&
                    readOnlySaveFailure.Stage ==
                        HistorySaveFailureStage.ProtectExistingFile,
                    "failed read-only clear disabled load-failure protection");
            }
            finally
            {
                if (File.Exists(readOnlyPath))
                {
                    File.SetAttributes(readOnlyPath, FileAttributes.Normal);
                }
            }

            string partialClearPath = Path.Combine(
                temporaryRoot,
                "history-partial-clear.csv");
            HistoryStore partialClearStore = new HistoryStore(partialClearPath);
            Assert(
                partialClearStore.Save(
                    new[] { CreatePoint(DateTime.UtcNow.AddSeconds(-2), 41) }),
                "partial-clear primary fixture save failed");
            Assert(
                partialClearStore.Save(
                    new[] { CreatePoint(DateTime.UtcNow.AddSeconds(-1), 42) }),
                "partial-clear backup fixture save failed");
            HistoryLoadResult partialClearLoad = partialClearStore.LoadWithStatus();
            Assert(
                partialClearLoad.Status == HistoryLoadStatus.Success &&
                partialClearLoad.AllowsAutomaticSave,
                "partial-clear fixture did not start in a writable state");
            string partialClearBackupPath = partialClearPath + ".bak";
            string partialClearBackupBytes = Convert.ToBase64String(
                File.ReadAllBytes(partialClearBackupPath));
            File.SetAttributes(
                partialClearBackupPath,
                File.GetAttributes(partialClearBackupPath) | FileAttributes.ReadOnly);
            try
            {
                Assert(
                    !partialClearStore.Clear(),
                    "partial history deletion was reported as a complete clear");
                Assert(
                    !File.Exists(partialClearPath) &&
                    File.Exists(partialClearBackupPath),
                    "partial-clear fixture did not isolate the backup deletion failure");
                HistorySaveFailure partialClearFailure;
                Assert(
                    !partialClearStore.Save(
                        new[] { CreatePoint(DateTime.UtcNow, 43) },
                        out partialClearFailure) &&
                    partialClearFailure.Stage ==
                        HistorySaveFailureStage.ProtectExistingFile,
                    "partial clear allowed persistence to recreate deleted history");
                Assert(
                    !File.Exists(partialClearPath) &&
                    Convert.ToBase64String(
                        File.ReadAllBytes(partialClearBackupPath)) ==
                        partialClearBackupBytes,
                    "save after a partial clear changed the remaining history state");
            }
            finally
            {
                if (File.Exists(partialClearBackupPath))
                {
                    File.SetAttributes(
                        partialClearBackupPath,
                        FileAttributes.Normal);
                }
            }
        }

        private static void TestHistoryBackupRecovery(string temporaryRoot)
        {
            string path = Path.Combine(temporaryRoot, "history-backup.csv");
            DateTime nowUtc = DateTime.UtcNow;
            HistoryPoint[] first = new[] { CreatePoint(nowUtc.AddSeconds(-2), 11) };
            HistoryPoint[] second = new[] { CreatePoint(nowUtc.AddSeconds(-1), 22) };
            HistoryStore store = new HistoryStore(path);
            Assert(store.Save(first), "initial history save failed");
            Assert(store.Save(second), "history replacement failed");
            Assert(File.Exists(path + ".bak"), "normal replacement did not retain a backup");

            const string corruptPrimary = "broken-primary\r\n";
            File.WriteAllText(path, corruptPrimary, new UTF8Encoding(false));
            HistoryStore recoveringStore = new HistoryStore(path);
            HistoryLoadResult recovered = recoveringStore.LoadWithStatus();
            Assert(
                recovered.Status == HistoryLoadStatus.Corrupt &&
                recovered.LoadedFromBackup &&
                recovered.BackupStatus == HistoryLoadStatus.Success,
                "valid history backup was not recovered into memory");
            Assert(
                recovered.Points.Length == 1 &&
                recovered.Points[0].CpuPercent == 11,
                "recovered history did not use the previous valid generation");
            Assert(
                !recovered.AllowsAutomaticSave,
                "backup recovery unexpectedly allowed overwriting the corrupt primary");

            HistorySaveFailure failure;
            Assert(
                !recoveringStore.Save(second, out failure) &&
                failure.Stage == HistorySaveFailureStage.ProtectExistingFile,
                "backup recovery did not preserve the damaged primary for inspection");
            Assert(
                File.ReadAllText(path, Encoding.UTF8) == corruptPrimary,
                "backup recovery silently replaced the damaged primary");

            string backupOnlyPath = Path.Combine(
                temporaryRoot,
                "history-backup-only.csv");
            HistoryStore backupOnlyFixtureStore = new HistoryStore(backupOnlyPath);
            Assert(
                backupOnlyFixtureStore.Save(first),
                "backup-only primary fixture save failed");
            Assert(
                backupOnlyFixtureStore.Save(second),
                "backup-only backup fixture save failed");
            string backupOnlyBackupPath = backupOnlyPath + ".bak";
            string backupOnlyBytes = Convert.ToBase64String(
                File.ReadAllBytes(backupOnlyBackupPath));
            File.Delete(backupOnlyPath);

            HistoryStore backupOnlyStore = new HistoryStore(backupOnlyPath);
            HistoryLoadResult backupOnly = backupOnlyStore.LoadWithStatus();
            Assert(
                backupOnly.Status == HistoryLoadStatus.Missing &&
                backupOnly.LoadedFromBackup &&
                backupOnly.BackupStatus == HistoryLoadStatus.Success &&
                !backupOnly.AllowsAutomaticSave &&
                backupOnly.NeedsUserAttention,
                "backup-only history did not retain write protection");
            Assert(
                backupOnly.Points.Length == 1 &&
                backupOnly.Points[0].CpuPercent == 11,
                "backup-only history did not recover the previous generation");

            HistorySaveFailure backupOnlyFailure;
            Assert(
                !backupOnlyStore.Save(second, out backupOnlyFailure) &&
                backupOnlyFailure != null &&
                backupOnlyFailure.Stage ==
                    HistorySaveFailureStage.ProtectExistingFile,
                "backup-only recovery allowed automatic primary recreation");
            Assert(
                !File.Exists(backupOnlyPath),
                "backup-only recovery recreated the missing primary");
            Assert(
                Convert.ToBase64String(
                    File.ReadAllBytes(backupOnlyBackupPath)) ==
                    backupOnlyBytes,
                "backup-only recovery changed the backup bytes");
        }

        private static void TestHistoryProtectionWarningGate()
        {
            HistorySaveFailure protectedLoadFailure = new HistorySaveFailure(
                HistorySaveFailureStage.ProtectExistingFile,
                "ProtectedLoadState",
                null);
            HistorySaveFailure writeFailure = new HistorySaveFailure(
                HistorySaveFailureStage.WriteTemporaryFile,
                "IOException",
                null);

            Assert(
                !MonitorForm.ShouldWarnForHistorySaveFailure(null),
                "a missing history failure requested a warning");
            Assert(
                !MonitorForm.ShouldWarnForHistorySaveFailure(
                    protectedLoadFailure),
                "the load-protection state consumed the normal save-warning gate");
            Assert(
                MonitorForm.ShouldWarnForHistorySaveFailure(writeFailure),
                "a real history write failure no longer requests a warning");

            HistoryPersistenceWarningGate gate =
                new HistoryPersistenceWarningGate();
            int staleGeneration = gate.CaptureGeneration();
            Assert(
                gate.TrySchedule(staleGeneration),
                "the initial history save warning was not scheduled");

            gate.Reset();
            int currentGeneration = gate.CaptureGeneration();
            Assert(
                currentGeneration != staleGeneration,
                "history warning reset did not advance the generation");
            Assert(
                !gate.TryShow(staleGeneration),
                "a pre-clear history failure showed after a successful clear");
            Assert(
                !gate.TrySchedule(staleGeneration),
                "a pre-clear history failure reclaimed the warning gate");
            Assert(
                gate.TrySchedule(currentGeneration) &&
                gate.TryShow(currentGeneration),
                "a stale warning suppressed a later real save failure");
        }

        private static void TestHistorySnapshotTimestamp()
        {
            DateTime sampledAtUtc = new DateTime(
                2026,
                7,
                17,
                2,
                3,
                4,
                DateTimeKind.Utc);
            HardwareSnapshot snapshot = new HardwareSnapshot
            {
                SampledAtUtc = sampledAtUtc,
                CpuPercent = 10,
                MemoryPercent = 20
            };
            HistoryPoint point = HistoryPoint.FromSnapshot(snapshot);
            Assert(
                point.TimestampUtc == sampledAtUtc,
                "history timestamp drifted from the completed hardware sample");
        }

        private static void TestHistoryChartTimeline()
        {
            DateTime startUtc = new DateTime(
                2026,
                7,
                17,
                1,
                0,
                0,
                DateTimeKind.Utc);
            RectangleF bounds = new RectangleF(10.0f, 0.0f, 100.0f, 50.0f);
            Assert(
                Math.Abs(HistoryChartControl.MapTimestampToX(
                    startUtc,
                    startUtc,
                    startUtc.AddSeconds(100),
                    bounds) - 10.0f) < 0.01f,
                "history start time did not map to the left edge");
            Assert(
                Math.Abs(HistoryChartControl.MapTimestampToX(
                    startUtc.AddSeconds(25),
                    startUtc,
                    startUtc.AddSeconds(100),
                    bounds) - 35.0f) < 0.01f,
                "history timestamp was not mapped proportionally");
            Assert(
                Math.Abs(HistoryChartControl.MapTimestampToX(
                    startUtc,
                    startUtc,
                    startUtc,
                    bounds) - 60.0f) < 0.01f,
                "single history point was not centered");

            HistoryPoint first = CreatePoint(startUtc, 10);
            HistoryPoint second = CreatePoint(startUtc.AddSeconds(2), 20);
            HistoryPoint third = CreatePoint(startUtc.AddSeconds(4), 30);
            HistoryPoint afterGap = CreatePoint(startUtc.AddMinutes(5), 40);
            HistoryPoint[] timeline = new[] { first, second, third, afterGap };
            TimeSpan threshold = HistoryChartControl.CalculateConnectionGapThreshold(timeline);
            Assert(
                HistoryChartControl.ArePointsConnected(first, second, threshold),
                "normal history samples were split into separate segments");
            Assert(
                !HistoryChartControl.ArePointsConnected(third, afterGap, threshold),
                "long history gap was drawn as continuous data");

            using (HistoryChartControl chart = new HistoryChartControl())
            {
                chart.SetPoints(new[] { afterGap, second, first, third });
                FieldInfo pointsField = typeof(HistoryChartControl).GetField(
                    "points",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                HistoryPoint[] sorted = pointsField == null
                    ? null
                    : pointsField.GetValue(chart) as HistoryPoint[];
                Assert(sorted != null && sorted.Length == 4, "chart point snapshot is missing");
                Assert(
                    sorted[0].TimestampUtc == first.TimestampUtc &&
                    sorted[3].TimestampUtc == afterGap.TimestampUtc,
                    "chart did not normalize clock rollback or out-of-order points");
            }
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

        private static void TestGatewayGlobalProbeInterval()
        {
            int sendCount = 0;
            GatewayEchoSender sender = delegate(IPAddress target, int timeout)
            {
                sendCount++;
                return Task.FromResult(GatewayEchoResult.CreateAvailable(1));
            };
            GatewayTargetValidator validator = delegate(
                NetworkMetrics metrics,
                out IPAddress target)
            {
                target = metrics == null
                    ? null
                    : IPAddress.Parse(metrics.DefaultGatewayAddress);
                return target == null
                    ? GatewayLatencyStatus.GatewayMissing
                    : GatewayLatencyStatus.Available;
            };

            using (GatewayLatencySampler sampler = new GatewayLatencySampler(
                sender,
                TimeSpan.FromSeconds(10),
                750,
                validator))
            {
                sampler.SetEnabled(true);
                sampler.Sample(NetworkMetrics.CreateAvailable(
                    "test-interface",
                    "test",
                    "192.168.1.1",
                    null,
                    null,
                    null));
                Assert(sendCount == 1, "initial gateway probe was not sent");

                GatewayLatencyMetrics changedTarget = sampler.Sample(
                    NetworkMetrics.CreateAvailable(
                        "test-interface",
                        "test",
                        "192.168.1.2",
                        null,
                        null,
                        null));
                Assert(sendCount == 1, "target change bypassed the global probe interval");
                Assert(
                    changedTarget.GatewayAddress == "192.168.1.2" &&
                    changedTarget.Status == GatewayLatencyStatus.Waiting,
                    "target change did not retain the pending target state");

                sampler.SetEnabled(false);
                sampler.SetEnabled(true);
                sampler.Sample(NetworkMetrics.CreateAvailable(
                    "test-interface",
                    "test",
                    "192.168.1.2",
                    null,
                    null,
                    null));
                Assert(sendCount == 1, "enable toggle bypassed the global probe interval");
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

        private static void TestSingletonMutexName()
        {
            Assert(
                Program.BuildSingletonMutexName("S-1-5-21-100-200-300-1001") ==
                @"Global\TinyHwBar.Singleton.S-1-5-21-100-200-300-1001",
                "singleton mutex is not shared across sessions for the same user");
            Assert(
                Program.BuildSingletonMutexName(string.Empty) ==
                @"Local\TinyHwBar.Singleton",
                "empty SID did not use the compatibility fallback");
            Assert(
                Program.BuildSingletonMutexName(@"Global\Injected") ==
                @"Local\TinyHwBar.Singleton",
                "mutex namespace injection was accepted");
        }

        private static void TestSingletonMutexSecurity()
        {
            const string validSidText = "S-1-5-21-100-200-300-1001";
            Assert(
                Program.BuildRequiredSingletonMutexName(validSidText) ==
                    @"Global\TinyHwBar.Singleton." + validSidText,
                "the required singleton name did not use a canonical SID");
            AssertRequiredSingletonNameRejected(string.Empty);
            AssertRequiredSingletonNameRejected("not-a-sid");
            AssertRequiredSingletonNameRejected(@"Global\Injected");

            SecurityIdentifier currentUserSid;
            string currentMutexName = Program.GetSingletonMutexName(
                out currentUserSid);
            Assert(
                currentUserSid != null &&
                currentMutexName == Program.BuildRequiredSingletonMutexName(
                    currentUserSid.Value) &&
                currentMutexName.StartsWith(
                    @"Global\TinyHwBar.Singleton.",
                    StringComparison.Ordinal),
                "the production singleton path did not require the current user SID");

            MutexSecurity trustedSecurity =
                Program.CreateTrustedGlobalMutexSecurity(currentUserSid);
            Assert(
                Program.IsTrustedGlobalMutexSecurity(
                    trustedSecurity,
                    currentUserSid),
                "the intended Global mutex ACL did not pass validation");

            SecurityIdentifier worldSid = new SecurityIdentifier(
                WellKnownSidType.WorldSid,
                null);
            MutexSecurity broadSecurity =
                Program.CreateTrustedGlobalMutexSecurity(currentUserSid);
            broadSecurity.AddAccessRule(new MutexAccessRule(
                worldSid,
                MutexRights.Synchronize,
                AccessControlType.Allow));
            Assert(
                !Program.IsTrustedGlobalMutexSecurity(
                    broadSecurity,
                    currentUserSid),
                "an extra World ACE was accepted for the Global mutex");

            SecurityIdentifier localSystemSid = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            SecurityIdentifier wrongOwner = string.Equals(
                currentUserSid.Value,
                localSystemSid.Value,
                StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null)
                : localSystemSid;
            MutexSecurity wrongOwnerSecurity =
                Program.CreateTrustedGlobalMutexSecurity(currentUserSid);
            wrongOwnerSecurity.SetOwner(wrongOwner);
            Assert(
                !Program.IsTrustedGlobalMutexSecurity(
                    wrongOwnerSecurity,
                    currentUserSid),
                "a Global mutex owned by another SID was accepted");

            string suffix = Guid.NewGuid().ToString("N");
            string globalName =
                @"Global\TinyHwBar.Tests.Singleton.Security." + suffix;
            string legacyName =
                @"Local\TinyHwBar.Tests.Singleton.Security.Legacy." + suffix;
            bool missingSidRejected = false;
            try
            {
                Program.TryAcquireSingletonMutexes(
                    globalName,
                    legacyName,
                    null);
            }
            catch (SingletonMutexSecurityException)
            {
                missingSidRejected = true;
            }

            Assert(
                missingSidRejected,
                "a Global singleton mutex was created without an owner SID");

            IDisposable lease = Program.TryAcquireSingletonMutexes(
                globalName,
                legacyName,
                currentUserSid);
            Assert(lease != null, "the secured Global singleton mutex was not created");
            try
            {
                using (Mutex inspectionHandle = Mutex.OpenExisting(
                    globalName,
                    MutexRights.ReadPermissions))
                {
                    Assert(
                        Program.IsTrustedGlobalMutexSecurity(
                            inspectionHandle.GetAccessControl(),
                            currentUserSid),
                        "the live Global mutex owner or ACL changed after creation");
                }

                Assert(
                    !CanAcquireSingletonMutexesOnWorker(
                        globalName,
                        legacyName,
                        currentUserSid),
                    "a pre-existing trusted Global mutex was reacquired");
            }
            finally
            {
                lease.Dispose();
            }

            string preExistingGlobalName =
                @"Global\TinyHwBar.Tests.Singleton.PreExisting." + suffix;
            bool preExistingCreatedNew;
            using (Mutex preExistingMutex = new Mutex(
                false,
                preExistingGlobalName,
                out preExistingCreatedNew,
                trustedSecurity))
            {
                Assert(
                    preExistingCreatedNew,
                    "the trusted pre-existing Global mutex fixture already existed");
                IDisposable preExistingLease = Program.TryAcquireSingletonMutexes(
                    preExistingGlobalName,
                    legacyName,
                    currentUserSid);
                try
                {
                    Assert(
                        preExistingLease == null,
                        "an unowned pre-existing Global mutex was reacquired");
                }
                finally
                {
                    if (preExistingLease != null)
                    {
                        preExistingLease.Dispose();
                    }
                }
            }

            string untrustedGlobalName =
                @"Global\TinyHwBar.Tests.Singleton.Untrusted." + suffix;
            bool untrustedCreatedNew;
            using (Mutex untrustedMutex = new Mutex(
                false,
                untrustedGlobalName,
                out untrustedCreatedNew,
                broadSecurity))
            {
                Assert(
                    untrustedCreatedNew,
                    "the untrusted Global mutex fixture already existed");
                bool untrustedRejected = false;
                IDisposable unexpectedLease = null;
                try
                {
                    unexpectedLease = Program.TryAcquireSingletonMutexes(
                        untrustedGlobalName,
                        legacyName,
                        currentUserSid);
                }
                catch (SingletonMutexSecurityException)
                {
                    untrustedRejected = true;
                }
                finally
                {
                    if (unexpectedLease != null)
                    {
                        unexpectedLease.Dispose();
                    }
                }

                Assert(
                    untrustedRejected,
                    "a pre-created Global mutex with an extra ACE was trusted");
            }
        }

        private static void AssertRequiredSingletonNameRejected(string userSid)
        {
            bool rejected = false;
            try
            {
                Program.BuildRequiredSingletonMutexName(userSid);
            }
            catch (SingletonIdentityUnavailableException)
            {
                rejected = true;
            }

            Assert(rejected, "an invalid SID reached the production mutex path");
        }

        private static void TestSingletonMutexCompatibility()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string primaryName =
                @"Local\TinyHwBar.Tests.Singleton.Primary." + suffix;
            string legacyName =
                @"Local\TinyHwBar.Tests.Singleton.Legacy." + suffix;

            IDisposable lease = Program.TryAcquireSingletonMutexes(
                primaryName,
                legacyName);
            Assert(lease != null, "the singleton mutex pair could not be acquired");
            try
            {
                Assert(
                    !CanAcquireSingletonMutexesOnWorker(
                        primaryName,
                        legacyName),
                    "a second current instance acquired the singleton mutex pair");
                Assert(
                    !CanStartLegacyInstanceOnWorker(legacyName),
                    "a legacy instance ignored the current instance compatibility mutex");
            }
            finally
            {
                lease.Dispose();
            }

            Assert(
                CanAcquireSingletonMutexesOnWorker(primaryName, legacyName),
                "disposing the singleton lease did not release both mutexes");

            using (Mutex legacyOwner = new Mutex(false, legacyName))
            {
                bool legacyOwned = TryOwnMutex(legacyOwner);
                try
                {
                    Assert(legacyOwned, "the legacy test mutex could not be acquired");
                    Assert(
                        !CanAcquireSingletonMutexesOnWorker(
                            primaryName,
                            legacyName),
                        "a current instance ignored a running legacy instance");

                    using (Mutex primaryProbe = new Mutex(false, primaryName))
                    {
                        bool primaryOwned = TryOwnMutex(primaryProbe);
                        try
                        {
                            Assert(
                                primaryOwned,
                                "a rejected legacy-instance launch leaked the primary mutex");
                        }
                        finally
                        {
                            if (primaryOwned)
                            {
                                primaryProbe.ReleaseMutex();
                            }
                        }
                    }
                }
                finally
                {
                    if (legacyOwned)
                    {
                        legacyOwner.ReleaseMutex();
                    }
                }
            }

            using (Mutex primaryOwner = new Mutex(false, primaryName))
            {
                bool primaryOwned = TryOwnMutex(primaryOwner);
                try
                {
                    Assert(primaryOwned, "the primary test mutex could not be acquired");
                    Assert(
                        !CanAcquireSingletonMutexesOnWorker(
                            primaryName,
                            legacyName),
                        "a current instance ignored the primary singleton mutex");

                    using (Mutex legacyProbe = new Mutex(false, legacyName))
                    {
                        bool legacyOwned = TryOwnMutex(legacyProbe);
                        try
                        {
                            Assert(
                                legacyOwned,
                                "a rejected current-instance launch leaked the legacy mutex");
                        }
                        finally
                        {
                            if (legacyOwned)
                            {
                                legacyProbe.ReleaseMutex();
                            }
                        }
                    }
                }
                finally
                {
                    if (primaryOwned)
                    {
                        primaryOwner.ReleaseMutex();
                    }
                }
            }

            string collisionPrimaryName =
                @"Local\TinyHwBar.Tests.Singleton.Collision.Primary." + suffix;
            string collisionLegacyName =
                @"Local\TinyHwBar.Tests.Singleton.Collision.Legacy." + suffix;
            bool eventCreatedNew;
            using (EventWaitHandle collision = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                collisionLegacyName,
                out eventCreatedNew))
            {
                Assert(
                    eventCreatedNew,
                    "the legacy mutex type-collision fixture already existed");
                bool collisionRejected = false;
                IDisposable unexpectedLease = null;
                try
                {
                    unexpectedLease = Program.TryAcquireSingletonMutexes(
                        collisionPrimaryName,
                        collisionLegacyName);
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    collisionRejected = true;
                }
                finally
                {
                    if (unexpectedLease != null)
                    {
                        unexpectedLease.Dispose();
                    }
                }

                Assert(
                    collisionRejected,
                    "a non-mutex legacy named object was not rejected fail-closed");
                using (Mutex primaryProbe = new Mutex(
                    false,
                    collisionPrimaryName))
                {
                    bool primaryOwned = TryOwnMutex(primaryProbe);
                    try
                    {
                        Assert(
                            primaryOwned,
                            "a legacy mutex type collision leaked the primary mutex");
                    }
                    finally
                    {
                        if (primaryOwned)
                        {
                            primaryProbe.ReleaseMutex();
                        }
                    }
                }
            }

            string fallbackName =
                @"Local\TinyHwBar.Tests.Singleton.Fallback." + suffix;
            IDisposable fallbackLease = Program.TryAcquireSingletonMutexes(
                fallbackName,
                fallbackName);
            Assert(
                fallbackLease != null,
                "the duplicate-name Local fallback could not be acquired");
            try
            {
                Assert(
                    !CanAcquireSingletonMutexesOnWorker(
                        fallbackName,
                        fallbackName),
                    "the Local fallback did not enforce a singleton");
            }
            finally
            {
                fallbackLease.Dispose();
            }
        }

        private static bool CanAcquireSingletonMutexesOnWorker(
            string primaryName,
            string legacyName)
        {
            return RunOnDedicatedWorker(
                delegate
                {
                    using (IDisposable lease = Program.TryAcquireSingletonMutexes(
                        primaryName,
                        legacyName))
                    {
                        return lease != null;
                    }
                });
        }

        private static bool CanAcquireSingletonMutexesOnWorker(
            string primaryName,
            string legacyName,
            SecurityIdentifier currentUserSid)
        {
            return RunOnDedicatedWorker(
                delegate
                {
                    using (IDisposable lease = Program.TryAcquireSingletonMutexes(
                        primaryName,
                        legacyName,
                        currentUserSid))
                    {
                        return lease != null;
                    }
                });
        }

        private static bool CanStartLegacyInstanceOnWorker(string legacyName)
        {
            return RunOnDedicatedWorker(
                delegate
                {
                    bool createdNew;
                    using (Mutex mutex = new Mutex(
                        true,
                        legacyName,
                        out createdNew))
                    {
                        if (createdNew)
                        {
                            mutex.ReleaseMutex();
                        }

                        return createdNew;
                    }
                });
        }

        private static bool RunOnDedicatedWorker(Func<bool> action)
        {
            bool result = false;
            Exception workerFailure = null;
            Thread worker = new Thread(
                new ThreadStart(delegate
                {
                    try
                    {
                        result = action();
                    }
                    catch (Exception exception)
                    {
                        workerFailure = exception;
                    }
                }));
            worker.IsBackground = true;
            worker.Start();
            if (!worker.Join(5000))
            {
                throw new InvalidOperationException(
                    "The dedicated singleton mutex test worker did not finish.");
            }

            if (workerFailure != null)
            {
                throw new InvalidOperationException(
                    "The dedicated singleton mutex test worker failed.",
                    workerFailure);
            }

            return result;
        }

        private static bool TryOwnMutex(Mutex mutex)
        {
            try
            {
                return mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
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
            Assert(
                GpuRoleSampler.ResolveAutomaticSelectionStatus(
                    GpuAdapterRole.Discrete,
                    new[]
                    {
                        IntelAdapterIntegrationKind.Discrete,
                        IntelAdapterIntegrationKind.Discrete
                    },
                    2) == IntelGpuDataStatus.AmbiguousAdapter,
                "automatic selection guessed between multiple discrete adapters");
            Assert(
                GpuRoleSampler.ResolveAutomaticSelectionStatus(
                    GpuAdapterRole.Discrete,
                    new[]
                    {
                        IntelAdapterIntegrationKind.Discrete,
                        IntelAdapterIntegrationKind.Unknown
                    },
                    1) == IntelGpuDataStatus.AmbiguousAdapter,
                "automatic selection ignored an unknown adapter beside a known role");
            Assert(
                GpuRoleSampler.ResolveAutomaticSelectionStatus(
                    GpuAdapterRole.Integrated,
                    new[]
                    {
                        IntelAdapterIntegrationKind.Integrated,
                        IntelAdapterIntegrationKind.Discrete
                    },
                    1) == IntelGpuDataStatus.Available,
                "automatic selection rejected a unique classified integrated adapter");

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
                GpuRoleSampler.IsRootEnumeratedDisplayDevicePath(
                    @"\\?\ROOT#DISPLAY#0000#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}"),
                "root-enumerated display adapter was not recognized");
            Assert(
                GpuRoleSampler.IsRootEnumeratedDisplayDevicePath(
                    @"\\?\root#display#0000#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}"),
                "root-enumerated display path matching became case-sensitive");
            Assert(
                !GpuRoleSampler.IsRootEnumeratedDisplayDevicePath(
                    @"\\?\PCI#VEN_8086&DEV_7D67#TEST"),
                "physical PCI adapter was treated as a root display adapter");
            Assert(
                !GpuRoleSampler.IsRootEnumeratedDisplayDevicePath(
                    @"\\?\ROOT#SYSTEM#0000#{TEST}"),
                "unrelated root device was treated as a display adapter");
            Assert(
                !GpuRoleSampler.IsRootEnumeratedDisplayDevicePath(null) &&
                !GpuRoleSampler.IsRootEnumeratedDisplayDevicePath(string.Empty) &&
                !GpuRoleSampler.IsRootEnumeratedDisplayDevicePath("   "),
                "missing device paths were treated as root display adapters");
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

        private static void TestGpuSelectionDecisions()
        {
            HardwareSnapshot snapshot = new HardwareSnapshot
            {
                GpuMode = GpuDisplayMode.Available,
                DiscreteGpuDetected = true,
                DiscreteGpuId = "00000001:00000002",
                DiscreteGpuName = "Discrete GPU",
                GpuPercent = 72,
                IntegratedGpuDetected = true,
                IntegratedGpuId = "00000003:00000004",
                IntegratedGpuName = "Integrated GPU",
                IntegratedGpuPercent = 18
            };

            HardwareSampler.ResolveGpuSelection(
                snapshot,
                GpuSelectionMode.Automatic,
                string.Empty,
                string.Empty);
            Assert(
                snapshot.GpuSelectionStatus == GpuSelectionStatus.Automatic,
                "automatic GPU selection status changed");
            Assert(!snapshot.BarUsesIntegratedGpu, "automatic selection did not prefer an available dGPU");
            Assert(
                snapshot.EffectiveGpuAdapterId == snapshot.DiscreteGpuId,
                "automatic dGPU identity was not exposed");

            snapshot.GpuMode = GpuDisplayMode.Eco;
            snapshot.GpuPercent = null;
            HardwareSampler.ResolveGpuSelection(
                snapshot,
                GpuSelectionMode.Automatic,
                string.Empty,
                string.Empty);
            Assert(snapshot.BarUsesIntegratedGpu, "automatic selection did not fall back to the active iGPU");
            Assert(
                snapshot.EffectiveGpuAdapterId == snapshot.IntegratedGpuId,
                "automatic iGPU fallback identity was not exposed");
            Assert(
                snapshot.ToDisplayText().IndexOf("iGPU 18%", StringComparison.Ordinal) >= 0,
                "main bar text did not identify the automatic iGPU fallback");

            HardwareSnapshot integratedFirstSample = new HardwareSnapshot
            {
                GpuMode = GpuDisplayMode.Unavailable,
                DiscreteGpuDetected = false,
                IntegratedGpuDetected = true,
                IntegratedGpuId = "00000005:00000006",
                IntegratedGpuName = "Integrated First Sample GPU",
                IntegratedGpuPercent = null,
                IntegratedSharedMemoryBytes = 2L * 1024L * 1024L * 1024L,
                IntegratedSharedMemoryLimitBytes = 8L * 1024L * 1024L * 1024L,
                IntegratedUtilizationStatus = IntelGpuDataStatus.FirstSamplePending,
                IntegratedMemoryStatus = IntelGpuDataStatus.Available
            };
            HardwareSampler.ResolveGpuSelection(
                integratedFirstSample,
                GpuSelectionMode.Automatic,
                string.Empty,
                string.Empty);
            Assert(
                integratedFirstSample.BarUsesIntegratedGpu &&
                integratedFirstSample.EffectiveGpuAdapterId ==
                    integratedFirstSample.IntegratedGpuId,
                "iGPU-only first sample was not selected without utilization data");
            string integratedFirstSampleText = integratedFirstSample.ToDisplayText();
            Assert(
                integratedFirstSampleText.IndexOf(
                    "iGPU --",
                    StringComparison.Ordinal) >= 0 &&
                integratedFirstSampleText.IndexOf(
                    "VR 25%",
                    StringComparison.Ordinal) >= 0,
                "iGPU-only first sample did not retain its displayable memory data");

            snapshot.GpuMode = GpuDisplayMode.Available;
            snapshot.GpuPercent = 72;
            snapshot.DiscreteGpuSelectionMatched = false;
            snapshot.IntegratedGpuSelectionMatched = true;
            HardwareSampler.ResolveGpuSelection(
                snapshot,
                GpuSelectionMode.Manual,
                snapshot.IntegratedGpuId,
                snapshot.IntegratedGpuName);
            Assert(
                snapshot.GpuSelectionStatus == GpuSelectionStatus.ManualSelected,
                "matched manual GPU was not selected");
            Assert(snapshot.BarUsesIntegratedGpu, "matched manual iGPU was ignored");

            snapshot.DiscreteGpuSelectionMatched = false;
            snapshot.IntegratedGpuSelectionMatched = false;
            HardwareSampler.ResolveGpuSelection(
                snapshot,
                GpuSelectionMode.Manual,
                "missing-adapter",
                "Removed GPU");
            Assert(
                snapshot.GpuSelectionStatus == GpuSelectionStatus.ManualUnavailableFallback,
                "missing manual GPU did not report an automatic fallback");
            Assert(!snapshot.BarUsesIntegratedGpu, "manual fallback did not use normal automatic selection");

            GpuAdapterChoice integrated = new GpuAdapterChoice(
                "i",
                "Integrated GPU",
                GpuRoleSampler.IntelVendorId,
                GpuAdapterRole.Integrated);
            GpuAdapterChoice discrete = new GpuAdapterChoice(
                "d",
                "Discrete GPU",
                GpuRoleSampler.NvidiaVendorId,
                GpuAdapterRole.Discrete);
            Assert(
                GpuRoleSampler.CompareAdapterChoices(discrete, integrated) < 0,
                "GPU choices no longer put the discrete adapter first");
            Assert(
                discrete.ToString().IndexOf("独立显卡", StringComparison.Ordinal) >= 0 &&
                integrated.ToString().IndexOf("核显", StringComparison.Ordinal) >= 0,
                "GPU choices do not show their roles");
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

                RadioButton automaticGpu = FindFirstControl<RadioButton>(
                    dashboard,
                    delegate(RadioButton control)
                    {
                        return control.AccessibleName == "GPU 自动选择（推荐）";
                    });
                RadioButton manualGpu = FindFirstControl<RadioButton>(
                    dashboard,
                    delegate(RadioButton control)
                    {
                        return control.AccessibleName == "GPU 手动选择";
                    });
                ComboBox gpuAdapter = FindFirstControl<ComboBox>(
                    dashboard,
                    delegate(ComboBox control)
                    {
                        return control.AccessibleName == "手动选择 GPU 设备";
                    });
                Button refreshGpu = FindFirstControl<Button>(
                    dashboard,
                    delegate(Button control)
                    {
                        return control.AccessibleName == "刷新 GPU 设备列表";
                    });
                Label gpuSelectionStatus = FindFirstControl<Label>(
                    dashboard,
                    delegate(Label control)
                    {
                        return control.AccessibleName == "GPU 选择状态";
                    });
                Assert(
                    automaticGpu != null && automaticGpu.Checked &&
                    automaticGpu.TabIndex == 0,
                    "recommended automatic GPU selection is not the dashboard default");
                Assert(
                    manualGpu != null && !manualGpu.Checked &&
                    manualGpu.TabIndex == 1,
                    "manual GPU selection is enabled by default or missing");
                Assert(
                    gpuAdapter != null &&
                    gpuAdapter.DropDownStyle == ComboBoxStyle.DropDownList &&
                    !gpuAdapter.Enabled &&
                    gpuAdapter.TabIndex == 2,
                    "manual GPU list is not present and disabled in automatic mode");
                Assert(
                    refreshGpu != null && refreshGpu.Enabled &&
                    refreshGpu.TabIndex == 3,
                    "GPU device refresh action is missing");
                Assert(
                    gpuSelectionStatus != null &&
                    gpuSelectionStatus.Text.IndexOf(
                        "自动模式",
                        StringComparison.Ordinal) >= 0,
                    "automatic GPU selection status is not explained");

                dashboard.UpdateSnapshot(new HardwareSnapshot
                {
                    GpuSelectionMode = GpuSelectionMode.Automatic,
                    GpuSelectionStatus = GpuSelectionStatus.Automatic,
                    EffectiveGpuAdapterName = "Automatic Test GPU"
                });
                Assert(
                    gpuSelectionStatus.Text == "当前自动使用：Automatic Test GPU",
                    "automatic GPU effective-device status was not updated");
                manualGpu.Checked = true;
                Assert(
                    gpuAdapter.Enabled && !automaticGpu.Checked,
                    "manual mode did not enable the GPU device list");

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
                CheckBox telemetryToggle = FindCheckBox(
                    dashboard,
                    "允许逐次确认的遥测/诊断摘要发送");
                Assert(
                    !telemetryToggle.Checked,
                    "telemetry is enabled in the default dashboard");
                Assert(
                    telemetryToggle is GrayscaleTextCheckBox &&
                    telemetryToggle.UseCompatibleTextRendering &&
                    telemetryToggle.AutoSize &&
                    telemetryToggle.MinimumSize == telemetryToggle.MaximumSize &&
                    telemetryToggle.PreferredSize == telemetryToggle.MinimumSize,
                    "telemetry checkbox rendering changed its original layout contract");
                GrayscaleTextLabel telemetryStatus =
                    FindFirstControl<GrayscaleTextLabel>(dashboard, null);
                Assert(
                    telemetryStatus != null &&
                    telemetryStatus.UseCompatibleTextRendering &&
                    telemetryStatus.AutoSize &&
                    telemetryStatus.MinimumSize.Height ==
                        telemetryStatus.MaximumSize.Height &&
                    telemetryStatus.PreferredSize.Height ==
                        telemetryStatus.MinimumSize.Height,
                    "telemetry status rendering changed its original row height");
                using (CheckBox baselineTelemetryToggle = new CheckBox())
                {
                    baselineTelemetryToggle.UseCompatibleTextRendering = false;
                    baselineTelemetryToggle.Font = telemetryToggle.Font;
                    baselineTelemetryToggle.Text = telemetryToggle.Text;
                    baselineTelemetryToggle.AutoSize = true;
                    Assert(
                        telemetryToggle.Font.Name == "Segoe UI" &&
                        telemetryToggle.PreferredSize ==
                            baselineTelemetryToggle.GetPreferredSize(Size.Empty),
                        "dashboard telemetry checkbox was sized before inheriting its UI font");
                }

                using (Label baselineTelemetryStatus = new Label())
                {
                    baselineTelemetryStatus.UseCompatibleTextRendering = false;
                    baselineTelemetryStatus.Font = telemetryStatus.Font;
                    baselineTelemetryStatus.Text = telemetryStatus.Text;
                    baselineTelemetryStatus.AutoSize = true;
                    Assert(
                        telemetryStatus.Font.Name == "Segoe UI" &&
                        telemetryStatus.PreferredSize.Height ==
                            baselineTelemetryStatus.GetPreferredSize(Size.Empty).Height,
                        "dashboard telemetry status was sized before inheriting its UI font");
                }
                int telemetryStatusHeight = telemetryStatus.PreferredSize.Height;
                dashboard.SetTelemetryStatus(
                    "可发送状态已启用；每次发送仍需预览并明确确认。");
                Assert(
                    telemetryStatus.Text ==
                        "可发送状态已启用；每次发送仍需预览并明确确认。" &&
                    telemetryStatus.PreferredSize.Height == telemetryStatusHeight,
                    "dynamic telemetry status changed the preserved single-line row height");

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

        private static void TestTelemetryTextRendering()
        {
            const string checkBoxText =
                "允许逐次确认的遥测/诊断摘要发送";
            const string statusText = "未发送任何遥测或诊断数据";

            using (Font font = new Font(
                "Segoe UI",
                9.0f,
                FontStyle.Regular,
                GraphicsUnit.Point))
            using (CheckBox baselineCheckBox = new CheckBox())
            using (GrayscaleTextCheckBox grayscaleCheckBox =
                new GrayscaleTextCheckBox())
            {
                baselineCheckBox.UseCompatibleTextRendering = false;
                baselineCheckBox.Font = font;
                baselineCheckBox.Text = checkBoxText;
                baselineCheckBox.AutoSize = true;

                grayscaleCheckBox.Font = font;
                grayscaleCheckBox.Text = checkBoxText;
                grayscaleCheckBox.AutoSize = true;
                Size originalPreferredSize =
                    baselineCheckBox.GetPreferredSize(Size.Empty);
                grayscaleCheckBox.EnableGrayscaleTextRendering();

                Assert(
                    grayscaleCheckBox.AutoSize &&
                    grayscaleCheckBox.PreferredSize == originalPreferredSize &&
                    grayscaleCheckBox.Size == originalPreferredSize,
                    "grayscale checkbox changed the original GDI preferred size");

                int checkedChanges = 0;
                grayscaleCheckBox.CheckedChanged += delegate
                {
                    checkedChanges++;
                };
                grayscaleCheckBox.Checked = true;
                grayscaleCheckBox.Checked = false;
                Assert(
                    checkedChanges == 2,
                    "grayscale checkbox changed the normal checked-state behavior");

                grayscaleCheckBox.BackColor = Color.White;
                grayscaleCheckBox.ForeColor = Color.Black;
                using (Bitmap bitmap = new Bitmap(
                    grayscaleCheckBox.Width,
                    grayscaleCheckBox.Height))
                {
                    grayscaleCheckBox.DrawToBitmap(
                        bitmap,
                        new Rectangle(Point.Empty, bitmap.Size));
                    Rectangle inkBounds = GetInkBounds(
                        bitmap,
                        new Rectangle(
                            20,
                            0,
                            Math.Max(0, bitmap.Width - 20),
                            bitmap.Height));
                    Assert(
                        CountChromaticPixels(
                            bitmap,
                            new Rectangle(
                                20,
                                0,
                                Math.Max(0, bitmap.Width - 20),
                                bitmap.Height)) == 0,
                        "grayscale checkbox text still contains RGB subpixel fringes");
                    Assert(
                        !inkBounds.IsEmpty &&
                        inkBounds.Top > 0 &&
                        inkBounds.Right < bitmap.Width &&
                        inkBounds.Bottom < bitmap.Height,
                        "grayscale checkbox text is missing or clipped");
                }
            }

            using (Font font = new Font(
                "Segoe UI",
                9.0f,
                FontStyle.Regular,
                GraphicsUnit.Point))
            using (Label baselineLabel = new Label())
            using (GrayscaleTextLabel unconstrainedLabel =
                new GrayscaleTextLabel())
            using (GrayscaleTextLabel grayscaleLabel = new GrayscaleTextLabel())
            {
                baselineLabel.UseCompatibleTextRendering = false;
                baselineLabel.Font = font;
                baselineLabel.Text = statusText;
                baselineLabel.AutoSize = true;

                grayscaleLabel.Font = font;
                grayscaleLabel.Text = statusText;
                grayscaleLabel.AutoSize = true;
                unconstrainedLabel.Font = font;
                unconstrainedLabel.Text = statusText;
                unconstrainedLabel.AutoSize = true;
                unconstrainedLabel.UseCompatibleTextRendering = true;
                Size originalPreferredSize =
                    baselineLabel.GetPreferredSize(Size.Empty);
                Size compatiblePreferredSize =
                    unconstrainedLabel.GetPreferredSize(Size.Empty);
                int originalPreferredHeight = originalPreferredSize.Height;
                grayscaleLabel.EnableGrayscaleTextRendering();

                Assert(
                    grayscaleLabel.AutoSize &&
                    grayscaleLabel.PreferredSize.Height == originalPreferredHeight,
                    "grayscale status label changed the original GDI row height");

                grayscaleLabel.BackColor = Color.White;
                grayscaleLabel.ForeColor = Color.Black;
                int comparisonWidth = Math.Max(
                    originalPreferredSize.Width,
                    compatiblePreferredSize.Width);
                grayscaleLabel.Size = new Size(
                    comparisonWidth,
                    originalPreferredHeight);
                unconstrainedLabel.BackColor = Color.White;
                unconstrainedLabel.ForeColor = Color.Black;
                unconstrainedLabel.Size = new Size(
                    comparisonWidth,
                    compatiblePreferredSize.Height);
                using (Bitmap bitmap = new Bitmap(
                    grayscaleLabel.Width,
                    grayscaleLabel.Height))
                using (Bitmap unconstrainedBitmap = new Bitmap(
                    unconstrainedLabel.Width,
                    unconstrainedLabel.Height))
                {
                    grayscaleLabel.DrawToBitmap(
                        bitmap,
                        new Rectangle(Point.Empty, bitmap.Size));
                    unconstrainedLabel.DrawToBitmap(
                        unconstrainedBitmap,
                        new Rectangle(Point.Empty, unconstrainedBitmap.Size));
                    Rectangle inkBounds = GetInkBounds(
                        bitmap,
                        new Rectangle(Point.Empty, bitmap.Size));
                    Rectangle unconstrainedInkBounds = GetInkBounds(
                        unconstrainedBitmap,
                        new Rectangle(Point.Empty, unconstrainedBitmap.Size));
                    Assert(
                        CountChromaticPixels(
                            bitmap,
                            new Rectangle(Point.Empty, bitmap.Size)) == 0,
                        "grayscale status text still contains RGB subpixel fringes");
                    Assert(
                        !inkBounds.IsEmpty &&
                        !unconstrainedInkBounds.IsEmpty &&
                        inkBounds.Top == unconstrainedInkBounds.Top &&
                        inkBounds.Bottom == unconstrainedInkBounds.Bottom,
                        "grayscale status text is missing or vertically clipped at the preserved row height; " +
                        "GDI preferred=" + originalPreferredSize +
                        ", GDI+ preferred=" + compatiblePreferredSize +
                        ", constrained ink=" + inkBounds +
                        ", unconstrained ink=" + unconstrainedInkBounds);
                }

                grayscaleLabel.Text =
                    "可发送状态已启用；每次发送仍需预览并明确确认。";
                Assert(
                    grayscaleLabel.PreferredSize.Height == originalPreferredHeight,
                    "long dynamic telemetry status changed the preserved row height");
                using (Bitmap longStatusBitmap = new Bitmap(
                    grayscaleLabel.Width,
                    grayscaleLabel.Height))
                {
                    grayscaleLabel.DrawToBitmap(
                        longStatusBitmap,
                        new Rectangle(Point.Empty, longStatusBitmap.Size));
                    Assert(
                        !GetInkBounds(
                            longStatusBitmap,
                            new Rectangle(
                                Point.Empty,
                                longStatusBitmap.Size)).IsEmpty,
                        "long dynamic telemetry status was not rendered");
                }
            }
        }

        private static int CountChromaticPixels(Bitmap bitmap, Rectangle bounds)
        {
            Rectangle clipped = Rectangle.Intersect(
                new Rectangle(Point.Empty, bitmap.Size),
                bounds);
            int count = 0;
            for (int y = clipped.Top; y < clipped.Bottom; y++)
            {
                for (int x = clipped.Left; x < clipped.Right; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    int maximum = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                    int minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                    if (maximum - minimum > 6)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static Rectangle GetInkBounds(Bitmap bitmap, Rectangle bounds)
        {
            Rectangle clipped = Rectangle.Intersect(
                new Rectangle(Point.Empty, bitmap.Size),
                bounds);
            int left = clipped.Right;
            int top = clipped.Bottom;
            int right = clipped.Left - 1;
            int bottom = clipped.Top - 1;
            for (int y = clipped.Top; y < clipped.Bottom; y++)
            {
                for (int x = clipped.Left; x < clipped.Right; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R < 245 || pixel.G < 245 || pixel.B < 245)
                    {
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            return right < left || bottom < top
                ? Rectangle.Empty
                : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }

        private static void TestDashboardWorkingAreaConstraint()
        {
            Rectangle workingArea = new Rectangle(0, 0, 1920, 1040);
            Rectangle oversized = DashboardForm.ConstrainToWorkingArea(
                new Rectangle(-200, -100, 2200, 1300),
                workingArea,
                12);
            Assert(oversized.Left == 12 && oversized.Top == 12, "oversized dashboard was not repositioned");
            Assert(
                oversized.Right == workingArea.Right - 12 &&
                oversized.Bottom == workingArea.Bottom - 12,
                "oversized dashboard was not capped to the working area");

            Rectangle shiftedWorkArea = new Rectangle(1920, -200, 1280, 1000);
            Rectangle offscreen = DashboardForm.ConstrainToWorkingArea(
                new Rectangle(0, -500, 760, 560),
                shiftedWorkArea,
                12);
            Assert(
                shiftedWorkArea.Contains(offscreen),
                "dashboard was not moved into a shifted monitor working area");
        }

        private static void TestDashboardDirtySettingsPreserved()
        {
            AppSettings initial = AppSettings.CreateDefault();
            using (DashboardForm dashboard = new DashboardForm(initial, new MetricHistory()))
            {
                CheckBox locked = FindCheckBox(dashboard, "锁定监控条位置");
                CheckBox history = FindCheckBox(dashboard, "保存最近 900 个指标点到本机");
                CheckBox startup = FindCheckBox(dashboard, "登录 Windows 后启动 TinyHwBar");
                ComboBox opacity = FindFirstControl<ComboBox>(
                    dashboard,
                    delegate(ComboBox control)
                    {
                        return control.AccessibleName == "监控条透明度";
                    });
                TextBox updateEndpoint = FindFirstControl<TextBox>(
                    dashboard,
                    delegate(TextBox control)
                    {
                        return control.AccessibleName == "更新清单 HTTPS 地址";
                    });
                Assert(opacity != null, "opacity editor is missing");
                Assert(updateEndpoint != null, "update endpoint editor is missing");

                locked.Checked = true;
                startup.Checked = true;
                updateEndpoint.Text = "https://draft.example.invalid/manifest.ini";

                AppSettings runtime = AppSettings.CreateDefault();
                runtime.Locked = false;
                runtime.OpacityPercent = 60;
                runtime.PersistHistory = false;
                runtime.StartupEnabled = false;
                runtime.UpdateManifestUrl = "https://saved.example.invalid/manifest.ini";
                dashboard.SyncSettings(runtime);

                Assert(locked.Checked, "runtime sync overwrote a dirty display edit");
                Assert(
                    opacity.SelectedItem != null && opacity.SelectedItem.ToString() == "60%",
                    "clean opacity field did not follow runtime state");
                Assert(startup.Checked, "runtime sync overwrote a dirty startup edit");
                Assert(
                    updateEndpoint.Text == "https://draft.example.invalid/manifest.ini",
                    "runtime sync overwrote a dirty endpoint edit");
                Assert(!history.Checked, "runtime sync did not update a clean history field");

                dashboard.SetStartupState(false, "test status");
                Assert(startup.Checked, "startup status refresh overwrote a dirty startup edit");

                dashboard.RefreshSettings(runtime);
                Assert(!locked.Checked, "explicit refresh did not accept the saved display value");
                Assert(!startup.Checked, "explicit refresh did not accept the saved startup value");
                Assert(
                    updateEndpoint.Text == "https://saved.example.invalid/manifest.ini",
                    "explicit refresh did not accept the saved endpoint");

                AppSettings nextRuntime = runtime.Clone();
                nextRuntime.Locked = true;
                nextRuntime.PersistHistory = true;
                nextRuntime.StartupEnabled = true;
                nextRuntime.UpdateManifestUrl = "https://next.example.invalid/manifest.ini";
                dashboard.SyncSettings(nextRuntime);
                Assert(locked.Checked, "clean display field did not follow runtime state");
                Assert(history.Checked, "clean history field did not follow runtime state");
                Assert(startup.Checked, "clean startup field did not follow runtime state");
                Assert(
                    updateEndpoint.Text == "https://next.example.invalid/manifest.ini",
                    "clean endpoint field did not follow runtime state");
            }
        }

        private static void TestDashboardMissingManualGpuFallback()
        {
            AppSettings settings = AppSettings.CreateDefault();
            settings.GpuSelectionMode = GpuSelectionMode.Manual;
            settings.SelectedGpuAdapterId = "FFFFFFFF:FFFFFFFF";
            settings.SelectedGpuAdapterName = "TinyHwBar Missing Test GPU";

            using (DashboardForm dashboard = new DashboardForm(settings, new MetricHistory()))
            {
                RadioButton manualGpu = FindFirstControl<RadioButton>(
                    dashboard,
                    delegate(RadioButton control)
                    {
                        return control.AccessibleName == "GPU 手动选择";
                    });
                ComboBox gpuAdapter = FindFirstControl<ComboBox>(
                    dashboard,
                    delegate(ComboBox control)
                    {
                        return control.AccessibleName == "手动选择 GPU 设备";
                    });
                Button refreshGpu = FindFirstControl<Button>(
                    dashboard,
                    delegate(Button control)
                    {
                        return control.AccessibleName == "刷新 GPU 设备列表";
                    });
                Label status = FindFirstControl<Label>(
                    dashboard,
                    delegate(Label control)
                    {
                        return control.AccessibleName == "GPU 选择状态";
                    });

                Assert(manualGpu != null && manualGpu.Checked, "saved manual GPU mode was not restored");
                Assert(
                    gpuAdapter != null && gpuAdapter.Enabled &&
                    gpuAdapter.SelectedItem != null &&
                    gpuAdapter.SelectedItem.ToString().IndexOf(
                        "当前不可用",
                        StringComparison.Ordinal) >= 0,
                    "missing manual GPU was not retained as an unavailable choice");
                Assert(
                    status != null &&
                    status.Text.IndexOf("临时回退自动选择", StringComparison.Ordinal) >= 0,
                    "missing manual GPU fallback is not explained");

                dashboard.UpdateSnapshot(new HardwareSnapshot
                {
                    GpuSelectionMode = GpuSelectionMode.Manual,
                    GpuSelectionStatus = GpuSelectionStatus.ManualUnavailableFallback,
                    RequestedGpuAdapterId = settings.SelectedGpuAdapterId,
                    RequestedGpuAdapterName = settings.SelectedGpuAdapterName,
                    EffectiveGpuAdapterName = "Fallback GPU"
                });
                Assert(
                    status.Text.IndexOf("临时回退自动选择", StringComparison.Ordinal) >= 0,
                    "runtime manual GPU fallback status was lost");

                Assert(refreshGpu != null, "GPU refresh button is missing");
                refreshGpu.PerformClick();
                Assert(
                    gpuAdapter.SelectedItem != null &&
                    gpuAdapter.SelectedItem.ToString().IndexOf(
                        "当前不可用",
                        StringComparison.Ordinal) >= 0,
                    "refresh discarded the unavailable saved GPU identity");
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
