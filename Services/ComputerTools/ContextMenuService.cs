using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace ArtkTools.Services.ComputerTools;

/// <summary>一条右键菜单项（shell 命令 / COM 右键扩展 / 命令仓库），带场景、来源软件与图标。</summary>
public sealed class ContextMenuEntry : INotifyPropertyChanged
{
    public required string Id { get; init; }

    /// <summary>场景分类：所有文件 / 文件夹 / 桌面背景 / 磁盘 / 文件类型 .xxx …（与参考实现一致）。</summary>
    public required string Scene { get; init; }

    public string Name { get; set; } = "";

    /// <summary>注册表里的原始名称，用于友好名判断与搜索。</summary>
    public string RawName { get; init; } = "";

    /// <summary>Shell 命令 / Shell 扩展 / 命令仓库。</summary>
    public required string Type { get; init; }

    /// <summary>当前用户 / 所有用户 + 32 位 / 64 位。</summary>
    public required string Scope { get; init; }

    public string Command { get; init; } = "";

    /// <summary>注册表 Icon 值（可能带 ",序号"）。</summary>
    public string IconValue { get; init; } = "";

    public string Clsid { get; init; } = "";

    public string SubCommands { get; init; } = "";

    /// <summary>禁用时写入的值的名称：shell 命令为 LegacyDisable / ProgrammaticAccessOnly，扩展为 CLSID。</summary>
    public string DisableValueName { get; init; } = "LegacyDisable";

    public required string Hive { get; init; }

    /// <summary>Registry64 / Registry32。</summary>
    public required string View { get; init; }

    public required string SubKey { get; init; }

    public bool Enabled { get; set; }

    public bool RequiresAdmin { get; init; }

    public bool ReadOnly { get; init; }

    public string ReadOnlyReason { get; init; } = "";

    /// <summary>命令仓库 / 文件类型等默认不展示的技术项。</summary>
    public bool AdvancedOnly { get; init; }

    public bool UserAdded { get; init; }

    /// <summary>来源软件名（已做中文归一化）。</summary>
    public string SoftwareName { get; set; } = "";

    public string Vendor { get; set; } = "";

    public bool IsThirdParty { get; set; }

    public bool PresentationResolved { get; set; }

    /// <summary>最终用于提取图标的文件。</summary>
    public string IconPath { get; set; } = "";

    public int IconIndex { get; set; }

    public bool IconLoaded { get; set; }

    private ImageSource? _iconDisplay;

    /// <summary>页面水合后填入的软件图标（解析不到时为空，界面回退到首字占位）。</summary>
    public ImageSource? IconDisplay
    {
        get => _iconDisplay;
        set
        {
            if (ReferenceEquals(_iconDisplay, value))
            {
                return;
            }

            _iconDisplay = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconDisplay)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsShellExtension => Type == "Shell 扩展";

    public bool CanToggle => !ReadOnly;

    public Visibility DeleteVisibility
        => ReadOnly || AdvancedOnly ? Visibility.Collapsed : Visibility.Visible;

    public string RegistryPath => $"{Hive}\\{SubKey}";

    public string StateText => Enabled ? "已启用" : "已禁用";

    /// <summary>列表副标题：来源软件 · 场景 · 范围。</summary>
    public string DetailText
    {
        get
        {
            string software = SoftwareName.Length > 0 ? SoftwareName : "来源未确认";

            return $"{software} · {Scene} · {Scope}";
        }
    }

    /// <summary>图标不可用时的首字（用于占位方块）。</summary>
    public string Initial
    {
        get
        {
            if (Name.Length == 0)
            {
                return "?";
            }

            char first = Name[0];

            if (char.IsHighSurrogate(first) && Name.Length > 1 && char.IsLowSurrogate(Name[1]))
            {
                return Name[..2];
            }

            // 孤立代理项会让 DWrite 直接崩溃，直接退化成问号
            return char.IsSurrogate(first) ? "?" : first.ToString();
        }
    }
}

/// <summary>
/// 右键菜单管理：按 tubatools-master 的扫描规则枚举 HKCU / HKLM 的 64 / 32 位视图，
/// 覆盖 21 个 shell / shellex 入口、命令仓库与文件类型，解析菜单名称、来源软件和图标。
/// </summary>
public static class ContextMenuService
{
    private sealed class RootDefinition
    {
        public required string Scene { get; init; }

        public required string Path { get; init; }

        public required string Type { get; init; }
    }

    private static RootDefinition Root(string scene, string path, string type)
        => new() { Scene = scene, Path = path, Type = type };

