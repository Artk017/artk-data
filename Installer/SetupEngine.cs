using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NovaSetup;

/// <summary>真正执行安装：释放内嵌的 Velopack Setup，静默安装到指定目录。
/// 快捷方式 / 启动等后续操作不在这里做，等用户在完成页点「确定」后调用 <see cref="ApplyPostInstall"/>。
/// 安装过程中通过 progress 汇报 0-100 的百分比，通过 detail 汇报 Velopack 的原始输出（进日志面板）。</summary>
internal static class SetupEngine
{
    public static int Run(InstallOptions o, Action<string>? log, CancellationToken token = default)
    {
        return Run(o, log, null, null, token);
    }

    public static int Run(
        InstallOptions o,
        Action<string>? log,
        IProgress<int>? progress = null,
        Action<string>? detail = null,
        CancellationToken token = default)
    {
        void Report(int percent) => progress?.Report(Math.Clamp(percent, 0, 100));

        string tmp = Path.Combine(Path.GetTempPath(), "nova-setup-" + Guid.NewGuid().ToString("N"));
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        try
        {
            if (token.IsCancellationRequested)
            {
                return 2;
            }

            Directory.CreateDirectory(tmp);
            string setupPath = Path.Combine(tmp, ProductInfo.Current.InnerSetupFileName);
            string setupLog = Path.Combine(tmp, "setup.log");

            log?.Invoke("正在准备安装文件…");
            Report(2);

            using (Stream? res = typeof(SetupEngine).Assembly.GetManifestResourceStream(ProductInfo.Current.SetupResourceName))
            {
                if (res == null)
                {
                    log?.Invoke("安装包损坏：缺少内置安装文件。");
                    return 3;
                }

                using var fs = new FileStream(setupPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                byte[] buffer = new byte[81920];
                long total = res.Length;
                long copied = 0;
                int read;

                while ((read = res.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (token.IsCancellationRequested)
                    {
                        return 2;
                    }

                    fs.Write(buffer, 0, read);
                    copied += read;

                    if (total > 0)
                    {
                        // 释放内嵌安装包占整条进度的 0-24%
                        Report(2 + (int)(22 * copied / total));
                    }
                }
            }

            string dir = NormalizeDir(o);
            Report(25);

            if (token.IsCancellationRequested)
            {
                return 2;
            }

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                log?.Invoke("无法创建安装目录：" + ex.Message);
                return 4;
            }

            // 覆盖安装时，正在运行的主程序 / Update.exe 会锁住文件，导致 Setup 退出码 1（重启后就好了）。
            // 这里先把它关掉再安装，省得用户自己重启；Artk Server 还要先停掉 SYSTEM 身份的计划任务服务。
            if (ProductInfo.Current.SupportsService)
            {
                ServiceInstaller.StopIfRunning(log);
            }

            log?.Invoke($"正在关闭正在运行的 {ProductInfo.Current.DisplayName}…");
            CloseRunningInstances(dir);
            Report(27);

            if (token.IsCancellationRequested)
            {
                return 2;
            }

            log?.Invoke($"正在安装到 {dir} …");

            // 配置（server.json / ui.json）和清单数据放在安装根目录，Velopack 升级会把整个根目录换掉，
            // 所以先备份，装完（不管成功失败）再放回去
            string? userData = BackupUserFiles(dir, tmp, log);
            Report(30);

            // 上一次安装失败时 Velopack 会把目录改名成「<目录>.<16位随机串>」留着回滚，这些残留会让下一次安装更容易失败
            CleanupRollbackLeftovers(dir, TimeSpan.FromMinutes(10), log);
            Report(32);

            var output = new StringBuilder();
            int code = RunInnerSetup(setupPath, dir, tmp, setupLog, token, output, detail, Report, seen);

            // 上一次安装被中断、或文件被杀软短暂占用时，清掉残留重试一次通常就能过
            if (code != 0 && code != 2 && !token.IsCancellationRequested)
            {
                log?.Invoke($"安装未成功（退出码 {code}），清理残留后重试一次…");
                CleanupRollbackLeftovers(dir, TimeSpan.Zero, log);
                Thread.Sleep(1500);
                code = RunInnerSetup(setupPath, dir, tmp, setupLog, token, output, detail, Report, seen);
            }

            Report(94);
            RestoreUserFiles(userData, dir, log);

            if (token.IsCancellationRequested)
            {
                log?.Invoke("已取消安装。");
                return 2;
            }

            if (code != 0)
            {
                string detailText = ReadFailureDetail(setupLog, output);
                string saved = SaveFailureLog(setupLog);

                string message = $"安装失败（退出码 {code}）。{(detailText.Length > 0 ? detailText : $"请确认目标目录可写、且没有正在运行的 {ProductInfo.Current.DisplayName}。")}";
                if (saved.Length > 0)
                {
                    message += $"（完整日志：{saved}）";
                }

                log?.Invoke(message);
                return code;
            }

            Report(100);
            log?.Invoke("安装完成。");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 2;
        }
        catch (Exception ex)
        {
            log?.Invoke("安装出错：" + ex.Message);
            return 1;
        }
        finally
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (Directory.Exists(tmp))
                    {
                        Directory.Delete(tmp, true);
                    }

                    break;
                }
                catch
                {
                    // 临时目录可能被子进程短暂占用，稍等再试
                    Thread.Sleep(250);
                }
            }
        }
    }

    /// <summary>
    /// 执行内嵌的 Velopack Setup，并把它的原始日志实时吐给 detail。
    /// 进度只能估算（Velopack 静默安装不报百分比）：按经过时间平滑逼近 92%，
    /// 真正结束时再跳到 94% / 100%，日志面板里能实时看到它到底做到哪一步。
    /// </summary>
    private static int RunInnerSetup(
        string setupPath,
        string dir,
        string tmp,
        string logFile,
        CancellationToken token,
        StringBuilder output,
        Action<string>? detail,
        Action<int> report,
        ConcurrentDictionary<string, byte> seen)
    {
        var psi = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = tmp,
        };
        psi.ArgumentList.Add("--silent");
        psi.ArgumentList.Add("--installto");
        psi.ArgumentList.Add(dir);
        psi.ArgumentList.Add("--log");
        psi.ArgumentList.Add(logFile);

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return 5;
        }

        // 取消时直接结束子安装进程（连同它的子进程）
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 已退出 / 无权限都忽略
            }
        });

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }

                Forward(detail, seen, e.Data);
            }
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }

                Forward(detail, seen, e.Data);
            }
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var clock = Stopwatch.StartNew();
        int logLines = 0;

        while (!proc.HasExited)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            Thread.Sleep(250);

            PumpLogFile(logFile, ref logLines, detail, seen);

            // 33% → 92%：18 秒的时间常数，长时间安装也还在缓慢前进
            double seconds = clock.Elapsed.TotalSeconds;
            report(33 + (int)(59 * (1 - Math.Exp(-seconds / 18.0))));
        }

        proc.WaitForExit();
        PumpLogFile(logFile, ref logLines, detail, seen);

        if (token.IsCancellationRequested)
        {
            return 2;
        }

        return proc.ExitCode;
    }

    /// <summary>把 setup.log 里新出现的行转发到日志面板（Velopack 的真实进度都在这个文件里）。</summary>
    private static void PumpLogFile(
        string file,
        ref int startLine,
        Action<string>? detail,
        ConcurrentDictionary<string, byte> seen)
    {
        try
        {
            if (!File.Exists(file))
            {
                return;
            }

            string text;

            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                text = reader.ReadToEnd();
            }

            if (text.Length == 0)
            {
                return;
            }

            string[] lines = text.Replace("\r", string.Empty).Split('\n');

            // 最后一行可能还在写入，先不处理，下一轮再补
            int complete = lines.Length;

            if (!text.EndsWith('\n'))
            {
                complete--;
            }

            if (complete < startLine)
            {
                startLine = 0;
            }

            for (int i = startLine; i < complete; i++)
            {
                Forward(detail, seen, lines[i]);
            }

            if (complete > startLine)
            {
                startLine = complete;
            }
        }
        catch
        {
            // 日志文件被占用 / 不存在都无所谓
        }
    }

    private static void Forward(Action<string>? detail, ConcurrentDictionary<string, byte> seen, string line)
    {
        if (detail == null)
        {
            return;
        }

        string text = line.Trim();

        if (text.Length == 0)
        {
            return;
        }

        // stdout 和 setup.log 会有重复行，转发一次就够了
        if (!seen.TryAdd(text, 0))
        {
            return;
        }

        if (seen.Count > 4000)
        {
            seen.Clear();
        }

        detail(text);
    }

    /// <summary>
    /// 安装根目录里的用户数据（server.json / ui.json / data 之类）先备份到临时目录，
    /// 因为 Velopack 升级会把整个根目录替换掉。
    /// </summary>
    private static string? BackupUserFiles(string dir, string tmp, Action<string>? log)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return null;
            }

            string backup = Path.Combine(tmp, "userdata");
            Directory.CreateDirectory(backup);

            string leaf = Path.GetFileName(dir);
            int copied = 0;

            foreach (string entry in Directory.GetFileSystemEntries(dir))
            {
                string name = Path.GetFileName(entry);

                // Velopack 自己管理的东西不备份
                if (name.Equals("current", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("packages", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(leaf + ".", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    string dest = Path.Combine(backup, name);
                    if (Directory.Exists(entry))
                    {
                        CopyDirectory(entry, dest);
                    }
                    else
                    {
                        File.Copy(entry, dest, true);
                    }

                    copied++;
                }
                catch
                {
                    // 单个文件被占用就跳过，别让备份失败拦住安装
                }
            }

            if (copied > 0)
            {
                log?.Invoke($"已备份原有配置：{copied} 项");
            }

            return backup;
        }
        catch (Exception ex)
        {
            log?.Invoke("备份原有配置失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>安装结束后把备份的用户数据放回安装根目录。</summary>
    private static void RestoreUserFiles(string? backup, string dir, Action<string>? log)
    {
        if (backup is null || !Directory.Exists(backup) || !Directory.Exists(dir))
        {
            return;
        }

        int restored = 0;

        try
        {
            foreach (string entry in Directory.GetFileSystemEntries(backup))
            {
                string dest = Path.Combine(dir, Path.GetFileName(entry));

                if (Directory.Exists(entry))
                {
                    CopyDirectory(entry, dest);
                }
                else
                {
                    File.Copy(entry, dest, true);
                }

                restored++;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("恢复原有配置失败：" + ex.Message);
            return;
        }

        if (restored > 0)
        {
            log?.Invoke($"已恢复原有配置：{restored} 项");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }

        foreach (string sub in Directory.GetDirectories(source))
        {
            CopyDirectory(sub, Path.Combine(target, Path.GetFileName(sub)));
        }
    }

    /// <summary>删掉 Velopack 安装失败后留下的回滚目录：&lt;安装目录&gt;.&lt;16 位随机串&gt;。</summary>
    private static void CleanupRollbackLeftovers(string dir, TimeSpan minAge, Action<string>? log)
    {
        try
        {
            string parent = Path.GetDirectoryName(dir) ?? string.Empty;
            string leaf = Path.GetFileName(dir);

            if (parent.Length == 0 || leaf.Length == 0 || !Directory.Exists(parent))
            {
                return;
            }

            foreach (string candidate in Directory.GetDirectories(parent))
            {
                string name = Path.GetFileName(candidate);

                if (name.Length != leaf.Length + 17 || !name.StartsWith(leaf + ".", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string suffix = name[(leaf.Length + 1)..];
                bool rollbackLike = suffix.All(char.IsLetterOrDigit);

                if (!rollbackLike)
                {
                    continue;
                }

                if (minAge > TimeSpan.Zero && DateTime.Now - Directory.GetLastWriteTime(candidate) < minAge)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(candidate, true);
                    log?.Invoke($"已清理上次安装的残留目录：{name}");
                }
                catch
                {
                    // 还被占用就留着，不影响本次安装
                }
            }
        }
        catch
        {
            // 列目录失败不拦安装
        }
    }

    /// <summary>Velopack 把真正的失败原因写在 [ERROR] 行里，优先取它，别把整段日志糊给用户。</summary>
    private static string ReadFailureDetail(string logFile, StringBuilder stdout)
    {
        string text = stdout.ToString().Trim();

        try
        {
            if (File.Exists(logFile))
            {
                text = File.ReadAllText(logFile);
            }
        }
        catch
        {
            // 读不到就用 stdout
        }

        var errors = new List<string>();

        foreach (string line in text.Replace("\r", string.Empty).Split('\n'))
        {
            if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(line.Trim());
            }
        }

        if (errors.Count > 0)
        {
            return string.Join(" ", errors.Count > 2 ? errors.GetRange(errors.Count - 2, 2) : errors);
        }

        if (text.Length > 500)
        {
            text = text[^500..];
        }

        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>把失败的安装日志留一份，方便用户直接发过来。</summary>
    private static string SaveFailureLog(string logFile)
    {
        try
        {
            if (!File.Exists(logFile))
            {
                return string.Empty;
            }

            string target = Path.Combine(Path.GetTempPath(), "artk-setup-error.log");
            File.Copy(logFile, target, true);
            return target;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>安装成功后（用户点「确定」时）才执行：创建/删除桌面快捷方式、按需注册服务、启动程序。</summary>
    public static void ApplyPostInstall(InstallOptions o, Action<string>? log = null)
    {
        string dir = NormalizeDir(o);
        string exe = Path.Combine(dir, "current", ProductInfo.Current.MainExeFileName);

        try
        {
            log?.Invoke("正在处理快捷方式…");

            // 改名前留下的 Nova 快捷方式先清掉，免得桌面 / 开始菜单里同时出现两个名字
            Shortcuts.RemoveLegacyShortcuts();

            if (o.CreateDesktopShortcut)
            {
                Shortcuts.EnsureDesktopShortcut(dir, exe);
            }
            else
            {
                Shortcuts.RemoveDesktopShortcuts(dir);
            }
        }
        catch
        {
            // 快捷方式处理失败不影响安装结果
        }

        // Artk Server：可选把后台服务（开机自启计划任务 + 防火墙）一起装上，需要管理员权限
        if (o.RegisterService && ProductInfo.Current.SupportsService)
        {
            ServiceInstaller.Register(dir, log);
        }

        if (!o.LaunchAfterInstall || !File.Exists(exe))
        {
            return;
        }

        log?.Invoke($"正在启动 {ProductInfo.Current.DisplayName}…");
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
        }
        catch
        {
            // 启动失败忽略
        }
    }

    public static string NormalizeDir(InstallOptions o)
    {
        string dir = (o.InstallDir ?? string.Empty).Trim().TrimEnd('\\', '/');
        return dir.Length == 0 ? InstallOptions.DefaultInstallDir() : InstallOptions.NormalizeProductDir(dir);
    }

    /// <summary>结束安装目录下正在运行的主程序 / Update.exe，以及常驻的 adb / fastboot，
    /// 避免文件被占用（adb server 会一直锁着安装目录，导致 Velopack 无法重命名目录）。</summary>
    private static void CloseRunningInstances(string dir)
    {
        string prefix = dir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        int self = Environment.ProcessId;

        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self)
                {
                    continue;
                }

                string? path = null;
                try
                {
                    path = p.MainModule?.FileName;
                }
                catch
                {
                    // 其它用户的进程读不到路径
                }

                string name = string.IsNullOrEmpty(path) ? p.ProcessName + ".exe" : Path.GetFileName(path);

                bool isAdb = name.Equals("adb.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("fastboot.exe", StringComparison.OrdinalIgnoreCase);

                if (isAdb)
                {
                    // adb server 是常驻进程，还可能继承 Artk Tools 的目录句柄，一直锁住安装目录。
                    // 直接结束，需要时 adb 会自己重新拉起。
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                    continue;
                }

                if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool known = ProductInfo.Current.RunningExeNames
                    .Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase));

                if (!known)
                {
                    continue;
                }

                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch
            {
                // 其它用户的进程 / 已退出，忽略
            }
        }

        // 给文件句柄一点释放时间
        Thread.Sleep(500);
    }

    // ---------------- 读取进程当前工作目录（用于识别锁住安装目录的 adb） ----------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int cls, ref ProcessBasicInformation info, int len, out int ret);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2a;
        public IntPtr Reserved2b;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    private static string? TryGetWorkingDirectory(int pid)
    {
        IntPtr h = OpenProcess(0x0400 | 0x0010, false, pid);
        if (h == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0)
            {
                return null;
            }

            byte[] pointer = new byte[8];
            if (!ReadProcessMemory(h, pbi.PebBaseAddress + 0x20, pointer, pointer.Length, out _))
            {
                return null;
            }

            IntPtr parameters = (IntPtr)BitConverter.ToInt64(pointer, 0);
            if (parameters == IntPtr.Zero)
            {
                return null;
            }

            // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory（x64 偏移 0x38）
            byte[] dir = new byte[0x50];
            if (!ReadProcessMemory(h, parameters + 0x38, dir, dir.Length, out _))
            {
                return null;
            }

            ushort length = BitConverter.ToUInt16(dir, 0);
            IntPtr buffer = (IntPtr)BitConverter.ToInt64(dir, 8);
            if (length == 0 || length > 2048 || buffer == IntPtr.Zero)
            {
                return null;
            }

            byte[] text = new byte[length];
            if (!ReadProcessMemory(h, buffer, text, text.Length, out _))
            {
                return null;
            }

            return Encoding.Unicode.GetString(text);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
