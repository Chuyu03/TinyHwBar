using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;

namespace TinyHwBar
{
    internal enum GpuSelectionMode
    {
        Automatic,
        Manual
    }

    internal enum SettingsLoadStatus
    {
        Missing = 0,
        Success = 1,
        Unsupported = 2,
        Corrupt = 3,
        IoFailure = 4
    }

    internal sealed class SettingsLoadResult
    {
        internal SettingsLoadResult(
            SettingsLoadStatus status,
            AppSettings settings,
            bool loadedFromBackup,
            SettingsLoadStatus? backupStatus)
        {
            Status = status;
            Settings = settings ?? AppSettings.CreateDefault();
            LoadedFromBackup = loadedFromBackup;
            BackupStatus = backupStatus;
        }

        internal SettingsLoadStatus Status { get; private set; }

        internal AppSettings Settings { get; private set; }

        internal bool LoadedFromBackup { get; private set; }

        internal SettingsLoadStatus? BackupStatus { get; private set; }

        internal bool AllowsAutomaticSave
        {
            get
            {
                if (Status == SettingsLoadStatus.Success)
                {
                    return true;
                }

                if (Status != SettingsLoadStatus.Missing || !BackupStatus.HasValue)
                {
                    return false;
                }

                return BackupStatus.Value == SettingsLoadStatus.Missing;
            }
        }

        internal bool RequiresExplicitSideEffectEnable
        {
            get { return !AllowsAutomaticSave; }
        }

        internal bool NeedsUserAttention
        {
            get { return !AllowsAutomaticSave; }
        }

        internal AppSettings CreateSessionSettings()
        {
            AppSettings sessionSettings = Settings.Clone();
            if (RequiresExplicitSideEffectEnable)
            {
                sessionSettings.PersistHistory = false;
                sessionSettings.GatewayLatencyEnabled = false;
                sessionSettings.AutomaticUpdateEnabled = false;
                sessionSettings.LoopbackApiEnabled = false;
                sessionSettings.TelemetryEnabled = false;
            }

            return sessionSettings;
        }

        internal string ToSafeDisplayText()
        {
            string primaryText = FormatStatus(Status);
            if (!BackupStatus.HasValue)
            {
                return "主设置文件：" + primaryText;
            }

            string recoveryText = LoadedFromBackup
                ? "；已从备份加载到本次运行，原文件未被覆盖"
                : "；未能从备份恢复";
            return "主设置文件：" + primaryText +
                "；备份文件：" + FormatStatus(BackupStatus.Value) +
                recoveryText;
        }

        private static string FormatStatus(SettingsLoadStatus status)
        {
            switch (status)
            {
                case SettingsLoadStatus.Missing:
                    return "不存在";
                case SettingsLoadStatus.Success:
                    return "可用";
                case SettingsLoadStatus.Unsupported:
                    return "版本不受支持";
                case SettingsLoadStatus.Corrupt:
                    return "内容损坏";
                case SettingsLoadStatus.IoFailure:
                    return "读取失败";
                default:
                    return "状态未知";
            }
        }
    }

    internal sealed class AppSettings
    {
        internal const int DefaultOpacityPercent = 90;
        internal static readonly int[] SupportedOpacityPercentages =
        {
            50,
            60,
            70,
            80,
            90,
            100
        };

        internal bool HasSavedPosition { get; set; }
        internal int Left { get; set; }
        internal int Top { get; set; }
        internal bool Locked { get; set; }
        internal bool ClickThrough { get; set; }
        internal int OpacityPercent { get; set; }
        internal bool PersistHistory { get; set; }
        internal bool GatewayLatencyEnabled { get; set; }
        internal GpuSelectionMode GpuSelectionMode { get; set; }
        internal string SelectedGpuAdapterId { get; set; }
        internal string SelectedGpuAdapterName { get; set; }
        internal bool StartupEnabled { get; set; }
        internal bool AutomaticUpdateEnabled { get; set; }
        internal string UpdateManifestUrl { get; set; }
        internal bool LoopbackApiEnabled { get; set; }
        internal bool TelemetryEnabled { get; set; }
        internal string TelemetryEndpoint { get; set; }

