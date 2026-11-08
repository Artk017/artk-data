using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace ArtkTools.Services;

/// <summary>当前用户对某个键是否已拥有「完全控制」。</summary>
public enum AclState
{
    /// <summary>读不到 AC，无法判断（通常是没有读权限）。</summary>
    Unknown,

    /// <summary>还没有「完全控制」。</summary>
    Missing,

    /// <summary>已经有「完全控制」。</summary>
    Granted,
}

/// <summary>
/// 给注册表键追加 ACL 并把所有者改成当前用户（含子键）。
/// 等价于 regedit「权限 → 添加 → 完全控制」+「高级 → 所有者 → 替换子容器和对象的所有者」。
/// 改 HKLM 需要提权：未提权时用 runas 重新拉起自身，由提权实例执行后退出。
/// </summary>
public static class RegistryAcl
{
    /// <summary>目标键（HKLM 下的相对路径）。</summary>
    public const string TargetSubKey = @"SYSTEM\CurrentControlSet\Services\wscsvc";

    /// <summary>提权实例的命令行开关。</summary>
    public const string ElevationSwitch = "--grant-key-acl";

    // RegOpenKeyEx 的访问位
    private const int KeyRead = 0x20019;
    private const int KeyAllAccess = 0x001F003F;
    private const int KeyEnumerateSubKeys = 0x0008;
    private const int ReadControl = 0x00020000;
    private const int WriteDac = 0x00040000;
    private const int WriteOwner = 0x00080000;

    /// <summary>当前进程是否已经是管理员权限。</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>当前用户的 SID，比账户名更稳（改名也不会失效）。</summary>
    public static string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;

    /// <summary>当前用户的账户名（微软账号会显示成「rtk A (artk-bk@outlook.com)」这种）。</summary>
    public static string CurrentUserDisplayName
    {
        get
        {
            try
            {
                var sid = new SecurityIdentifier(CurrentUserSid);
                return sid.Translate(typeof(NTAccount)).Value;
            }
            catch (Exception)
            {
                return Environment.UserName;
            }
        }
    }

    /// <summary>给 HKLM 下的某个键：所有者改成当前用户（递归子键），并追加「完全控制」。</summary>
    public static bool Apply(string subKeyPath, out string error)
    {
        error = string.Empty;

        try
        {
            // 改所有者需要这两个特权，管理员令牌里默认是关闭的
            EnablePrivilege("SeTakeOwnershipPrivilege");
            EnablePrivilege("SeRestorePrivilege");

            var owner = new SecurityIdentifier(CurrentUserSid);

            // 1) 所有者 = 当前用户，并「替换子容器和对象的所有者」
            SetOwnerRecursive(subKeyPath, owner);

            // 2) 追加完全控制（拿到所有权后 WRITE_DAC 才有效）
            using RegistryKey? key = OpenKey(subKeyPath, ReadControl | WriteDac);
            if (key is null)
            {
                error = "打开注册表键失败";
                return false;
            }

            RegistrySecurity security = key.GetAccessControl(AccessControlSections.Access);

            if (!HasFullControl(security, owner, includeInherited: false))
            {
                security.AddAccessRule(new RegistryAccessRule(
                    owner,
                    RegistryRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            key.SetAccessControl(security);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "需要管理员权限";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>把键及其所有子键的所有者改成指定账户。</summary>
    private static void SetOwnerRecursive(string subKeyPath, SecurityIdentifier owner)
    {
        try
        {
            using RegistryKey? key = OpenKey(subKeyPath, ReadControl | WriteOwner | KeyEnumerateSubKeys | KeyRead);
            if (key is null)
            {
                return;
            }

            RegistrySecurity security = key.GetAccessControl(AccessControlSections.Owner);
            SecurityIdentifier? current = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

            if (current != owner)
            {
                security.SetOwner(owner);
                key.SetAccessControl(security);
            }

            List<string> children = key.GetSubKeyNames().ToList();
            foreach (string name in children)
            {
                SetOwnerRecursive(subKeyPath + "\\" + name, owner);
            }
        }
        catch (Exception)
        {
            // 单个键失败不影响其它键
        }
    }

    /// <summary>DACL 里是否已有该账户的「完全控制」允许项。</summary>
    internal static bool HasFullControl(RegistrySecurity security, SecurityIdentifier user, bool includeInherited)
    {
        return security
            .GetAccessRules(includeExplicit: true, includeInherited: includeInherited, targetType: typeof(SecurityIdentifier))
            .Cast<RegistryAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                         rule.IdentityReference == user &&
                         (rule.RegistryRights & RegistryRights.FullControl) == RegistryRights.FullControl);
    }

    /// <summary>
    /// 检测当前用户（按 SID，不看它所在的管理员组）是否已对该键有「完全控制」。
    /// 能读 DACL 就按 ACE 判断；读不到（连 READ_CONTROL 都被拒）则退化成
    /// 「能否以完全控制打开」的能力探测——被拒说明确实没有完全控制。
    /// </summary>
    public static AclState CheckFullControl(string subKeyPath)
    {
        var owner = new SecurityIdentifier(CurrentUserSid);

        // 1) KEY_READ 里含 READ_CONTROL，够读 DACL
        using (RegistryKey? key = OpenKey(subKeyPath, KeyRead))
        {
            if (key is not null)
            {
                try
                {
                    RegistrySecurity security = key.GetAccessControl(AccessControlSections.Access);
                    return HasFullControl(security, owner, includeInherited: true)
                        ? AclState.Granted
                        : AclState.Missing;
                }
                catch (Exception)
                {
                    // 读 ACL 失败，落到下面的能力探测
                }
            }
        }

        // 2) 拿不到 DACL：能按完全控制打开就等于有权限，被拒就等于没有
        try
        {
            using RegistryKey? full = OpenKey(subKeyPath, KeyAllAccess);
            return full is not null ? AclState.Granted : AclState.Missing;
        }
        catch (Exception)
        {
            return AclState.Unknown;
        }
    }

    /// <summary>按指定访问权限打开 HKLM 下的键；失败返回 null。</summary>
    private static RegistryKey? OpenKey(string subKeyPath, int access) => HklmKey.Open(subKeyPath, access);

    /// <summary>
    /// 未提权时用 runas 拉起自身，让提权实例去改；返回这次操作是否成功。
    /// 提权实例执行完立刻退出，不会多开主窗口。
    /// </summary>
    public static bool ApplyViaElevatedSelf(string subKeyPath, out string error)
    {
        error = string.Empty;

        try
        {
            string exe = Environment.ProcessPath ?? string.Empty;
            if (exe.Length == 0)
            {
                error = "拿不到程序路径";
                return false;
            }

            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{ElevationSwitch} \"{subKeyPath}\"",
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动提权实例";
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // 用户在 UAC 上点了「否」
            error = "已取消提权";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------------- Win32 ----------------

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
        {
            return;
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out Luid luid))
            {
                return;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