    private static readonly RootDefinition[] Roots =
    {
        Root("所有文件", @"Software\Classes\*\shell", "Shell 命令"),
        Root("所有文件", @"Software\Classes\*\shellex\ContextMenuHandlers", "Shell 扩展"),
        Root("所有文件系统对象", @"Software\Classes\AllFilesystemObjects\shell", "Shell 命令"),
        Root("所有文件系统对象", @"Software\Classes\AllFilesystemObjects\shellex\ContextMenuHandlers", "Shell 扩展"),
        Root("文件夹", @"Software\Classes\Directory\shell", "Shell 命令"),
        Root("文件夹", @"Software\Classes\Directory\shellex\ContextMenuHandlers", "Shell 扩展"),
        Root("文件夹背景", @"Software\Classes\Directory\Background\shell", "Shell 命令"),
        Root("文件夹背景", @"Software\Classes\Directory\Background\shellex\ContextMenuHandlers", "Shell 扩展"),
        Root("桌面背景", @"Software\Classes\DesktopBackground\shell", "Shell 命令"),
        Root("桌面背景", @"Software\Classes\DesktopBackground\shellex\ContextMenuHandlers", "Shell 扩展"),
        Root("磁盘", @"Software\Classes\Drive\shell", "Shell 命令"),
        Root("磁盘", @"Software\Classes\Drive\shellex\ContextMenuHandlers", "Shell 扩展"),
        Root("磁盘拖放", @"Software\Classes\Drive\shellex\DragDropHandlers", "Shell 扩展"),
        Root("文件夹对象", @"Software\Classes\Folder\shell", "Shell 命令"),
        Root("文件夹对象", @"Software\Classes\Folder\shellex\ContextMenuHandlers", "Shell 扩展"),
        Root("文件夹拖放", @"Software\Classes\Folder\shellex\DragDropHandlers", "Shell 扩展"),
        Root("快捷方式", @"Software\Classes\lnkfile\shell", "Shell 命令"),
        Root("快捷方式", @"Software\Classes\lnkfile\shellex\ContextMenuHandlers", "Shell 扩展"),
        Root("可执行文件", @"Software\Classes\exefile\shell", "Shell 命令"),
        Root("可执行文件", @"Software\Classes\exefile\shellex\ContextMenuHandlers", "Shell 扩展"),
        Root("未知文件", @"Software\Classes\Unknown\shell", "Shell 命令"),
    };

    private const string CommandStoreRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";

    private const string BlockedKey = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    /// <summary>场景固定排序：贴近用户感知的右键位置；未列出的场景（文件类型）排到末尾。</summary>
    public static readonly string[] SceneOrder =
    {
        "所有文件", "所有文件系统对象", "可执行文件", "快捷方式", "未知文件",
        "文件夹", "文件夹对象", "文件夹拖放", "文件夹背景", "桌面背景",
        "磁盘", "磁盘拖放", "命令仓库",
        "文件右键", "文件夹右键", "文件夹空白处右键", "磁盘右键", "文件资源管理器右键",
    };

    public static int SceneRank(string scene)
    {
        int index = Array.IndexOf(SceneOrder, scene);

        return index < 0 ? 1000 : index;
    }

    /// <summary>主列表默认显示：已识别第三方 + 用户自建；系统内置、未识别与技术记录默认隐藏。</summary>
    public static bool MatchesMainMenuList(ContextMenuEntry entry)
        => !entry.AdvancedOnly && entry.PresentationResolved && (entry.IsThirdParty || entry.UserAdded);

    /// <summary>搜索关键字匹配（名称 / 软件 / 场景 / 范围 / 命令 / 位置 / CLSID）。</summary>
    public static bool MatchesKeyword(ContextMenuEntry entry, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        return Matches(entry.Name, keyword) || Matches(entry.RawName, keyword)
            || Matches(entry.SoftwareName, keyword) || Matches(entry.Scene, keyword)
            || Matches(entry.Scope, keyword) || Matches(entry.Command, keyword)
            || Matches(entry.Clsid, keyword) || Matches(entry.SubKey, keyword);
    }

    private static bool Matches(string? value, string keyword)
        => value is not null && value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint outputCount, IntPtr reserved);

    public static Task<IReadOnlyList<ContextMenuEntry>> ScanAsync() => Task.Run(Scan);

    public static IReadOnlyList<ContextMenuEntry> Scan()
    {
        var entries = new List<ContextMenuEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string hive in new[] { "HKCU", "HKLM" })
        {
            foreach (string view in new[] { "Registry64", "Registry32" })
            {
                foreach (RootDefinition root in Roots)
                {
                    EnumerateRoot(hive, view, root, entries, seen);
                }

                EnumerateRoot(hive, view, Root("命令仓库", CommandStoreRoot, "命令仓库"), entries, seen, advancedOnly: true);
                EnumerateFileTypes(hive, view, entries, seen);
            }
        }

        foreach (ContextMenuEntry entry in entries)
        {
            ApplyPresentation(entry);
        }

