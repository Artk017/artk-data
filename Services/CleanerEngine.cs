using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ArtkTools.Services;

/// <summary>
/// 两阶段清理：
/// 扫描：只遍历 FileKey / RegKey 生成删除清单，不碰任何文件；被占用的文件会被跳过。
/// 清理：按清单执行删除，失败的文件静默跳过。
/// </summary>
public sealed class CleanerEngine
{
    private readonly CleanerPathExpander _expander = new();

    // ---------------- 扫描 ----------------

    public Task<ScanResult> AnalyzeAsync(CleanerEntry entry, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(() => Analyze(entry, progress, token), token);

    private ScanResult Analyze(CleanerEntry entry, IProgress<string>? progress, CancellationToken token)
    {
        var result = new ScanResult { Entry = entry };
        var fileExclusions = BuildFileExclusions(entry);
        var registryExclusions = BuildRegistryExclusions(entry);

        IProgress<string>? entryProgress = progress is null
            ? null
            : new PrefixedProgress(entry.Name, progress);

        // 同一项目里多个 FileKey 可能命中同一个文件，用集合去重
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileKey in entry.FileKeys)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                foreach (var file in FindFiles(fileKey, fileExclusions, entryProgress, token))
                {
                    if (!seen.Add(file))
                    {
                        continue;
                    }

                    // 被独占占用 / 无权限的文件不计入可清理体积
                    var size = TryGetDeletableSize(file);
                    if (size < 0)
                    {
                        continue;
                    }

                    result.FilesToDelete.Add(file);
                    result.TotalBytes += size;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单个 FileKey 出错不影响其他规则
            }
        }

