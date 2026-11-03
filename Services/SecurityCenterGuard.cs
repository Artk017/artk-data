using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WinUIDemo.Services;

/// <summary>
/// 「安全中心设置」一键开关：把 Windows 安全中心里那批开关批量关掉 / 恢复。
/// <para>
/// 关掉的项：内存完整性、本地安全机构保护、易受攻击的驱动程序阻止列表、检查应用和文件、
/// Edge 的 SmartScreen、Store 应用的 SmartScreen、网络钓鱼防护、实时保护、云提供的保护、
/// 自动提交样本、开发人员驱动器保护、智能应用控制。
/// 打开时全部恢复（智能应用控制置为 1）。
/// </para>
/// <para>
/// 注册表项自己写；Defender 那几项走 <c>Set-MpPreference</c>；篡改防护没有公开接口，
/// 只能报告「需要在安全中心里手动关」。整段逻辑塞进一个临时 .ps1 交给管理员 PowerShell 跑，
/// 每一步的成败写进日志，退出码 = 失败条数。
/// </para>
/// </summary>
public static class SecurityCenterGuard
{
    private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string LsaKey = @"SYSTEM\CurrentControlSet\Control\Lsa";
    private const string CiConfigKey = @"SYSTEM\CurrentControlSet\Control\CI\Config";
    private const string CiPolicyKey = @"SYSTEM\CurrentControlSet\Control\CI\Policy";
    private const string ExplorerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
    private const string EdgeKey = @"Software\Microsoft\Edge\SmartScreenEnabled";
    private const string AppHostKey = @"Software\Microsoft\Windows\CurrentVersion\AppHost";

    /// <summary>注册表这 7 项全关才算「已关闭」；Defender 那几项不参与判定（读它们要起 PowerShell，太慢）。</summary>
    public static bool IsDisabled()
    {
        return ReadDword(RegistryHive.LocalMachine, HvciKey, "Enabled") == 0
            && ReadDword(RegistryHive.LocalMachine, LsaKey, "RunAsPPL") is null or 0
            && ReadDword(RegistryHive.LocalMachine, CiConfigKey, "VulnerableDriverBlocklistEnable") == 0
            && ReadDword(RegistryHive.LocalMachine, CiPolicyKey, "VerifiedAndReputablePolicyState") == 0
            && string.Equals(ReadString(RegistryHive.LocalMachine, ExplorerKey, "SmartScreenEnabled"), "Off", StringComparison.OrdinalIgnoreCase)
            && ReadDefaultDword(RegistryHive.CurrentUser, EdgeKey) == 0
            && ReadDword(RegistryHive.CurrentUser, AppHostKey, "EnableWebContentEvaluation") == 0;
    }