        internal static AppSettings CreateDefault()
        {
            return new AppSettings
            {
                HasSavedPosition = false,
                Left = 0,
                Top = 0,
                Locked = false,
                ClickThrough = false,
                OpacityPercent = DefaultOpacityPercent,
                PersistHistory = true,
                GatewayLatencyEnabled = true,
                GpuSelectionMode = GpuSelectionMode.Automatic,
                SelectedGpuAdapterId = string.Empty,
                SelectedGpuAdapterName = string.Empty,
                StartupEnabled = false,
                AutomaticUpdateEnabled = false,
                UpdateManifestUrl = string.Empty,
                LoopbackApiEnabled = false,
                TelemetryEnabled = false,
                TelemetryEndpoint = string.Empty
            };
        }

        internal static bool IsSupportedOpacityPercent(int value)
        {
            return Array.IndexOf(SupportedOpacityPercentages, value) >= 0;
        }

        internal AppSettings Clone()
        {
            return new AppSettings
            {
                HasSavedPosition = HasSavedPosition,
                Left = Left,
                Top = Top,
                Locked = Locked,
                ClickThrough = ClickThrough,
                OpacityPercent = OpacityPercent,
                PersistHistory = PersistHistory,
                GatewayLatencyEnabled = GatewayLatencyEnabled,
                GpuSelectionMode = GpuSelectionMode,
                SelectedGpuAdapterId = SelectedGpuAdapterId ?? string.Empty,
                SelectedGpuAdapterName = SelectedGpuAdapterName ?? string.Empty,
                StartupEnabled = StartupEnabled,
                AutomaticUpdateEnabled = AutomaticUpdateEnabled,
                UpdateManifestUrl = UpdateManifestUrl ?? string.Empty,
                LoopbackApiEnabled = LoopbackApiEnabled,
                TelemetryEnabled = TelemetryEnabled,
                TelemetryEndpoint = TelemetryEndpoint ?? string.Empty
            };
        }
    }

    internal static class SettingsStore
    {
        private const long MaximumSettingsFileBytes = 64L * 1024L;

        private static readonly HashSet<string> KnownSettingKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Version",
                "Left",
                "Top",
                "Locked",
                "ClickThrough",
                "OpacityPercent",
                "PersistHistory",
                "GatewayLatencyEnabled",
                "GpuSelectionMode",
                "SelectedGpuAdapterId",
                "SelectedGpuAdapterName",
                "StartupEnabled",
                "AutomaticUpdateEnabled",
                "UpdateManifestUrl",
                "LoopbackApiEnabled",
                "TelemetryEnabled",
                "TelemetryEndpoint"
            };

        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TinyHwBar");

        private static readonly string SettingsPath = Path.Combine(
            SettingsDirectory,
            "settings.ini");

        internal static AppSettings Load()
        {
            return LoadWithStatus(SettingsPath).Settings;
        }

        internal static AppSettings Load(string settingsPath)
        {
            return LoadWithStatus(settingsPath).Settings;
        }

        internal static SettingsLoadResult LoadWithStatus()
        {
            return LoadWithStatus(SettingsPath);
        }

        internal static SettingsLoadResult LoadWithStatus(string settingsPath)
        {
            SettingsFileReadResult primary = ReadFile(settingsPath);
            if (primary.Status == SettingsLoadStatus.Success)
            {
                return new SettingsLoadResult(
                    SettingsLoadStatus.Success,
                    primary.Settings,
                    false,
                    null);
            }

            string backupPath = string.IsNullOrWhiteSpace(settingsPath)
                ? string.Empty
                : settingsPath + ".bak";
            SettingsFileReadResult backup = ReadFile(backupPath);
            bool loadedFromBackup = backup.Status == SettingsLoadStatus.Success;
            return new SettingsLoadResult(
                primary.Status,
                loadedFromBackup ? backup.Settings : AppSettings.CreateDefault(),
                loadedFromBackup,
                backup.Status);
        }

