using System;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace TinyHwBar
{
    internal sealed class SingletonIdentityUnavailableException : Exception
    {
        internal SingletonIdentityUnavailableException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class SingletonMutexSecurityException : Exception
    {
        internal SingletonMutexSecurityException(string message)
            : base(message)
        {
        }

        internal SingletonMutexSecurityException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal static class Program
    {
        private const string LocalMutexName = @"Local\TinyHwBar.Singleton";

        internal static string BuildSingletonMutexName(string userSid)
        {
            string normalizedSid = (userSid ?? string.Empty).Trim();
            if (normalizedSid.Length == 0 || normalizedSid.IndexOf('\\') >= 0)
            {
                return LocalMutexName;
            }

            return @"Global\TinyHwBar.Singleton." + normalizedSid;
        }

        internal static string BuildRequiredSingletonMutexName(string userSid)
        {
            string normalizedSid = (userSid ?? string.Empty).Trim();
            try
            {
                SecurityIdentifier sid = new SecurityIdentifier(normalizedSid);
                return @"Global\TinyHwBar.Singleton." + sid.Value;
            }
            catch (ArgumentException exception)
            {
                throw new SingletonIdentityUnavailableException(
                    "The current Windows user SID is unavailable or invalid.",
                    exception);
            }
        }

        internal static string GetSingletonMutexName(
            out SecurityIdentifier currentUserSid)
        {
            currentUserSid = null;
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity == null || identity.User == null)
                    {
                        throw new SingletonIdentityUnavailableException(
                            "The current Windows identity does not expose a user SID.",
                            null);
                    }

                    currentUserSid = identity.User;
                    return BuildRequiredSingletonMutexName(currentUserSid.Value);
                }
            }
            catch (SingletonIdentityUnavailableException)
            {
                currentUserSid = null;
                throw;
            }
            catch (Exception exception)
            {
                currentUserSid = null;
                throw new SingletonIdentityUnavailableException(
                    "The current Windows user SID could not be read.",
                    exception);
            }
        }

        internal static MutexSecurity CreateTrustedGlobalMutexSecurity(
            SecurityIdentifier currentUserSid)
        {
            if (currentUserSid == null)
            {
                throw new ArgumentNullException("currentUserSid");
            }

            SecurityIdentifier localSystemSid = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            MutexSecurity security = new MutexSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(currentUserSid);
            security.AddAccessRule(new MutexAccessRule(
                currentUserSid,
                MutexRights.FullControl,
                AccessControlType.Allow));
            if (!SidEquals(currentUserSid, localSystemSid))
            {
                security.AddAccessRule(new MutexAccessRule(
                    localSystemSid,
                    MutexRights.FullControl,
                    AccessControlType.Allow));
            }

            return security;
        }

        internal static bool IsTrustedGlobalMutexSecurity(
            MutexSecurity security,
            SecurityIdentifier currentUserSid)
        {
            if (security == null || currentUserSid == null ||
                !security.AreAccessRulesProtected ||
                !security.AreAccessRulesCanonical)
            {
                return false;
            }

            SecurityIdentifier owner = security.GetOwner(
                typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!SidEquals(owner, currentUserSid))
            {
                return false;
            }

            SecurityIdentifier localSystemSid = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            bool samePrincipal = SidEquals(currentUserSid, localSystemSid);
            int currentUserRuleCount = 0;
            int localSystemRuleCount = 0;
            AuthorizationRuleCollection rules = security.GetAccessRules(
                true,
                true,
                typeof(SecurityIdentifier));
            foreach (AuthorizationRule authorizationRule in rules)
            {
                MutexAccessRule rule = authorizationRule as MutexAccessRule;
                SecurityIdentifier ruleSid = rule == null
                    ? null
                    : rule.IdentityReference as SecurityIdentifier;
                if (rule == null || ruleSid == null || rule.IsInherited ||
                    rule.AccessControlType != AccessControlType.Allow ||
                    rule.MutexRights != MutexRights.FullControl)
                {
                    return false;
                }

                bool isCurrentUser = SidEquals(ruleSid, currentUserSid);
                bool isLocalSystem = SidEquals(ruleSid, localSystemSid);
                if (!isCurrentUser && !isLocalSystem)
                {
                    return false;
                }

                if (isCurrentUser)
                {
                    currentUserRuleCount++;
                }

                if (isLocalSystem)
                {
                    localSystemRuleCount++;
                }
            }

            if (samePrincipal)
            {
                return rules.Count == 1 &&
                    currentUserRuleCount == 1 &&
                    localSystemRuleCount == 1;
            }

            return rules.Count == 2 &&
                currentUserRuleCount == 1 &&
                localSystemRuleCount == 1;
        }

        private static bool SidEquals(
            SecurityIdentifier left,
            SecurityIdentifier right)
        {
            return left != null && right != null &&
                string.Equals(
                    left.Value,
                    right.Value,
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static IDisposable TryAcquireSingletonMutexes(
            string primaryMutexName,
            string legacyMutexName)
        {
            return TryAcquireSingletonMutexes(
                primaryMutexName,
                legacyMutexName,
                null);
        }

        internal static IDisposable TryAcquireSingletonMutexes(
            string primaryMutexName,
            string legacyMutexName,
            SecurityIdentifier globalOwnerSid)
        {
            if (string.IsNullOrWhiteSpace(primaryMutexName))
            {
                throw new ArgumentException("A primary mutex name is required.", "primaryMutexName");
            }

            if (string.IsNullOrWhiteSpace(legacyMutexName))
            {
                throw new ArgumentException("A legacy mutex name is required.", "legacyMutexName");
            }

            bool isGlobalMutex = primaryMutexName.StartsWith(
                @"Global\",
                StringComparison.Ordinal);
            if (isGlobalMutex && globalOwnerSid == null)
            {
                throw new SingletonMutexSecurityException(
                    "A current-user SID is required for the Global singleton mutex.");
            }

            Mutex primaryMutex = isGlobalMutex
                ? TryCreateSecuredGlobalMutex(
                    primaryMutexName,
                    globalOwnerSid)
                : TryCreateOwnedMutex(primaryMutexName);
            if (primaryMutex == null)
            {
                return null;
            }

            if (string.Equals(
                    primaryMutexName,
                    legacyMutexName,
                    StringComparison.Ordinal))
            {
                return new SingletonMutexLease(primaryMutex, null);
            }

            try
            {
                Mutex legacyMutex = TryCreateOwnedMutex(legacyMutexName);
                if (legacyMutex == null)
                {
                    ReleaseAndDisposeMutex(primaryMutex);
                    return null;
                }

                return new SingletonMutexLease(primaryMutex, legacyMutex);
            }
            catch
            {
                ReleaseAndDisposeMutex(primaryMutex);
                throw;
            }
        }

        private static Mutex TryCreateOwnedMutex(string mutexName)
        {
            bool createdNew;
            Mutex mutex = new Mutex(true, mutexName, out createdNew);
            if (createdNew)
            {
                return mutex;
            }

            mutex.Dispose();
            return null;
        }

        private static Mutex TryCreateSecuredGlobalMutex(
            string mutexName,
            SecurityIdentifier currentUserSid)
        {
            MutexSecurity requestedSecurity =
                CreateTrustedGlobalMutexSecurity(currentUserSid);
            bool createdNew;
            Mutex mutex = new Mutex(
                true,
                mutexName,
                out createdNew,
                requestedSecurity);
            try
            {
                MutexSecurity actualSecurity = mutex.GetAccessControl();
                if (!IsTrustedGlobalMutexSecurity(
                        actualSecurity,
                        currentUserSid))
                {
                    throw new SingletonMutexSecurityException(
                        createdNew
                            ? "The new Global singleton mutex has an unexpected security descriptor."
                            : "The existing Global singleton mutex has an untrusted owner or access list.");
                }
            }
            catch (SingletonMutexSecurityException)
            {
                CloseCreatedMutex(mutex, createdNew);
                throw;
            }
            catch (Exception exception)
            {
                CloseCreatedMutex(mutex, createdNew);
                throw new SingletonMutexSecurityException(
                    "The Global singleton mutex security descriptor could not be verified.",
                    exception);
            }

            if (createdNew)
            {
                return mutex;
            }

            mutex.Dispose();
            return null;
        }

        private static void CloseCreatedMutex(Mutex mutex, bool owned)
        {
            if (owned)
            {
                ReleaseAndDisposeMutex(mutex);
                return;
            }

            mutex.Dispose();
        }

        private static void ReleaseAndDisposeMutex(Mutex mutex)
        {
            if (mutex == null)
            {
                return;
            }

            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The owning thread may already have ended during process teardown.
            }
            finally
            {
                mutex.Dispose();
            }
        }

        [STAThread]
        private static void Main()
        {
            IDisposable singletonMutexes;
            SecurityIdentifier currentUserSid;

            try
            {
                string singletonMutexName = GetSingletonMutexName(
                    out currentUserSid);
                singletonMutexes = TryAcquireSingletonMutexes(
                    singletonMutexName,
                    LocalMutexName,
                    currentUserSid);
            }
            catch (SingletonIdentityUnavailableException)
            {
                ShowSingletonIdentityFailure();
                return;
            }
            catch (UnauthorizedAccessException)
            {
                ShowSingletonMutexFailure();
                return;
            }
            catch (SingletonMutexSecurityException)
            {
                ShowSingletonMutexFailure();
                return;
            }
            catch (IOException)
            {
                ShowSingletonMutexFailure();
                return;
            }
            catch (SecurityException)
            {
                ShowSingletonMutexFailure();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                ShowSingletonMutexFailure();
                return;
            }

            if (singletonMutexes == null)
            {
                MessageBox.Show(
                    "TinyHwBar 已在运行。请先从系统托盘菜单选择“退出”，再启动新版本。",
                    "TinyHwBar",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using (singletonMutexes)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (MonitorForm form = new MonitorForm())
                {
                    Application.Run(form);
                }
            }
        }

        private static void ShowSingletonMutexFailure()
        {
            MessageBox.Show(
                "无法安全验证 TinyHwBar 单例锁的所有者或访问权限，程序未启动。\r\n\r\n请先退出其他会话中的 TinyHwBar 后重试。",
                "TinyHwBar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private static void ShowSingletonIdentityFailure()
        {
            MessageBox.Show(
                "无法读取当前 Windows 用户 SID，因此不能安全建立跨会话单例锁。TinyHwBar 未启动。\r\n\r\n请重新登录 Windows 后再试。",
                "TinyHwBar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private sealed class SingletonMutexLease : IDisposable
        {
            private Mutex primaryMutex;
            private Mutex legacyMutex;

            internal SingletonMutexLease(Mutex primary, Mutex legacy)
            {
                primaryMutex = primary;
                legacyMutex = legacy;
            }

            public void Dispose()
            {
                Mutex legacy = legacyMutex;
                Mutex primary = primaryMutex;
                legacyMutex = null;
                primaryMutex = null;

                ReleaseAndDisposeMutex(legacy);
                ReleaseAndDisposeMutex(primary);
            }
        }
    }
}
