using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TinyHwBar
{
    internal sealed class AppSettings
    {
        internal bool HasSavedPosition { get; set; }
        internal int Left { get; set; }
        internal int Top { get; set; }
        internal bool Locked { get; set; }
        internal bool ClickThrough { get; set; }

        internal static AppSettings CreateDefault()
        {
            return new AppSettings
            {
                HasSavedPosition = false,
                Left = 0,
                Top = 0,
                Locked = false,
                ClickThrough = false
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
            if (!File.Exists(SettingsPath))
            {
                return AppSettings.CreateDefault();
            }

            try
            {
                Dictionary<string, string> values = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

                string[] lines = File.ReadAllLines(SettingsPath, Encoding.UTF8);
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

                if (!values.TryGetValue("Version", out versionText) ||
                    !values.TryGetValue("Left", out leftText) ||
                    !values.TryGetValue("Top", out topText) ||
                    !values.TryGetValue("Locked", out lockedText) ||
                    !values.TryGetValue("ClickThrough", out clickThroughText) ||
                    !int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) ||
                    version != 1 ||
                    !int.TryParse(leftText, NumberStyles.Integer, CultureInfo.InvariantCulture, out left) ||
                    !int.TryParse(topText, NumberStyles.Integer, CultureInfo.InvariantCulture, out top) ||
                    !bool.TryParse(lockedText, out locked) ||
                    !bool.TryParse(clickThroughText, out clickThrough))
                {
                    return AppSettings.CreateDefault();
                }

                return new AppSettings
                {
                    HasSavedPosition = true,
                    Left = left,
                    Top = top,
                    Locked = locked,
                    ClickThrough = clickThrough
                };
            }
            catch (Exception)
            {
                return AppSettings.CreateDefault();
            }
        }

        internal static void Save(int left, int top, bool locked, bool clickThrough)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);

                string[] lines =
                {
                    "Version=1",
                    "Left=" + left.ToString(CultureInfo.InvariantCulture),
                    "Top=" + top.ToString(CultureInfo.InvariantCulture),
                    "Locked=" + locked.ToString(CultureInfo.InvariantCulture),
                    "ClickThrough=" + clickThrough.ToString(CultureInfo.InvariantCulture)
                };

                File.WriteAllLines(SettingsPath, lines, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Settings persistence is optional; monitoring must continue.
            }
        }
    }
}