    /// <summary>跑一遍：<paramref name="disable"/> 为 true 是全部关闭，false 是全部恢复。</summary>
    public static bool Run(bool disable, out string report)
    {
        report = string.Empty;

        string id = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(Path.GetTempPath(), "nova-sec-" + id + ".ps1");
        string logPath = Path.Combine(Path.GetTempPath(), "nova-sec-" + id + ".log");

        try
        {
            File.WriteAllText(scriptPath, BuildScript(disable, logPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                report = "无法启动管理员 PowerShell";
                return false;
            }

            process.WaitForExit();
            report = ReadLog(logPath);

            // 退出码 = 失败条数（篡改防护那种「改不了」只记警告，不算失败）
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            report = "已取消提权";
            return false;
        }
        catch (Exception ex)
        {
            report = ex.Message;
            return false;
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(logPath);
        }
    }

    /// <summary>拼出给管理员 PowerShell 的脚本。</summary>
    internal static string BuildScript(bool disable, string logPath)
    {
        string onOff = disable ? "0" : "1";
        var builder = new StringBuilder();

        builder.AppendLine("$ErrorActionPreference = 'Continue'");
        builder.AppendLine("$log = '" + logPath.Replace("'", "''") + "'");
        builder.AppendLine("$failed = 0");
        builder.AppendLine("'== Artk Tools 安全中心 ==' | Out-File -FilePath $log -Encoding UTF8");
        builder.AppendLine(@"
function Log([string]$text) { $text | Out-File -Append -FilePath $log -Encoding UTF8 }
function Set-Dword([string]$p, [string]$n, [int]$v) {
  try {
    if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null }
    New-ItemProperty -Path $p -Name $n -Value $v -PropertyType DWord -Force | Out-Null
    Log ('OK   ' + $p + '\' + $n + ' = ' + $v)
  } catch {
    $script:failed++
    Log ('FAIL ' + $p + '\' + $n + ' : ' + $_.Exception.Message)
  }
}
function Set-Text([string]$p, [string]$n, [string]$v) {
  try {
    if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null }
    New-ItemProperty -Path $p -Name $n -Value $v -PropertyType String -Force | Out-Null
    Log ('OK   ' + $p + '\' + $n + ' = ' + $v)
  } catch {
    $script:failed++
    Log ('FAIL ' + $p + '\' + $n + ' : ' + $_.Exception.Message)
  }
}
function Set-DefaultDword([string]$p, [int]$v) {
  try {
    # 必须用可写句柄：PowerShell 提供程序给的 Get-Item 是只读的，SetValue 会报「无法写入到注册表项」
    $sub = $p
    if ($sub.StartsWith('HKCU:\')) { $sub = $sub.Substring(6) }
    $root = [Microsoft.Win32.Registry]::CurrentUser
    $key = $root.OpenSubKey($sub, $true)
    if ($null -eq $key) { $key = $root.CreateSubKey($sub) }
    if ($null -eq $key) { throw '打不开键' }
    $key.SetValue('', $v, [Microsoft.Win32.RegistryValueKind]::DWord)
    $key.Close()
    Log ('OK   ' + $p + '\(默认) = ' + $v)
  } catch {
    $script:failed++
    Log ('FAIL ' + $p + '\(默认) : ' + $_.Exception.Message)
  }
}
function Try-Remove([string]$p, [string]$n) {
  try {
    if (Test-Path $p) { Remove-ItemProperty -Path $p -Name $n -ErrorAction SilentlyContinue }
    Log ('OK   已删除 ' + $p + '\' + $n)
  } catch {
    Log ('WARN ' + $p + '\' + $n + ' : ' + $_.Exception.Message)
  }
}
");

        builder.AppendLine($"Set-Dword 'HKLM:\\{HvciKey}' 'Enabled' {onOff}");
        if (disable)
        {
            builder.AppendLine($"Try-Remove 'HKLM:\\{HvciKey}' 'WasEnabledBy'");
        }
        else
        {
            builder.AppendLine($"Set-Dword 'HKLM:\\{HvciKey}' 'WasEnabledBy' 2");
        }

        builder.AppendLine($"Set-Dword 'HKLM:\\{LsaKey}' 'RunAsPPL' {onOff}");
        builder.AppendLine($"Set-Dword 'HKLM:\\{LsaKey}' 'RunAsPPLBoot' {onOff}");
        builder.AppendLine($"Set-Dword 'HKLM:\\{CiConfigKey}' 'VulnerableDriverBlocklistEnable' {onOff}");
        builder.AppendLine($"Set-Dword 'HKLM:\\{CiPolicyKey}' 'VerifiedAndReputablePolicyState' {onOff}");
        builder.AppendLine($"Set-Text  'HKLM:\\{ExplorerKey}' 'SmartScreenEnabled' '{(disable ? "Off" : "Warn")}'");
        builder.AppendLine($"Set-DefaultDword 'HKCU:\\{EdgeKey}' {onOff}");
        builder.AppendLine($"Set-Dword 'HKCU:\\{AppHostKey}' 'EnableWebContentEvaluation' {onOff}");

        // ---- Defender：实时保护 / 云保护 / 样本提交 / 开发人员驱动器保护 ----
        builder.AppendLine("try {");
        builder.AppendLine(disable
            ? "  Set-MpPreference -DisableRealtimeMonitoring $true -MAPSReporting 0 -SubmitSamplesConsent 0 -ErrorAction Stop"
            : "  Set-MpPreference -DisableRealtimeMonitoring $false -MAPSReporting 2 -SubmitSamplesConsent 1 -ErrorAction Stop");
        builder.AppendLine(disable
            ? "  Log 'OK   Defender: 实时保护=关 云保护=关 自动提交样本=询问'"
            : "  Log 'OK   Defender: 实时保护=开 云保护=高级 自动提交样本=自动'");
        builder.AppendLine("} catch {");
        builder.AppendLine("  $script:failed++");
        builder.AppendLine("  Log ('FAIL Defender 偏好: ' + $_.Exception.Message + '（篡改防护开着时改不了）')");
        builder.AppendLine("}");

        // ---- 开发人员驱动器保护：实测落在这（1 = 同步扫描，保护开；0 = 异步扫描，性能模式）----
        builder.AppendLine($"Set-Dword 'HKLM:\\SOFTWARE\\Microsoft\\Windows Defender\\Real-Time Protection' 'DisableAsyncScanOnOpen' {(disable ? "0" : "1")}");

        // ---- 回读真实状态：脚本说 OK 不等于系统真的关掉了，界面按这几行判断 ----
        builder.AppendLine("try {");
        builder.AppendLine("  $cs = Get-MpComputerStatus -ErrorAction Stop");
        builder.AppendLine("  Log ('STATE 实时保护=' + $cs.RealTimeProtectionEnabled + ';篡改防护=' + $cs.IsTamperProtected)");
        builder.AppendLine("} catch { Log ('WARN 读不到 Defender 状态: ' + $_.Exception.Message) }");
        builder.AppendLine("try {");
        builder.AppendLine("  $mp = Get-MpPreference -ErrorAction Stop");
        builder.AppendLine("  Log ('STATE 云保护=' + $mp.MAPSReporting + ';样本提交=' + $mp.SubmitSamplesConsent + ';异步检查=' + $mp.AllowSwitchToAsyncInspection)");
        builder.AppendLine("} catch { }");
        builder.AppendLine("try {");
        builder.AppendLine("  $dd = (Get-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows Defender\\Real-Time Protection' -Name 'DisableAsyncScanOnOpen' -ErrorAction Stop).DisableAsyncScanOnOpen");
        builder.AppendLine("  Log ('STATE 开发人员驱动器保护=' + $dd)");
        builder.AppendLine("} catch { Log 'STATE 开发人员驱动器保护=<读不到>' }");

        builder.AppendLine("exit $failed");

        return builder.ToString();
    }

    private static int? ReadDword(RegistryHive hive, string path, string name)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
            object? value = key?.GetValue(name);
            return value is null ? null : Convert.ToInt32(value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadString(RegistryHive hive, string path, string name)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
            return key?.GetValue(name) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? ReadDefaultDword(RegistryHive hive, string path)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
            object? value = key?.GetValue(string.Empty);
            return value is null ? null : Convert.ToInt32(value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
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
            // 临时文件删不掉就算了
        }
    }
}
