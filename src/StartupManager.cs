using System;
using System.IO;
using Microsoft.Win32;

namespace TinyHwBar
{
    internal enum StartupRegistrationState
    {
        Disabled,
        EnabledForCurrentExecutable,
        DifferentCommand
    }

    internal enum StartupStoredValueKind
    {
        Missing,
        String,
        NonString
    }

    internal sealed class StartupStoredValue
    {
        private StartupStoredValue(
            StartupStoredValueKind kind,
            string commandLine)
        {
            Kind = kind;
            CommandLine = commandLine;
        }

        internal StartupStoredValueKind Kind { get; private set; }
        internal string CommandLine { get; private set; }

        internal static StartupStoredValue Missing()
        {
            return new StartupStoredValue(StartupStoredValueKind.Missing, null);
        }

        internal static StartupStoredValue FromString(string commandLine)
        {
            return new StartupStoredValue(StartupStoredValueKind.String, commandLine);
        }

        internal static StartupStoredValue NonString()
        {
            return new StartupStoredValue(StartupStoredValueKind.NonString, null);
        }
    }

    internal interface IStartupRegistrationStore
    {
        StartupStoredValue Read(string valueName);
        bool WriteIfMissingOrMatching(string valueName, string commandLine);
        void Replace(string valueName, string commandLine);
        bool DeleteIfMatches(string valueName, string expectedCommandLine);
    }

    internal sealed class RegistryStartupRegistrationStore : IStartupRegistrationStore
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static readonly object MissingValue = new object();