        return entries
            .OrderBy(e => SceneRank(e.Scene))
            .ThenBy(e => e.Scene, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    // ───────────────────────── 枚举 ─────────────────────────

    private static void EnumerateFileTypes(string hive, string view, List<ContextMenuEntry> entries, HashSet<string> seen)
    {
        try
        {
            using RegistryKey baseKey = OpenBase(hive, view);
            using RegistryKey? classes = baseKey.OpenSubKey(@"Software\Classes", writable: false);

            if (classes is null)
            {
                return;
            }

            foreach (string name in SafeNames(classes))
            {
                if (!name.StartsWith('.') || name.Length > 24)
                {
                    continue;
                }

                var owners = new List<string> { name };

                try
                {
                    using RegistryKey? extensionKey = baseKey.OpenSubKey(@"Software\Classes\" + name, writable: false);
                    string progId = Read(extensionKey, "");

                    if (!string.IsNullOrWhiteSpace(progId) && progId.IndexOf('\\') < 0)
                    {
                        owners.Add(progId);
                    }
                }
                catch
                {
                    // 扩展名键读不到就只用扩展名本身
                }

                foreach (string owner in owners.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string suffix in new[] { @"\shell", @"\shellex\ContextMenuHandlers" })
                    {
                        bool shellExtension = suffix.IndexOf("shellex", StringComparison.OrdinalIgnoreCase) >= 0;
                        RootDefinition root = Root(
                            "文件类型 " + name,
                            @"Software\Classes\" + owner + suffix,
                            shellExtension ? "Shell 扩展" : "Shell 命令");

                        EnumerateRoot(hive, view, root, entries, seen, advancedOnly: true);
                    }
                }
            }
        }
        catch
        {
            // 文件类型属于附加信息，读取失败不影响主清单
        }
    }

    private static void EnumerateRoot(
        string hive,
        string view,
        RootDefinition root,
        List<ContextMenuEntry> entries,
        HashSet<string> seen,
        bool advancedOnly = false)
    {
        try
        {
            using RegistryKey baseKey = OpenBase(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(root.Path, writable: false);

            if (key is null)
            {
                return;
            }

            foreach (string childName in SafeNames(key))
            {
                string subKey = root.Path + "\\" + childName;
                string id = hive + "|" + (hive == "HKCU" ? "" : view) + "|" + subKey;

                if (!seen.Add(id))
                {
                    continue;
                }

                try
                {
                    using RegistryKey? child = baseKey.OpenSubKey(subKey, writable: false);

                    if (child is null)
                    {
                        continue;
                    }

                    bool shellExtension = root.Type == "Shell 扩展";
                    string clsid = shellExtension ? Read(child, "") : Read(child, "ExplorerCommandHandler");

                    // 不少 shellex 处理器的 CLSID 只写在子键名上，默认值是空的
                    if (shellExtension && clsid.Length == 0 && childName.StartsWith('{'))
                    {
                        clsid = childName;
                    }

                    string command = shellExtension ? string.Empty : ReadChildDefault(baseKey, subKey + "\\command");
                    string rawDisplay = First(Read(child, "MUIVerb"), Read(child, ""), childName);
                    string display = FriendlyMenuName(rawDisplay, childName, command);
                    bool hasLegacyDisable = HasValue(child, "LegacyDisable");
                    bool hasProgrammaticDisable = HasValue(child, "ProgrammaticAccessOnly");
                    bool disabled = shellExtension ? IsBlocked(hive, view, clsid) : hasLegacyDisable || hasProgrammaticDisable;

                    entries.Add(new ContextMenuEntry
                    {
                        Id = id,
                        Scene = root.Scene,
                        Name = display,
                        RawName = rawDisplay,
                        Type = root.Type,
                        Scope = (hive == "HKCU" ? "当前用户" : "所有用户") + " / "
                            + (view == "Registry32" ? "32 位" : "64 位"),
                        Command = command,
                        IconValue = ResolveEntryIcon(child, hive, view, root, childName),
                        Clsid = clsid,
                        SubCommands = Read(child, "SubCommands"),
                        DisableValueName = shellExtension
                            ? clsid
                            : (hasProgrammaticDisable ? "ProgrammaticAccessOnly" : "LegacyDisable"),
                        Hive = hive,
                        View = view,
                        SubKey = subKey,
                        Enabled = !disabled,
                        RequiresAdmin = hive == "HKLM",
                        ReadOnly = (shellExtension && string.IsNullOrWhiteSpace(clsid))
                            || (!shellExtension && hasLegacyDisable && hasProgrammaticDisable),
                        ReadOnlyReason = shellExtension && string.IsNullOrWhiteSpace(clsid)
                            ? "没有读取到 CLSID，不能安全启停。"
                            : (!shellExtension && hasLegacyDisable && hasProgrammaticDisable
                                ? "同时存在两个禁用标记，当前版本先保持只读，避免破坏程序的显示逻辑。"
                                : string.Empty),
                        UserAdded = HasValue(child, "RogueCleanerUserAdded"),
                        AdvancedOnly = advancedOnly,
                    });
                }
                catch
                {
                    // 单个子键坏了就跳过
                }
            }
        }
        catch
        {
            // 某些根键没有读取权限，跳过即可
        }
    }

    // ───────────────────────── 名称 / 图标解析（移植参考实现） ─────────────────────────

    internal static string FriendlyMenuName(string raw, string childName, string command)
    {
        string value = (raw ?? string.Empty).Trim();
        string lower = (value + " " + childName + " " + command).ToLowerInvariant();

        if (lower.Contains("safe360ext")) return "360 安全扫描";
        if (lower.Contains("softmgrext")) return "360 软件管家";
        if (lower.Contains("qingshellext")) return "上传到 WPS 云文档";
        if (lower.Contains("qingnsecontextmenu")) return "WPS 云文档操作菜单";
        if (lower.Contains("sgshellext")) return "搜狗右键菜单";
        if (lower.Contains("bdeunlock") || lower.Contains("unlock-bde")) return "解锁 BitLocker 驱动器";
        if (lower.Contains("fvewiz") || lower.Contains("manage-bde")) return "管理 BitLocker";

        if (value.StartsWith('@'))
        {
            try
            {
                var resolved = new StringBuilder(512);

                if (SHLoadIndirectString(value, resolved, (uint)resolved.Capacity, IntPtr.Zero) == 0
                    && resolved.Length > 0)
                {
                    value = resolved.ToString();
                }
            }
            catch
            {
                // 解析失败保留原始文本
            }
        }

        string cleanValue = CleanMenuText(value);
        value = ChineseDisplayText.ContextMenuName(cleanValue);

        if (IsReadableMenuText(value)
            && (ChineseDisplayText.HasChinese(value) || !string.Equals(value, cleanValue, StringComparison.OrdinalIgnoreCase)))
        {
            return value;
        }

        string key = (childName ?? string.Empty).Trim();
        string keyLower = key.ToLowerInvariant();

        if (keyLower == "open") return "打开";
        if (keyLower == "runas" || keyLower == "runasuser") return "以管理员身份运行";
        if (keyLower == "edit") return "编辑";
        if (keyLower == "print" || keyLower == "printto") return "打印";
        if (keyLower == "share") return "共享";

        string cleanedKey = ChineseDisplayText.ContextMenuName(CleanMenuText(key));

        if (IsReadableMenuText(cleanedKey) && ChineseDisplayText.HasChinese(cleanedKey))
        {
            return cleanedKey;
        }

        string executable = ExtractExecutable(command);

        if (executable.Length > 0 && File.Exists(executable))
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(executable);
                string description = First(info.FileDescription, info.ProductName);

                if (IsReadableMenuText(description))
                {
                    return ChineseDisplayText.EnsureChineseContextMenuName(
                        CleanMenuText(description), description, string.Empty);
                }
            }
            catch
            {
                // 读不到版本信息就用兜底名称
            }
        }

        return "第三方软件右键菜单";
    }