        foreach (var regKey in entry.RegKeys)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                result.RegistryToDelete.AddRange(FindRegistryItems(regKey, registryExclusions));
            }
            catch
            {
            }
        }

        return result;
    }

    private IEnumerable<string> FindFiles(FileKeyEntry fileKey, List<ExclusionRule> excluded, IProgress<string>? progress, CancellationToken token)
    {
        bool recurse = fileKey.Flag is FileKeyFlag.Recurse or FileKeyFlag.RemoveSelf;

        var patterns = fileKey.Pattern
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dir in _expander.ResolvePaths(fileKey.Path))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in EnumerateFilesSafe(dir, patterns, excluded, recurse, progress, token))
            {
                if (!IsExcluded(file, excluded) && !IsProtected(file))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string[] patterns, List<ExclusionRule> excluded, bool recurse, IProgress<string>? progress, CancellationToken token)
    {
        // 整棵目录已被排除时，在进入前直接跳过
        var scanRoot = root.TrimEnd('\\') + "\\";
        if (excluded.Any(rule => rule.Pattern is null && scanRoot.StartsWith(rule.DirPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            yield break;
        }

        progress?.Report(root);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns)
        {
            List<string> files;
            try
            {
                // 先落成集合再遍历：枚举过程中的 IO 异常才能在这里接住
                files = Directory.GetFiles(root, pattern).ToList();
            }
            catch
            {
                files = new List<string>();
            }

            foreach (var file in files)
            {
                if (seen.Add(file))
                {
                    yield return file;
                }
            }
        }

        if (!recurse)
        {
            yield break;
        }

        List<string> dirs;
        try
        {
            // 跳过目录联接 / 符号链接，避免 C:\Users\All Users -> C:\ProgramData 这类无限循环
            dirs = Directory.EnumerateDirectories(root)
                            .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
                            .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var sub in dirs)
        {
            token.ThrowIfCancellationRequested();

            foreach (var file in EnumerateFilesSafe(sub, patterns, excluded, recurse: true, progress, token))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<RegistryItemToDelete> FindRegistryItems(RegKeyEntry regKey, List<string> exclusions)
    {
        if (IsRegistryPathExcluded(regKey.KeyPath, exclusions))
        {
            yield break;
        }

        var (hive, subKey) = SplitHiveSubKey(regKey.KeyPath);
        using var root = CleanerRegistry.OpenHive(hive);
        if (root is null)
        {
            yield break;
        }

        using var key = root.OpenSubKey(subKey, writable: false);
        if (key is null)
        {
            yield break;
        }

        if (regKey.ValueName is not null)
        {
            if (key.GetValue(regKey.ValueName) is not null)
            {
                yield return new RegistryItemToDelete { KeyPath = regKey.KeyPath, ValueName = regKey.ValueName };
            }
        }
        else
        {
            yield return new RegistryItemToDelete { KeyPath = regKey.KeyPath };
        }
    }

    // ---------------- 清理 ----------------

    public Task<(int Count, long Bytes)> CleanAsync(ScanResult result, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(() => Clean(result, progress, token), token);

    private (int Count, long Bytes) Clean(ScanResult result, IProgress<string>? progress, CancellationToken token)
    {
        int count = 0;
        long bytes = 0;
        var registryExclusions = BuildRegistryExclusions(result.Entry);

        foreach (var file in result.FilesToDelete)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                count++;
                bytes += size;
                progress?.Report(file);
            }
            catch
            {
                // 文件被占用或已经不存在，跳过
            }
        }

        foreach (var item in result.RegistryToDelete)
        {
            try
            {
                if (DeleteRegistryItem(item, registryExclusions))
                {
                    count++;
                }
            }
            catch
            {
            }
        }

        // REMOVESELF：删掉因此变空的目录
        foreach (var fileKey in result.Entry.FileKeys.Where(fk => fk.Flag == FileKeyFlag.RemoveSelf))
        {
            foreach (var resolved in _expander.ResolvePaths(fileKey.Path))
            {
                TryPruneEmptyDirs(resolved);
            }
        }

        return (count, bytes);
    }

    private static bool DeleteRegistryItem(RegistryItemToDelete item, List<string> exclusions)
    {
        var itemPath = NormalizeRegistryPath(item.KeyPath);
        if (IsRegistryPathExcluded(itemPath, exclusions))
        {
            return false;
        }

        var (hive, subKey) = SplitHiveSubKey(item.KeyPath);
        using var root = CleanerRegistry.OpenHive(hive);
        if (root is null)
        {
            return false;
        }

        if (item.ValueName is not null)
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return false;
            }

            key.DeleteValue(item.ValueName, throwOnMissingValue: false);
            return true;
        }

        using (var key = root.OpenSubKey(subKey, writable: false))
        {
            if (key is null)
            {
                return false;
            }
        }

        // 有被排除的子键时，逐支清理，保护这些键
        var protectedKeys = exclusions
            .Where(path => IsSameOrChild(path, itemPath) && RegistryKeyExists(path))
            .ToList();

        if (protectedKeys.Count == 0)
        {
            var parentSubKey = Path.GetDirectoryName(subKey)?.Replace('/', '\\') ?? "";
            var keyName = Path.GetFileName(subKey);

            using var parent = root.OpenSubKey(parentSubKey, writable: true);
            if (parent is null)
            {
                return false;
            }

            parent.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
            return true;
        }

        using var target = root.OpenSubKey(subKey, writable: true);
        if (target is null)
        {
            return false;
        }

        DeleteRegistryTreeExcept(target, itemPath, protectedKeys);
        return true;
    }

    private static void DeleteRegistryTreeExcept(RegistryKey key, string keyPath, List<string> exclusions)
    {
        foreach (var valueName in key.GetValueNames())
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            var childPath = keyPath + "\\" + subKeyName;

            if (IsRegistryPathExcluded(childPath, exclusions))
            {
                continue;
            }

            if (exclusions.Any(path => IsSameOrChild(path, childPath)))
            {
                using var child = key.OpenSubKey(subKeyName, writable: true);
                if (child is not null)
                {
                    DeleteRegistryTreeExcept(child, childPath, exclusions);
                }
            }
            else
            {
                key.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
            }
        }
    }

    /// <summary>由深到浅删除空目录，最后连根目录也尝试删掉。</summary>
    private static void TryPruneEmptyDirs(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var sub in Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                                         .OrderByDescending(d => d.Length))
            {
                if (Directory.GetFileSystemEntries(sub).Length == 0)
                {
                    Directory.Delete(sub);
                }
            }

            if (Directory.GetFileSystemEntries(path).Length == 0)
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    // ---------------- 辅助 ----------------

    /// <summary>把 ExcludeKey 转成扫描时可匹配的规则；REG 类型不参与文件扫描。</summary>
    private List<ExclusionRule> BuildFileExclusions(CleanerEntry entry)
    {
        var rules = new List<ExclusionRule>();

        foreach (var ex in entry.ExcludeKeys)
        {
            if (ex.Type is ExcludeType.Reg)
            {
                continue;
            }

            foreach (var p in _expander.ResolvePaths(ex.Path))
            {
                rules.Add(new ExclusionRule(p.TrimEnd('\\') + "\\", ex.Pattern));
            }
        }

        return rules;
    }

    private static List<string> BuildRegistryExclusions(CleanerEntry entry)
    {
        return entry.ExcludeKeys
            .Where(ex => ex.Type == ExcludeType.Reg)
            .Select(ex => NormalizeRegistryPath(ex.Path))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsRegistryPathExcluded(string path, List<string> exclusions)
    {
        var normalized = NormalizeRegistryPath(path);
        return exclusions.Any(excluded => IsSameOrChild(normalized, excluded));
    }

    private static bool IsSameOrChild(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool RegistryKeyExists(string path)
    {
        var (hive, subKey) = SplitHiveSubKey(path);
        using var root = CleanerRegistry.OpenHive(hive);
        using var key = root?.OpenSubKey(subKey, writable: false);
        return key is not null;
    }

    private static string NormalizeRegistryPath(string path)
    {
        var (hive, subKey) = SplitHiveSubKey(path.Trim().TrimEnd('\\'));

        hive = hive switch
        {
            "HKEY_CURRENT_USER" => "HKCU",
            "HKEY_LOCAL_MACHINE" => "HKLM",
            "HKEY_USERS" => "HKU",
            "HKEY_CURRENT_CONFIG" => "HKCC",
            "HKEY_CLASSES_ROOT" => "HKCR",
            _ => hive
        };

        return subKey.Length == 0 ? hive : hive + "\\" + subKey.Trim('\\');
    }

    /// <summary>
    /// 以 DELETE 权限试探文件能否删除；被其他进程独占时提前跳过，
    /// 避免把注定删不掉的文件算进可清理体积。
    /// </summary>
    private static long TryGetDeletableSize(string path)
    {
        const uint DELETE = 0x00010000;
        const uint FILE_SHARE_ALL = 0x7;
        const uint OPEN_EXISTING = 3;

        using var handle = CreateFileW(path, DELETE, FILE_SHARE_ALL,
                                       IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return -1;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsExcluded(string path, List<ExclusionRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule.Matches(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 内置安全网：无论规则怎么写都不删的路径。
    /// 主要保护浏览器扩展的 IndexedDB，以及 QQ / 微信的聊天记录目录。
    /// </summary>
    private static readonly string[] ProtectedSegments =
    {
        @"\IndexedDB\chrome-extension_",
        @"\Documents\Tencent Files",
        @"\Documents\WeChat Files",
        @"\Documents\xwechat_files",
        @"\Documents\WXWork",
    };

    private static bool IsProtected(string path) =>
        ProtectedSegments.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static (string Hive, string SubKey) SplitHiveSubKey(string path)
    {
        var idx = path.IndexOf('\\');
        return idx < 0 ? (path.ToUpperInvariant(), "") : (path[..idx].ToUpperInvariant(), path[(idx + 1)..]);
    }

    // ---------------- 嵌套类型 ----------------

    /// <summary>一条 ExcludeKey 规则；DirPrefix 总以 \ 结尾，避免 Cache\ 误伤 CacheExtra\。</summary>
    private readonly record struct ExclusionRule(string DirPrefix, string? Pattern)
    {
        public bool Matches(string filePath)
        {
            if (!filePath.StartsWith(DirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Pattern is null)
            {
                return true;
            }

            if (Pattern.Contains('*') || Pattern.Contains('?'))
            {
                var fileName = Path.GetFileName(filePath);
                return FileSystemName.MatchesSimpleExpression(Pattern, fileName, ignoreCase: true);
            }

            var relativePath = filePath[DirPrefix.Length..];
            return relativePath.Equals(Pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class PrefixedProgress(string prefix, IProgress<string> inner) : IProgress<string>
    {
        public void Report(string path) => inner.Report($"{prefix}  ›  {path}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