        public StartupStoredValue Read(string valueName)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null)
                {
                    return StartupStoredValue.Missing();
                }

                return ReadStoredValue(key, valueName);
            }
        }

        public bool WriteIfMissingOrMatching(string valueName, string commandLine)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Could not open the current-user Run key.");
                }

                StartupStoredValue currentValue = ReadStoredValue(key, valueName);
                if (currentValue.Kind != StartupStoredValueKind.Missing &&
                    (currentValue.Kind != StartupStoredValueKind.String ||
                     !CommandLinesMatch(currentValue.CommandLine, commandLine)))
                {
                    return false;
                }

                key.SetValue(valueName, commandLine, RegistryValueKind.String);
                return true;
            }
        }

        public void Replace(string valueName, string commandLine)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Could not open the current-user Run key.");
                }

                key.SetValue(valueName, commandLine, RegistryValueKind.String);
            }
        }

        public bool DeleteIfMatches(string valueName, string expectedCommandLine)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null)
                {
                    return true;
                }

                StartupStoredValue currentValue = ReadStoredValue(key, valueName);
                if (currentValue.Kind == StartupStoredValueKind.Missing)
                {
                    return true;
                }

                if (currentValue.Kind != StartupStoredValueKind.String ||
                    !CommandLinesMatch(currentValue.CommandLine, expectedCommandLine))
                {
                    return false;
                }

                key.DeleteValue(valueName, false);
                return true;
            }
        }

        private static StartupStoredValue ReadStoredValue(
            RegistryKey key,
            string valueName)
        {
            object rawValue = key.GetValue(
                valueName,
                MissingValue,
                RegistryValueOptions.DoNotExpandEnvironmentNames);

            if (object.ReferenceEquals(rawValue, MissingValue))
            {
                return StartupStoredValue.Missing();
            }

            string commandLine = rawValue as string;
            return commandLine != null
                ? StartupStoredValue.FromString(commandLine)
                : StartupStoredValue.NonString();
        }

        private static bool CommandLinesMatch(string left, string right)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    internal sealed class StartupManager
    {
        internal const string DefaultValueName = "TinyHwBar";

        private readonly string executablePath;
        private readonly string expectedCommandLine;
        private readonly string valueName;
        private readonly IStartupRegistrationStore store;
        private readonly Func<string, DriveType> driveTypeResolver;

        internal StartupManager(string executablePath)
            : this(executablePath, DefaultValueName, new RegistryStartupRegistrationStore())
        {
        }

        internal StartupManager(
            string executablePath,
            string valueName,
            IStartupRegistrationStore store)
            : this(executablePath, valueName, store, ResolveDriveType)
        {
        }

        internal StartupManager(
            string executablePath,
            string valueName,
            IStartupRegistrationStore store,
            Func<string, DriveType> driveTypeResolver)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("An executable path is required.", "executablePath");
            }

            if (string.IsNullOrWhiteSpace(valueName))
            {
                throw new ArgumentException("A startup value name is required.", "valueName");
            }

            if (store == null)
            {
                throw new ArgumentNullException("store");
            }

            if (driveTypeResolver == null)
            {
                throw new ArgumentNullException("driveTypeResolver");
            }

            string fullPath = Path.GetFullPath(executablePath);
            if (!string.Equals(
                Path.GetExtension(fullPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The startup path must identify an .exe file.", "executablePath");
            }

            this.executablePath = fullPath;
            this.valueName = valueName;
            this.store = store;
            this.driveTypeResolver = driveTypeResolver;
            expectedCommandLine = QuoteExecutablePath(fullPath);
        }

        internal string ExecutablePath
        {
            get { return executablePath; }
        }

        internal string ExpectedCommandLine
        {
            get { return expectedCommandLine; }
        }

        internal StartupRegistrationState GetStatus()
        {
            StartupStoredValue registeredValue = store.Read(valueName);
            if (registeredValue == null)
            {
                return StartupRegistrationState.DifferentCommand;
            }

            if (registeredValue.Kind == StartupStoredValueKind.Missing)
            {
                return StartupRegistrationState.Disabled;
            }

            return registeredValue.Kind == StartupStoredValueKind.String &&
                CommandLinesMatch(registeredValue.CommandLine, expectedCommandLine)
                ? StartupRegistrationState.EnabledForCurrentExecutable
                : StartupRegistrationState.DifferentCommand;
        }

        internal void EnableForCurrentUser()
        {
            EnsureLocalExecutablePath();
            EnsureExecutableExists();
            if (!store.WriteIfMissingOrMatching(valueName, expectedCommandLine))
            {
                throw new InvalidOperationException(
                    "A different startup command already uses this value name. " +
                    "It was not overwritten.");
            }
        }

        internal void ReplaceForCurrentUserAfterConfirmation(bool userConfirmed)
        {
            if (!userConfirmed)
            {
                throw new InvalidOperationException(
                    "Replacing a different startup command requires explicit confirmation.");
            }

            EnsureLocalExecutablePath();
            EnsureExecutableExists();
            store.Replace(valueName, expectedCommandLine);
        }

        internal bool DisableForCurrentUser()
        {
            return store.DeleteIfMatches(valueName, expectedCommandLine);
        }

        internal static string QuoteExecutablePath(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("An executable path is required.", "executablePath");
            }

            if (executablePath.IndexOf('"') >= 0)
            {
                throw new ArgumentException("Executable paths cannot contain quotes.", "executablePath");
            }

            return "\"" + executablePath + "\"";
        }

        private static bool CommandLinesMatch(string left, string right)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        private void EnsureLocalExecutablePath()
        {
            if (executablePath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                ThrowNonLocalExecutablePath();
            }

            DriveType driveType;
            try
            {
                driveType = driveTypeResolver(executablePath);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Startup can only be enabled when the executable's local drive type can be verified.",
                    exception);
            }

            if (driveType == DriveType.Network ||
                driveType == DriveType.Unknown ||
                driveType == DriveType.NoRootDirectory)
            {
                ThrowNonLocalExecutablePath();
            }
        }

        private static DriveType ResolveDriveType(string fullPath)
        {
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return DriveType.NoRootDirectory;
            }

            return new DriveInfo(root).DriveType;
        }

        private static void ThrowNonLocalExecutablePath()
        {
            throw new InvalidOperationException(
                "Startup can only be enabled for an executable on a local file-system path.");
        }

        private void EnsureExecutableExists()
        {
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    "The executable must exist before startup can be enabled.",
                    executablePath);
            }
        }
    }
}