        private static SettingsFileReadResult ReadFile(string settingsPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(settingsPath) ||
                    Directory.Exists(settingsPath))
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.IoFailure);
                }

                FileInfo file = new FileInfo(settingsPath);
                if (!file.Exists)
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Missing);
                }

                if (file.Length <= 0 || file.Length > MaximumSettingsFileBytes)
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                }

                Dictionary<string, string> values = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

                string[] lines = File.ReadAllLines(settingsPath, Encoding.UTF8);
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                    }

                    string key = line.Substring(0, separatorIndex).Trim();
                    string value = line.Substring(separatorIndex + 1).Trim();
                    if (!KnownSettingKeys.Contains(key))
                    {
                        return SettingsFileReadResult.Create(
                            SettingsLoadStatus.Unsupported);
                    }

                    if (values.ContainsKey(key))
                    {
                        return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                    }

                    values.Add(key, value);
                }

                string versionText;
                string leftText;
                string topText;
                string lockedText;
                string clickThroughText;
                int version;
                int left;
                int top;
                bool locked;
                bool clickThrough;
                int opacityPercent = AppSettings.DefaultOpacityPercent;
                AppSettings defaults = AppSettings.CreateDefault();

                if (!values.TryGetValue("Version", out versionText) ||
                    !int.TryParse(
                        versionText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out version))
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                }

                if (version != 1 && version != 2)
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Unsupported);
                }

                if (!values.TryGetValue("Left", out leftText) ||
                    !values.TryGetValue("Top", out topText) ||
                    !values.TryGetValue("Locked", out lockedText) ||
                    !values.TryGetValue("ClickThrough", out clickThroughText) ||
                    !int.TryParse(leftText, NumberStyles.Integer, CultureInfo.InvariantCulture, out left) ||
                    !int.TryParse(topText, NumberStyles.Integer, CultureInfo.InvariantCulture, out top) ||
                    !bool.TryParse(lockedText, out locked) ||
                    !bool.TryParse(clickThroughText, out clickThrough))
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                }

                string opacityPercentText;
                if (values.TryGetValue("OpacityPercent", out opacityPercentText) &&
                    (!int.TryParse(
                        opacityPercentText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out opacityPercent) ||
                     !AppSettings.IsSupportedOpacityPercent(opacityPercent)))
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                }

                bool isVersion2 = version == 2;
                bool persistHistory = defaults.PersistHistory;
                bool gatewayLatencyEnabled = defaults.GatewayLatencyEnabled;
                bool startupEnabled = defaults.StartupEnabled;
                bool automaticUpdateEnabled = defaults.AutomaticUpdateEnabled;
                bool loopbackApiEnabled = defaults.LoopbackApiEnabled;
                bool telemetryEnabled = defaults.TelemetryEnabled;
                if (isVersion2 &&
                    (!TryReadOptionalBoolean(
                        values,
                        "PersistHistory",
                        defaults.PersistHistory,
                        out persistHistory) ||
                     !TryReadOptionalBoolean(
                        values,
                        "GatewayLatencyEnabled",
                        defaults.GatewayLatencyEnabled,
                        out gatewayLatencyEnabled) ||
                     !TryReadOptionalBoolean(
                        values,
                        "StartupEnabled",
                        defaults.StartupEnabled,
                        out startupEnabled) ||
                     !TryReadOptionalBoolean(
                        values,
                        "AutomaticUpdateEnabled",
                        defaults.AutomaticUpdateEnabled,
                        out automaticUpdateEnabled) ||
                     !TryReadOptionalBoolean(
                        values,
                        "LoopbackApiEnabled",
                        defaults.LoopbackApiEnabled,
                        out loopbackApiEnabled) ||
                     !TryReadOptionalBoolean(
                        values,
                        "TelemetryEnabled",
                        defaults.TelemetryEnabled,
                        out telemetryEnabled)))
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                }
                string updateManifestUrl = string.Empty;
                string telemetryEndpoint = string.Empty;
                GpuSelectionMode gpuSelectionMode = GpuSelectionMode.Automatic;
                string selectedGpuAdapterId = string.Empty;
                string selectedGpuAdapterName = string.Empty;
                if (isVersion2 &&
                    (!TryReadOptionalHttpsEndpoint(
                        values,
                        "UpdateManifestUrl",
                        out updateManifestUrl) ||
                     !TryReadOptionalHttpsEndpoint(
                        values,
                        "TelemetryEndpoint",
                        out telemetryEndpoint) ||
                     !TryReadOptionalGpuSelectionMode(
                        values,
                        out gpuSelectionMode) ||
                     !TryReadOptionalGpuAdapterId(
                        values,
                        "SelectedGpuAdapterId",
                        out selectedGpuAdapterId) ||
                     !TryReadOptionalSingleLineText(
                        values,
                        "SelectedGpuAdapterName",
                        256,
                        out selectedGpuAdapterName)))
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                }

                if (gpuSelectionMode == GpuSelectionMode.Manual &&
                    selectedGpuAdapterId.Length == 0)
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                }

                if (gpuSelectionMode == GpuSelectionMode.Automatic &&
                    (selectedGpuAdapterId.Length != 0 ||
                     selectedGpuAdapterName.Length != 0))
                {
                    return SettingsFileReadResult.Create(SettingsLoadStatus.Corrupt);
                }

                if (automaticUpdateEnabled && updateManifestUrl.Length == 0)
                {
                    automaticUpdateEnabled = false;
                }

                if (telemetryEnabled && telemetryEndpoint.Length == 0)
                {
                    telemetryEnabled = false;
                }

                return new SettingsFileReadResult(
                    SettingsLoadStatus.Success,
                    new AppSettings
                    {
                        HasSavedPosition = true,
                        Left = left,
                        Top = top,
                        Locked = locked,
                        ClickThrough = clickThrough,
                        OpacityPercent = opacityPercent,
                        PersistHistory = persistHistory,
                        GatewayLatencyEnabled = gatewayLatencyEnabled,
                        GpuSelectionMode = gpuSelectionMode,
                        SelectedGpuAdapterId = selectedGpuAdapterId,
                        SelectedGpuAdapterName = selectedGpuAdapterName,
                        StartupEnabled = startupEnabled,
                        AutomaticUpdateEnabled = automaticUpdateEnabled,
                        UpdateManifestUrl = updateManifestUrl,
                        LoopbackApiEnabled = loopbackApiEnabled,
                        TelemetryEnabled = telemetryEnabled,
                        TelemetryEndpoint = telemetryEndpoint
                    });
            }
            catch (UnauthorizedAccessException)
            {
                return SettingsFileReadResult.Create(SettingsLoadStatus.IoFailure);
            }
            catch (IOException)
            {
                return SettingsFileReadResult.Create(SettingsLoadStatus.IoFailure);
            }
            catch (SecurityException)
            {
                return SettingsFileReadResult.Create(SettingsLoadStatus.IoFailure);
            }
            catch (NotSupportedException)
            {
                return SettingsFileReadResult.Create(SettingsLoadStatus.IoFailure);
            }
            catch (ArgumentException)
            {
                return SettingsFileReadResult.Create(SettingsLoadStatus.IoFailure);
            }
        }

        internal static bool Save(
            int left,
            int top,
            bool locked,
            bool clickThrough,
            int opacityPercent)
        {
            AppSettings settings = AppSettings.CreateDefault();
            settings.HasSavedPosition = true;
            settings.Left = left;
            settings.Top = top;
            settings.Locked = locked;
            settings.ClickThrough = clickThrough;
            settings.OpacityPercent = opacityPercent;
            return Save(settings);
        }

        internal static bool Save(AppSettings settings)
        {
            return Save(settings, SettingsPath);
        }

        internal static bool SaveAfterLoad(
            AppSettings settings,
            SettingsLoadResult loadResult)
        {
            return SaveAfterLoad(settings, loadResult, SettingsPath);
        }

        internal static bool SaveAfterLoad(
            AppSettings settings,
            SettingsLoadResult loadResult,
            string settingsPath)
        {
            return loadResult != null &&
                loadResult.AllowsAutomaticSave &&
                Save(settings, settingsPath);
        }

        internal static bool Save(AppSettings settings, string settingsPath)
        {
            if (settings == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(settingsPath))
            {
                return false;
            }

            try
            {
                string directory = Path.GetDirectoryName(settingsPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return false;
                }

                Directory.CreateDirectory(directory);

                string[] lines =
                {
                    "Version=2",
                    "Left=" + settings.Left.ToString(CultureInfo.InvariantCulture),
                    "Top=" + settings.Top.ToString(CultureInfo.InvariantCulture),
                    "Locked=" + settings.Locked.ToString(CultureInfo.InvariantCulture),
                    "ClickThrough=" + settings.ClickThrough.ToString(CultureInfo.InvariantCulture),
                    "OpacityPercent=" + settings.OpacityPercent.ToString(CultureInfo.InvariantCulture),
                    "PersistHistory=" + settings.PersistHistory.ToString(CultureInfo.InvariantCulture),
                    "GatewayLatencyEnabled=" + settings.GatewayLatencyEnabled.ToString(CultureInfo.InvariantCulture),
                    "GpuSelectionMode=" + NormalizeGpuSelectionMode(settings.GpuSelectionMode),
                    "SelectedGpuAdapterId=" +
                        (settings.GpuSelectionMode == GpuSelectionMode.Manual
                            ? NormalizeGpuAdapterId(settings.SelectedGpuAdapterId)
                            : string.Empty),
                    "SelectedGpuAdapterName=" +
                        (settings.GpuSelectionMode == GpuSelectionMode.Manual
                            ? NormalizeSingleLineText(settings.SelectedGpuAdapterName, 256)
                            : string.Empty),
                    "StartupEnabled=" + settings.StartupEnabled.ToString(CultureInfo.InvariantCulture),
                    "AutomaticUpdateEnabled=" + settings.AutomaticUpdateEnabled.ToString(CultureInfo.InvariantCulture),
                    "UpdateManifestUrl=" + NormalizeHttpsEndpoint(settings.UpdateManifestUrl),
                    "LoopbackApiEnabled=" + settings.LoopbackApiEnabled.ToString(CultureInfo.InvariantCulture),
                    "TelemetryEnabled=" + settings.TelemetryEnabled.ToString(CultureInfo.InvariantCulture),
                    "TelemetryEndpoint=" + NormalizeHttpsEndpoint(settings.TelemetryEndpoint)
                };

                string temporaryPath = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                string backupPath = settingsPath + ".bak";

                try
                {
                    File.WriteAllLines(temporaryPath, lines, new UTF8Encoding(false));

                    if (File.Exists(settingsPath))
                    {
                        File.Replace(temporaryPath, settingsPath, backupPath, true);
                    }
                    else
                    {
                        File.Move(temporaryPath, settingsPath);
                    }

                    return true;
                }
                finally
                {
                    TryDelete(temporaryPath);
                }
            }
            catch (Exception)
            {
                // Settings persistence is optional; monitoring must continue.
                return false;
            }
        }

        private static bool TryReadOptionalBoolean(
            Dictionary<string, string> values,
            string key,
            bool defaultValue,
            out bool value)
        {
            string text;
            if (!values.TryGetValue(key, out text))
            {
                value = defaultValue;
                return true;
            }

            return bool.TryParse(text, out value);
        }

        private static bool TryReadOptionalHttpsEndpoint(
            Dictionary<string, string> values,
            string key,
            out string value)
        {
            string text;
            if (!values.TryGetValue(key, out text) ||
                string.IsNullOrWhiteSpace(text))
            {
                value = string.Empty;
                return true;
            }

            value = NormalizeHttpsEndpoint(text);
            return value.Length != 0;
        }

        private static bool TryReadOptionalGpuSelectionMode(
            Dictionary<string, string> values,
            out GpuSelectionMode mode)
        {
            string text;
            if (!values.TryGetValue("GpuSelectionMode", out text))
            {
                mode = GpuSelectionMode.Automatic;
                return true;
            }

            if (string.Equals(text, "Manual", StringComparison.OrdinalIgnoreCase))
            {
                mode = GpuSelectionMode.Manual;
                return true;
            }

            mode = GpuSelectionMode.Automatic;
            return string.Equals(
                text,
                "Automatic",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadOptionalGpuAdapterId(
            Dictionary<string, string> values,
            string key,
            out string value)
        {
            string text;
            if (!values.TryGetValue(key, out text) || string.IsNullOrWhiteSpace(text))
            {
                value = string.Empty;
                return true;
            }

            value = NormalizeGpuAdapterId(text);
            return value.Length != 0;
        }

        private static bool TryReadOptionalSingleLineText(
            Dictionary<string, string> values,
            string key,
            int maximumLength,
            out string value)
        {
            string text;
            if (!values.TryGetValue(key, out text) || string.IsNullOrWhiteSpace(text))
            {
                value = string.Empty;
                return true;
            }

            string trimmed = text.Trim();
            if (maximumLength <= 0 || trimmed.Length > maximumLength ||
                trimmed.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                value = string.Empty;
                return false;
            }

            value = trimmed;
            return true;
        }

        private static string NormalizeGpuSelectionMode(GpuSelectionMode mode)
        {
            return mode == GpuSelectionMode.Manual ? "Manual" : "Automatic";
        }

        private static string NormalizeGpuAdapterId(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string value = text.Trim();
            if (value.Length != 17 || value[8] != ':')
            {
                return string.Empty;
            }

            for (int index = 0; index < value.Length; index++)
            {
                if (index == 8)
                {
                    continue;
                }

                char character = value[index];
                bool isHexadecimal =
                    (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F');
                if (!isHexadecimal)
                {
                    return string.Empty;
                }
            }

            return value.ToUpperInvariant();
        }

        private static string NormalizeSingleLineText(
            string text,
            int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(text) || maximumLength <= 0)
            {
                return string.Empty;
            }

            string value = text.Trim();
            int lineBreakIndex = value.IndexOfAny(new[] { '\r', '\n' });
            if (lineBreakIndex >= 0)
            {
                value = value.Substring(0, lineBreakIndex).Trim();
            }

            if (value.Length > maximumLength)
            {
                value = value.Substring(0, maximumLength).TrimEnd();
            }

            return value;
        }

        private static string NormalizeHttpsEndpoint(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2048)
            {
                return string.Empty;
            }

            Uri endpoint;
            string endpointError;
            return PublicHttpsEndpointPolicy.TryCreate(
                text.Trim(),
                out endpoint,
                out endpointError)
                ? endpoint.AbsoluteUri
                : string.Empty;
        }

        private sealed class SettingsFileReadResult
        {
            internal SettingsFileReadResult(
                SettingsLoadStatus status,
                AppSettings settings)
            {
                Status = status;
                Settings = settings ?? AppSettings.CreateDefault();
            }

            internal SettingsLoadStatus Status { get; private set; }

            internal AppSettings Settings { get; private set; }

            internal static SettingsFileReadResult Create(SettingsLoadStatus status)
            {
                return new SettingsFileReadResult(status, AppSettings.CreateDefault());
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
                // Cleanup failures must not terminate monitoring.
            }
        }
    }
}