    private static string ResolveEntryIcon(
        RegistryKey child, string hive, string view, RootDefinition root, string childName)
    {
        string icon = Read(child, "Icon");

        if (!string.IsNullOrWhiteSpace(icon))
        {
            return icon;
        }

        string commandStoreId = Read(child, "CommandStore");

        if (string.IsNullOrWhiteSpace(commandStoreId))
        {
            commandStoreId = Read(child, "SubCommands");
        }

        if (!string.IsNullOrWhiteSpace(commandStoreId) && commandStoreId.IndexOf(';') < 0)
        {
            icon = ReadKey(hive, view, CommandStoreRoot + "\\" + commandStoreId.Trim(), "Icon");

            if (!string.IsNullOrWhiteSpace(icon))
            {
                return icon;
            }
        }

        if (root.Type == "命令仓库")
        {
            return ReadKey(hive, view, CommandStoreRoot + "\\" + childName, "Icon");
        }

        return string.Empty;
    }

    private static string ReadKey(string hive, string view, string subKey, string valueName)
    {
        try
        {
            using RegistryKey baseKey = OpenBase(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(subKey, writable: false);

            return Read(key, valueName);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CleanMenuText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&").Trim();
    }

    private static bool IsReadableMenuText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80)
        {
            return false;
        }

        if (value.StartsWith('@') || value.IndexOf("System32", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        if (value.IndexOf('\\') >= 0 || value.IndexOf('{') >= 0)
        {
            return false;
        }

        return value.Any(c => char.IsLetter(c) || c > 127);
    }

    private static string ExtractExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        string text = Environment.ExpandEnvironmentVariables(command.Trim());

        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);

            return end > 1 ? text.Substring(1, end - 1) : string.Empty;
        }

        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);

        return exe >= 0 ? text[..(exe + 4)].Trim() : string.Empty;
    }

    private static bool IsBlocked(string hive, string view, string clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid))
        {
            return false;
        }

        try
        {
            using RegistryKey baseKey = OpenBase(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(BlockedKey, writable: false);

            return key is not null && HasValue(key, clsid);
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────── 来源软件 / 图标水合（移植参考实现） ─────────────────────────

    private sealed class IconCandidate
    {
        public required string Path { get; init; }

        public int Index { get; init; }
    }

    private sealed class Presentation
    {
        public string Name = "";

        public string Vendor = "";

        public string Confidence = "Unknown";

        public IconCandidate? Icon;

        public string Reason = "";
    }

    private static readonly ConcurrentDictionary<string, string> RepresentativeCache = new(StringComparer.OrdinalIgnoreCase);

    private static void ApplyPresentation(ContextMenuEntry entry)
    {
        Presentation presentation = Resolve(entry);
        entry.SoftwareName = ChineseDisplayText.SoftwareName(presentation.Name);
        entry.Vendor = presentation.Vendor;
        entry.IsThirdParty = string.Equals(presentation.Confidence, "Confirmed", StringComparison.OrdinalIgnoreCase);
        entry.IconPath = presentation.Icon?.Path ?? string.Empty;
        entry.IconIndex = presentation.Icon?.Index ?? 0;
        entry.Name = ChineseDisplayText.EnsureChineseContextMenuName(entry.Name, entry.SoftwareName, entry.Scene);
        entry.PresentationResolved = true;
    }

    private static Presentation Resolve(ContextMenuEntry entry)
    {
        IconCandidate? declaredIcon = ParseIconCandidate(entry.IconValue);
        string path = FirstExistingExecutable(entry.Command);
        string reason = string.Empty;

        if (path.Length == 0 && declaredIcon is not null)
        {
            path = declaredIcon.Path;
            reason = "来自菜单声明的图标资源";
        }

        if (path.Length == 0 && entry.Clsid.Length > 0)
        {
            path = ResolveClsidBinary(entry.Clsid);

            if (path.Length > 0)
            {
                reason = "来自右键扩展注册信息";
            }
        }

        string vendor = entry.Vendor;
        string name = string.Empty;
        string confidence = "Unknown";

        if (path.Length > 0)
        {
            bool windows = IsWindowsBinary(path);

            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                vendor = CleanIdentity(info.CompanyName);
                string product = CleanIdentity(info.ProductName);

                if (product.Length > 0)
                {
                    name = product;
                }
            }
            catch
            {
                // 读不到版本信息就用文件名兜底
            }

            if (!windows && IsWindowsAppsBinary(path) && IsMicrosoftVendor(vendor))
            {
                windows = true;
            }

            if (windows)
            {
                vendor = "微软 / Windows";
                name = "Windows 系统组件";
                confidence = "System";
            }
            else
            {
                if (name.Length == 0)
                {
                    name = Path.GetFileNameWithoutExtension(path);
                }

                confidence = "Confirmed";
            }

            if (reason.Length == 0)
            {
                reason = "来自实际执行文件";
            }
        }

        IconCandidate? iconCandidate = declaredIcon;

        if (iconCandidate is null && path.Length > 0)
        {
            iconCandidate = new IconCandidate { Path = path, Index = 0 };
        }

        if (iconCandidate is not null
            && iconCandidate.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !IsWindowsBinary(iconCandidate.Path))
        {
            string representative = FindRepresentativeExecutable(iconCandidate.Path, entry, name, vendor);

            if (representative.Length > 0)
            {
                iconCandidate = new IconCandidate { Path = representative, Index = 0 };

                // DLL 自身没有友好的产品名（如 BdzShell），改从它代表的 exe 读软件名，
                // 这样界面能显示 "Bandizip" 而不是扩展模块名。
                if (!IsWindowsBinary(representative))
                {
                    try
                    {
                        FileVersionInfo info = FileVersionInfo.GetVersionInfo(representative);
                        string product = CleanIdentity(info.ProductName);
                        string company = CleanIdentity(info.CompanyName);

                        if (product.Length > 0)
                        {
                            name = product;
                        }

                        if (company.Length > 0)
                        {
                            vendor = company;
                        }

                        confidence = "Confirmed";
                    }
                    catch
                    {
                        // 读不到版本信息就沿用 DLL 的名称
                    }
                }
            }
        }

        if (name.Length == 0)
        {
            name = "来源未确认";
        }

        if (vendor.Length == 0)
        {
            vendor = confidence == "System" ? "微软 / Windows" : "来源未确认";
        }

        return new Presentation
        {
            Name = name,
            Vendor = vendor,
            Confidence = confidence,
            Icon = iconCandidate,
            Reason = reason,
        };
    }

    private static IconCandidate? ParseIconCandidate(string value)
    {
        string path = FirstExistingFile(value);

        if (path.Length == 0)
        {
            return null;
        }

        int index = 0;
        Match match = Regex.Match(Environment.ExpandEnvironmentVariables(value), @",\s*(?<i>-?\d+)\s*$");

        if (match.Success)
        {
            int.TryParse(match.Groups["i"].Value, out index);
        }

        return new IconCandidate { Path = path, Index = index };
    }

    /// <summary>DLL 扩展没有自己的主程序时，在安装目录（最多向上三层）挑最像的 exe 作图标。</summary>
    private static string FindRepresentativeExecutable(
        string componentPath, ContextMenuEntry entry, string softwareName, string vendor)
    {
        if (RepresentativeCache.TryGetValue(componentPath, out string? cached) && cached is not null)
        {
            return cached;
        }

        string selected = string.Empty;
        int bestScore = int.MinValue;

        try
        {
            DirectoryInfo? directory = new FileInfo(componentPath).Directory;
            string componentName = Path.GetFileNameWithoutExtension(componentPath);
            string evidenceText = string.Join(" ",
                new[] { entry.Name, entry.RawName, softwareName, vendor, entry.Command, entry.IconValue }
                    .Where(v => !string.IsNullOrWhiteSpace(v))).ToLowerInvariant();

            for (int level = 0; directory is not null && level < 3; level++, directory = directory.Parent)
            {
                FileInfo[] candidates;

                try
                {
                    candidates = directory.EnumerateFiles("*.exe", SearchOption.TopDirectoryOnly).Take(80).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (FileInfo candidate in candidates)
                {
                    string baseName = Path.GetFileNameWithoutExtension(candidate.Name);
                    string lower = baseName.ToLowerInvariant();
                    int score = 100 - level * 12;

                    if (string.Equals(baseName, componentName, StringComparison.OrdinalIgnoreCase)) score += 100;
                    if (string.Equals(baseName, directory.Name, StringComparison.OrdinalIgnoreCase)) score += 80;
                    if (evidenceText.IndexOf(lower) >= 0 && lower.Length >= 4) score += 35;
                    if (Regex.IsMatch(lower, "uninst|uninstall|setup|update|helper|crash|report|repair|notify|toast|installer|inst$", RegexOptions.IgnoreCase)) score -= 70;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        selected = candidate.FullName;
                    }
                }
            }
        }
        catch
        {
            selected = string.Empty;
        }

        RepresentativeCache[componentPath] = selected;
        return selected;
    }

    private static string ResolveClsidBinary(string clsid)
    {
        string clean = clsid.Trim();

        foreach (RegistryView view in Environment.Is64BitOperatingSystem
                     ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                     : new[] { RegistryView.Default })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);

                foreach (string leaf in new[] { "LocalServer32", "InprocServer32" })
                {
                    using RegistryKey? key = root.OpenSubKey($@"CLSID\{clean}\{leaf}", writable: false);
                    string value = key is null ? string.Empty : Convert.ToString(key.GetValue(string.Empty, string.Empty)) ?? string.Empty;
                    string path = FirstExistingFile(value);

                    if (path.Length > 0)
                    {
                        return path;
                    }
                }
            }
            catch
            {
                // 跳过无权限的视图
            }
        }

        return string.Empty;
    }

    private static string FirstExistingExecutable(params string[] values)
    {
        foreach (string value in values)
        {
            string path = ExtractFile(value, executableOnly: true);

            if (path.Length > 0)
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static string FirstExistingFile(params string[] values)
    {
        foreach (string value in values)
        {
            string path = ExtractFile(value, executableOnly: false);

            if (path.Length > 0)
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static string ExtractFile(string value, bool executableOnly)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        Match match = Regex.Match(
            expanded,
            @"(?:""(?<p>[^""]+?\.(?:exe|dll|ico))""|(?<p>[A-Za-z]:\\[^\r\n,;]+?\.(?:exe|dll|ico)))",
            RegexOptions.IgnoreCase);

        string path = match.Success ? match.Groups["p"].Value : expanded.Trim(' ', '"');
        int comma = path.LastIndexOf(',');

        if (comma > 2 && Regex.IsMatch(path[(comma + 1)..], @"^\s*-?\d+\s*$"))
        {
            path = path[..comma].Trim();
        }

        path = path.Trim(' ', '"');

        if (executableOnly
            && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            return File.Exists(path) ? Path.GetFullPath(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CleanIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string clean = value.Trim();

        if (clean is "未知第三方" or "未知" or "仅提示")
        {
            return string.Empty;
        }

        string lower = clean.ToLowerInvariant();

        return lower.Contains("todo") || lower.Contains("<产品名>") || lower.Contains("<product")
            ? string.Empty
            : clean;
    }

    private static bool IsWindowsBinary(string path)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsAppsBinary(string path)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMicrosoftVendor(string vendor)
        => !string.IsNullOrWhiteSpace(vendor)
           && (vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
               || vendor.Contains("微软", StringComparison.Ordinal));

    // ───────────────────────── 注册表读取工具 ─────────────────────────

    private static RegistryKey OpenBase(string hive, string view)
        => RegistryKey.OpenBaseKey(
            hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            view == "Registry32" ? RegistryView.Registry32 : RegistryView.Registry64);

    private static string[] SafeNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string Read(RegistryKey? key, string name)
    {
        try
        {
            return key is null ? string.Empty : Convert.ToString(key.GetValue(name, string.Empty))?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool HasValue(RegistryKey? key, string name)
    {
        try
        {
            return key is not null && key.GetValueNames()
                .Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string ReadChildDefault(RegistryKey baseKey, string child)
    {
        try
        {
            using RegistryKey? key = baseKey.OpenSubKey(child, writable: false);

            return Read(key, "");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string First(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    // ───────────────────────── 启用 / 禁用 / 删除 ─────────────────────────

    public static void SetEnabled(ContextMenuEntry entry, bool enabled)
    {
        if (entry.ReadOnly)
        {
            throw new InvalidOperationException(entry.ReadOnlyReason);
        }

        bool shellExtension = entry.IsShellExtension;

        if (!entry.RequiresAdmin || IsElevated())
        {
            try
            {
                using RegistryKey baseKey = OpenBase(entry.Hive, entry.View);

                if (shellExtension)
                {
                    using RegistryKey blocked = baseKey.CreateSubKey(BlockedKey, writable: true)
                        ?? throw new InvalidOperationException("无法打开 Shell Extensions\\Blocked");

                    if (enabled)
                    {
                        blocked.DeleteValue(entry.Clsid, throwOnMissingValue: false);
                    }
                    else
                    {
                        blocked.SetValue(entry.Clsid, string.Empty, RegistryValueKind.String);
                    }
                }
                else
                {
                    using RegistryKey key = baseKey.OpenSubKey(entry.SubKey, writable: true)
                        ?? throw new InvalidOperationException("无法打开注册表项（可能已被删除）");

                    if (enabled)
                    {
                        key.DeleteValue(entry.DisableValueName, throwOnMissingValue: false);
                    }
                    else
                    {
                        key.SetValue(entry.DisableValueName, string.Empty, RegistryValueKind.String);
                    }
                }

                return;
            }
            catch when (!entry.RequiresAdmin || !IsElevated())
            {
                // 落到下面的提权路径
            }
        }

        if (shellExtension)
        {
            RunElevated(enabled
                ? RegDeleteCommand(entry, BlockedKey, entry.Clsid)
                : RegAddCommand(entry, BlockedKey, entry.Clsid));
        }
        else
        {
            RunElevated(enabled
                ? RegDeleteCommand(entry, entry.SubKey, entry.DisableValueName)
                : RegAddCommand(entry, entry.SubKey, entry.DisableValueName));
        }
    }

    public static void Delete(ContextMenuEntry entry)
    {
        if (entry.ReadOnly)
        {
            throw new InvalidOperationException(entry.ReadOnlyReason);
        }

        if (!entry.RequiresAdmin || IsElevated())
        {
            try
            {
                using RegistryKey baseKey = OpenBase(entry.Hive, entry.View);
                baseKey.DeleteSubKeyTree(entry.SubKey, throwOnMissingSubKey: false);

                if (entry.IsShellExtension && entry.Clsid.Length > 0)
                {
                    try
                    {
                        using RegistryKey? blocked = baseKey.OpenSubKey(BlockedKey, writable: true);
                        blocked?.DeleteValue(entry.Clsid, throwOnMissingValue: false);
                    }
                    catch
                    {
                        // 清理屏蔽标记失败不影响删除结果
                    }
                }

                return;
            }
            catch when (!entry.RequiresAdmin || !IsElevated())
            {
                // 落到下面的提权路径
            }
        }

        RunElevated($"delete \"{HivePrefix(entry)}\\{entry.SubKey}\" /f{ViewFlag(entry)}");
    }

    public static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();

            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string RegDeleteCommand(ContextMenuEntry entry, string keyPath, string valueName)
        => $"delete \"{HivePrefix(entry)}\\{keyPath}\" /v \"{valueName}\" /f{ViewFlag(entry)}";

    private static string RegAddCommand(ContextMenuEntry entry, string keyPath, string valueName)
        => $"add \"{HivePrefix(entry)}\\{keyPath}\" /v \"{valueName}\" /t REG_SZ /d \"\" /f{ViewFlag(entry)}";

    private static string HivePrefix(ContextMenuEntry entry) => entry.Hive == "HKLM" ? "HKLM" : "HKCU";

    private static string ViewFlag(ContextMenuEntry entry)
        => entry.View == "Registry32" ? " /reg:32" : string.Empty;

    private static void RunElevated(string arguments)
    {
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        });

        process?.WaitForExit();
    }
}

/// <summary>右键菜单名称 / 来源软件的中文归一化（移植参考资料）。</summary>
internal static class ChineseDisplayText
{
    public static string ContextMenuName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string text = value.Trim();

        if (text.Equals("Open Folder as PyCharm Project", StringComparison.OrdinalIgnoreCase)) return "作为 PyCharm 项目打开文件夹";
        if (text.Equals("Open Folder as Project", StringComparison.OrdinalIgnoreCase)) return "作为项目打开文件夹";
        if (text.Equals("Open Git Bash here", StringComparison.OrdinalIgnoreCase)) return "在此处打开 Git Bash";
        if (text.Equals("Open Git GUI Here", StringComparison.OrdinalIgnoreCase)) return "在此处打开 Git 图形界面";
        if (text.Equals("Open in Windows Terminal", StringComparison.OrdinalIgnoreCase)) return "在 Windows 终端中打开";
        if (text.Equals("Open PowerShell window here", StringComparison.OrdinalIgnoreCase)) return "在此处打开 PowerShell 窗口";
        if (text.Equals("Pin to Quick access", StringComparison.OrdinalIgnoreCase)) return "固定到快速访问";
        if (text.Equals("Unpin from Quick access", StringComparison.OrdinalIgnoreCase)) return "从快速访问取消固定";
        if (text.Equals("Open", StringComparison.OrdinalIgnoreCase)) return "打开";
        if (text.Equals("Edit", StringComparison.OrdinalIgnoreCase)) return "编辑";
        if (text.Equals("Print", StringComparison.OrdinalIgnoreCase)) return "打印";
        if (text.Equals("Share", StringComparison.OrdinalIgnoreCase)) return "共享";

        Match editWith = Regex.Match(text, @"^Edit\s+with\s+(?<app>.+)$", RegexOptions.IgnoreCase);

        if (editWith.Success) return "使用 " + TrimEnglishDecoration(editWith.Groups["app"].Value) + " 编辑";

        Match contextMenu = Regex.Match(text, @"^(?<app>.+?)\s+Context\s+menu$", RegexOptions.IgnoreCase);

        if (contextMenu.Success) return TrimEnglishDecoration(contextMenu.Groups["app"].Value) + " 右键菜单";

        Match openIn = Regex.Match(text, @"^Open\s+(?:Folder\s+)?in\s+(?<app>.+)$", RegexOptions.IgnoreCase);

        if (openIn.Success) return "在 " + TrimEnglishDecoration(openIn.Groups["app"].Value) + " 中打开";

        if (text.StartsWith("Open with ", StringComparison.OrdinalIgnoreCase)) return "使用 " + text[10..].Trim() + " 打开";

        Match scanWith = Regex.Match(text, @"^Scan\s+with\s+(?<app>.+)$", RegexOptions.IgnoreCase);

        if (scanWith.Success) return "使用 " + TrimEnglishDecoration(scanWith.Groups["app"].Value) + " 扫描";

        Match compareWith = Regex.Match(text, @"^Compare\s+with\s+(?<app>.+)$", RegexOptions.IgnoreCase);

        if (compareWith.Success) return "使用 " + TrimEnglishDecoration(compareWith.Groups["app"].Value) + " 比较";

        Match uploadTo = Regex.Match(text, @"^Upload\s+to\s+(?<app>.+)$", RegexOptions.IgnoreCase);

        if (uploadTo.Success) return "上传到 " + TrimEnglishDecoration(uploadTo.Groups["app"].Value);

        Match addTo = Regex.Match(text, @"^Add\s+to\s+(?<target>.+)$", RegexOptions.IgnoreCase);

        if (addTo.Success) return "添加到 " + TrimEnglishDecoration(addTo.Groups["target"].Value);

        Match sendTo = Regex.Match(text, @"^Send\s+to\s+(?<target>.+)$", RegexOptions.IgnoreCase);

        if (sendTo.Success) return "发送到 " + TrimEnglishDecoration(sendTo.Groups["target"].Value);

        return text;
    }

    public static string EnsureChineseContextMenuName(string value, string softwareName, string scene)
    {
        string translated = ContextMenuName(value);
        string software = SoftwareName(softwareName);
        bool softwareKnown = software != "来源未确认" && software != "正在识别…";
        string softwareLabel = TrimSoftwareSuffix(software);

        // 通用兜底名（扩展没有可读标题时）直接换成“软件名 + 右键菜单”，
        // 例如 Bandizip →「Bandizip 右键菜单」，避免一堆同名的“第三方软件右键菜单”。
        if (IsGenericMenuName(translated))
        {
            if (softwareKnown)
            {
                return softwareLabel + " 右键菜单";
            }

            string location = string.IsNullOrWhiteSpace(scene) ? string.Empty : scene.Trim();

            return (HasChinese(location) ? location : "第三方软件") + "右键菜单";
        }

        if (HasChinese(translated))
        {
            return translated;
        }

        if (softwareKnown)
        {
            return softwareLabel + " 右键菜单";
        }

        string fallback = string.IsNullOrWhiteSpace(scene) ? string.Empty : scene.Trim();

        return (HasChinese(fallback) ? fallback : "第三方软件") + "右键菜单";
    }

    private static bool IsGenericMenuName(string value)
        => value is "第三方软件右键菜单" or "第三方右键菜单" or "右键菜单" or "来源未确认";

    private static string TrimSoftwareSuffix(string value)
    {
        string text = value.Trim();

        foreach (string suffix in new[] { " 软件", "软件" })
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal) && text.Length > suffix.Length)
            {
                return text[..^suffix.Length].Trim();
            }
        }

        return text;
    }

    public static string SoftwareName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "来源未确认";
        }

        string text = value.Trim();

        if (HasChinese(text)) return text;
        if (text.Contains("WPS Office", StringComparison.OrdinalIgnoreCase) || text.Contains("Kingsoft", StringComparison.OrdinalIgnoreCase)) return "WPS / 金山";
        if (text.Contains("PyCharm", StringComparison.OrdinalIgnoreCase)) return "PyCharm 开发工具";
        if (text.Contains("Notepad++", StringComparison.OrdinalIgnoreCase)) return "Notepad++ 文本编辑器";
        if (text.Contains("WinRAR", StringComparison.OrdinalIgnoreCase)) return "WinRAR 压缩软件";
        if (text.Contains("Beyond Compare", StringComparison.OrdinalIgnoreCase)) return "Beyond Compare 文件比较工具";
        if (text.Contains("Windows", StringComparison.OrdinalIgnoreCase) && text.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) return "Windows 系统组件";
        if (text.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD Radeon 显卡软件";
        if (text.Equals("Git", StringComparison.OrdinalIgnoreCase)) return "Git 版本管理工具";
        if (text.Equals("Source", StringComparison.OrdinalIgnoreCase) || text.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return "来源未确认";

        return text + " 软件";
    }

    public static bool HasChinese(string value)
        => !string.IsNullOrEmpty(value)
           && value.Any(c => c >= '\u3400' && c <= '\u9fff');

    private static string TrimEnglishDecoration(string value)
        => (value ?? string.Empty).Trim().TrimEnd('.', '…').Trim();
}
