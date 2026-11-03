using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WinUIDemo.Services;

/// <summary>
/// 按最小权限打开 HKLM 下的键。
/// 不用 <see cref="Registry.LocalMachine"/> 的 OpenSubKey，是因为它默认要 READ_CONTROL，
/// 而像 wscsvc 这种键普通用户连读 ACL 都会被拒；这里只请求刚好够用的访问位。
/// </summary>
internal static class HklmKey
{
    private static readonly IntPtr Hklm = new(unchecked((int)0x80000002));

    /// <summary>按指定访问位打开键；打不开（含拒绝访问）返回 null。</summary>
    public static RegistryKey? Open(string subKeyPath, int access)
    {
        int result = RegOpenKeyEx(Hklm, subKeyPath, 0, access, out IntPtr handle);

        return result == 0 && handle != IntPtr.Zero
            ? RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true))
            : null;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyEx(IntPtr hKey, string subKey, uint options, int samDesired, out IntPtr result);
}
