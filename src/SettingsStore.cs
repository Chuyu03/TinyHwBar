using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TinyHwBar
{
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
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TinyHwBar");

        private static readonly string SettingsPath = Path.Combine(
            SettingsDirectory,
            "settings.ini");

        internal static AppSettings Load()
        {
            return Load(SettingsPath);
        }

        internal static AppSettings Load(string settingsPath)
        {
            if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
            {
                return AppSettings.CreateDefault();
            }

            try
            {
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
                        return AppSettings.CreateDefault();
                    }

                    string key = line.Substring(0, separatorIndex).Trim();
                    string value = line.Substring(separatorIndex + 1).Trim();
                    if (values.ContainsKey(key))
                    {
                        return AppSettings.CreateDefault();
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
                    !values.TryGetValue("Left", out leftText) ||
                    !values.TryGetValue("Top", out topText) ||
                    !values.TryGetValue("Locked", out lockedText) ||
                    !values.TryGetValue("ClickThrough", out clickThroughText) ||
                    !int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) ||
                    (version != 1 && version != 2) ||
                    !int.TryParse(leftText, NumberStyles.Integer, CultureInfo.InvariantCulture, out left) ||
                    !int.TryParse(topText, NumberStyles.Integer, CultureInfo.InvariantCulture, out top) ||
                    !bool.TryParse(lockedText, out locked) ||
                    !bool.TryParse(clickThroughText, out clickThrough))
                {
                    return AppSettings.CreateDefault();
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
                    opacityPercent = AppSettings.DefaultOpacityPercent;
                }

                bool isVersion2 = version == 2;
                bool persistHistory = isVersion2
                    ? ReadOptionalBoolean(
                        values,
                        "PersistHistory",
                        defaults.PersistHistory)
                    : defaults.PersistHistory;
                bool gatewayLatencyEnabled = isVersion2
                    ? ReadOptionalBoolean(
                        values,
                        "GatewayLatencyEnabled",
                        defaults.GatewayLatencyEnabled)
                    : defaults.GatewayLatencyEnabled;
                bool startupEnabled = isVersion2
                    ? ReadOptionalBoolean(
                        values,
                        "StartupEnabled",
                        defaults.StartupEnabled)
                    : defaults.StartupEnabled;
                bool automaticUpdateEnabled = isVersion2
                    ? ReadOptionalBoolean(
                        values,
                        "AutomaticUpdateEnabled",
                        defaults.AutomaticUpdateEnabled)
                    : defaults.AutomaticUpdateEnabled;
                bool loopbackApiEnabled = isVersion2
                    ? ReadOptionalBoolean(
                        values,
                        "LoopbackApiEnabled",
                        defaults.LoopbackApiEnabled)
                    : defaults.LoopbackApiEnabled;
                bool telemetryEnabled = isVersion2
                    ? ReadOptionalBoolean(
                        values,
                        "TelemetryEnabled",
                        defaults.TelemetryEnabled)
                    : defaults.TelemetryEnabled;
                string updateManifestUrl = isVersion2
                    ? ReadOptionalHttpsEndpoint(values, "UpdateManifestUrl")
                    : string.Empty;
                string telemetryEndpoint = isVersion2
                    ? ReadOptionalHttpsEndpoint(values, "TelemetryEndpoint")
                    : string.Empty;

                if (automaticUpdateEnabled && updateManifestUrl.Length == 0)
                {
                    automaticUpdateEnabled = false;
                }

                if (telemetryEnabled && telemetryEndpoint.Length == 0)
                {
                    telemetryEnabled = false;
                }

                return new AppSettings
                {
                    HasSavedPosition = true,
                    Left = left,
                    Top = top,
                    Locked = locked,
                    ClickThrough = clickThrough,
                    OpacityPercent = opacityPercent,
                    PersistHistory = persistHistory,
                    GatewayLatencyEnabled = gatewayLatencyEnabled,
                    StartupEnabled = startupEnabled,
                    AutomaticUpdateEnabled = automaticUpdateEnabled,
                    UpdateManifestUrl = updateManifestUrl,
                    LoopbackApiEnabled = loopbackApiEnabled,
                    TelemetryEnabled = telemetryEnabled,
                    TelemetryEndpoint = telemetryEndpoint
                };
            }
            catch (Exception)
            {
                return AppSettings.CreateDefault();
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
                        TryDelete(backupPath);
                        File.Replace(temporaryPath, settingsPath, backupPath, true);
                        TryDelete(backupPath);
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

        private static bool ReadOptionalBoolean(
            Dictionary<string, string> values,
            string key,
            bool defaultValue)
        {
            string text;
            bool value;
            return values.TryGetValue(key, out text) && bool.TryParse(text, out value)
                ? value
                : defaultValue;
        }

        private static string ReadOptionalHttpsEndpoint(
            Dictionary<string, string> values,
            string key)
        {
            string text;
            return values.TryGetValue(key, out text)
                ? NormalizeHttpsEndpoint(text)
                : string.Empty;
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
