using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinUIDemo.Services;

/// <summary>
/// 一键 KMS 激活：让管理员 PowerShell 依次执行 4 条 slmgr 命令，每条之间停 0.5 秒：
/// <c>/ckms</c> → <c>/ipk</c> → <c>/skms</c> → <c>/ato</c>。
/// <para>
/// 命令写在临时 .ps1 里再以管理员身份跑，避免一层层转义引号；输出重定向到日志文件，
/// 回来读出来给界面显示。脚本执行完自己删掉。
/// </para>
/// </summary>
public static class KmsActivation
{
    /// <summary>要装的产品密钥（Windows 10/11 专业版 KMS 客户端密钥）。</summary>
    private const string ProductKey = "W269N-WFGWX-YVC9B-4J6C9-T83GX";

    /// <summary>KMS 服务器。</summary>
    private const string KmsHost = "kms.03k.org";

    /// <summary>两条命令之间的间隔。</summary>
    private const int StepDelayMilliseconds = 500;

    /// <summary>要依次执行的 slmgr 参数，顺序即执行顺序。</summary>
    internal static string[] Steps { get; } =
    {
        "/ckms",
        "/ipk " + ProductKey,
        "/skms " + KmsHost,
        "/ato",
    };

    /// <summary>
    /// 跑一遍 KMS 激活。返回是否全部命令成功，<paramref name="report"/> 是命令输出（失败时给界面看）。
    /// </summary>
    public static bool Run(out string report)
    {
        report = string.Empty;

        string id = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(Path.GetTempPath(), "nova-kms-" + id + ".ps1");
        string logPath = Path.Combine(Path.GetTempPath(), "nova-kms-" + id + ".log");

        try
        {
            File.WriteAllText(scriptPath, BuildScript(logPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

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

            // 脚本的退出码 = 失败的命令条数
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // 用户在 UAC 上点了「否」
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
    internal static string BuildScript(string logPath)
    {
        var builder = new StringBuilder();

        builder.AppendLine("$ErrorActionPreference = 'Continue'");
        builder.AppendLine("$log = '" + logPath.Replace("'", "''") + "'");
        builder.AppendLine("$failed = 0");
        builder.AppendLine("'== Artk Tools KMS 激活 ==' | Out-File -FilePath $log -Encoding UTF8");
        builder.AppendLine("$slmgr = Join-Path $env:SystemRoot 'System32\\slmgr.vbs'");
        builder.AppendLine("$steps = @(");
        for (int i = 0; i < Steps.Length; i++)
        {
            // 最后一项不能带尾逗号：@('a', 'b',) 在 PowerShell 里是语法错误
            string separator = i < Steps.Length - 1 ? "," : string.Empty;
            builder.AppendLine("  '" + Steps[i].Replace("'", "''") + "'" + separator);
        }

        builder.AppendLine(")");
        builder.AppendLine("foreach ($step in $steps) {");
        builder.AppendLine("  $output = (cscript //nologo $slmgr $step.Split(' ') *>&1 | Out-String).Trim()");
        builder.AppendLine("  if ($LASTEXITCODE -ne 0) { $failed++ }");
        builder.AppendLine("  ('> slmgr ' + $step) | Out-File -Append -FilePath $log -Encoding UTF8");
        builder.AppendLine("  $output | Out-File -Append -FilePath $log -Encoding UTF8");
        builder.AppendLine("  Start-Sleep -Milliseconds " + StepDelayMilliseconds);
        builder.AppendLine("}");
        builder.AppendLine("exit $failed");

        return builder.ToString();
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
