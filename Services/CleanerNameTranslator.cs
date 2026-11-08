using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ArtkTools.Services;

/// <summary>
/// Winapp2 数据库里的项目名是英文的，这里把常见词句翻译成中文。
/// 品牌名（Microsoft Edge、Chrome 等）保留原样，只翻译动作与对象。
/// </summary>
public static class CleanerNameTranslator
{
    private static readonly Dictionary<string, string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tencent WeChat"] = "腾讯微信",
        ["Tencent QQ"] = "腾讯 QQ",
        ["Microsoft To Do"] = "Microsoft 待办",
        ["Microsoft Outlook For Windows"] = "Microsoft Outlook（Windows）",
        ["Microsoft PC Manager"] = "Microsoft 电脑管家",
        ["Microsoft Solitaire Collection"] = "Microsoft 纸牌合集",
        ["Microsoft Phone Link"] = "Microsoft 手机连接",
        ["Universal Windows Platform"] = "通用 Windows 平台",
        ["Windows Shell - Folder/Dialog Box View Settings"] = "Windows Shell - 文件夹/对话框视图设置",
        ["Windows Shell - Theme Colors History"] = "Windows Shell - 主题色历史记录",
        ["Logitech G HUB - Window Size/Location"] = "Logitech G HUB - 窗口大小/位置",
        ["Steam Packages"] = "Steam 安装包",
        ["Steam Installers"] = "Steam 安装程序",
        ["Steam Games"] = "Steam 游戏",
        // 保持原名的品牌 / 框架
        ["Squirrel.Windows"] = "Squirrel.Windows",
        ["Qt Framework"] = "Qt Framework",
        ["Windows Media Player"] = "Windows Media Player",
        ["NVIDIA App"] = "NVIDIA App",
    };

    /// <summary>按长度从长到短匹配，避免“Logs”被“Log”抢先替换。</summary>
    private static readonly List<(Regex Pattern, string Replacement)> Rules = BuildRules();

    public static string Translate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (Exact.TryGetValue(name, out var exact))
        {
            return exact;
        }

        // 已经是中文（例如补充规则里的项目）就直接返回
        if (!name.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            return name;
        }

        var text = name;
        foreach (var (pattern, replacement) in Rules)
        {
            text = pattern.Replace(text, replacement);
        }

        text = text.Replace(" & ", "和").Replace("&", "和");

        // 短语逐段替换后，中文之间常留下多余空格（“凭据管理器 保存的”），合并掉
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", "");
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();

        return text;
    }

    /// <summary>数据库里的风险提示也是英文的，这里给出现有提示的中文版本；未知提示原样返回。</summary>
    private static readonly Dictionary<string, string> Warnings = new(StringComparer.Ordinal)
    {
        ["Close Visual Studio Code before cleaning."] =
            "清理前请先关闭 Visual Studio Code。",

        [@"Deletes Explorer\FileExts which stores your custom file type associations (e.g. which app opens .jpg or .png). After cleaning, Windows will no longer remember your chosen default apps for file types until you reassign them manually."] =
            "会删除资源管理器的 FileExts（保存自定义文件类型关联的位置，例如 .jpg、.png 默认用哪个应用打开）。清理后 Windows 不会再记住你设置的默认应用，需要重新手动指定。",

        ["Clears the recent documents list for all user accounts on this machine, not just the current user."] =
            "会清空本机所有用户账户的最近文档列表，而不只是当前用户。",

        ["Resets all Explorer folder view preferences (icon/details/column layouts), window sizes, positions, and File Open/Save dialog history. All custom folder layouts will be lost permanently."] =
            "会重置所有资源管理器的文件夹视图设置（图标 / 详细信息 / 列布局）、窗口大小与位置，以及文件打开/保存对话框历史。自定义的文件夹布局会永久丢失。",

        ["Deletes all Recall screenshots and semantic index data. Recall will start fresh after cleaning."] =
            "会删除所有 Recall 截图与语义索引数据，清理后 Recall 会重新开始记录。",

        ["Clears Jump Lists (recently used files shown per app on the taskbar). Pinned items are safe, but all recent file history will be removed."] =
            "会清空跳转列表（任务栏上每个应用显示的最近使用文件）。固定项不受影响，但最近文件历史会被全部移除。",

        ["Resets the Start Menu program cache. The Start Menu may take a moment to rebuild on next login."] =
            "会重置开始菜单的程序缓存，下次登录时开始菜单可能需要一点时间来重建。",

        ["You need to run this in Windows Safe Mode. Or stop services \"Connected Devices Platform Service\" and \"Connected Devices Platform User Service_xxxxx\" prior to cleaning and restart after cleaning."] =
            "需要在 Windows 安全模式下执行；或者先停止“Connected Devices Platform Service”和“Connected Devices Platform User Service_xxxxx”两个服务，清理完成后再重新启动。",

        ["Removes the local file history used to restore earlier versions of edited files."] =
            "会删除用于恢复文件早期版本的本地文件历史记录。",

        ["Heads up: this also clears your pinned/favorite files in Word, Excel and PowerPoint. Office stores pinned items in the same \"recent files\" list, so they get wiped along with the recent-documents history. Leave this unchecked if you rely on Office favorites."] =
            "注意：这也会清空 Word、Excel、PowerPoint 中固定的文件。Office 把固定项保存在同一个“最近文件”列表里，会随最近文档历史一起删除。如果你依赖 Office 收藏，请不要勾选。",

        ["This will also delete all app-specific settings."] =
            "这还会删除该应用的全部个性化设置。",

        ["Make sure to launch all of your installed UPlay games at least once before running this, or they may not launch properly."] =
            "执行前请先启动一次所有已安装的 UPlay 游戏，否则它们可能无法正常启动。",

        ["This entry should only be enabled after you have launched each of your installed Steam games at least once. Deleting these files before then may cause Steam to think the game's installation is broken. After the first launch, these files become useless."] =
            "只有在至少启动过一次所有已安装的 Steam 游戏后才应勾选。提前删除这些文件可能让 Steam 认为游戏安装已损坏；首次启动之后这些文件就没有用了。",

        ["This will cause you to redownload the Paramount Dynamic RSS file."] =
            "会导致重新下载 Paramount 的动态 RSS 文件。",
    };

    public static string TranslateWarning(string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return warning ?? string.Empty;
        }

        return Warnings.TryGetValue(warning, out var chinese) ? chinese : warning;
    }

    private static List<(Regex, string)> BuildRules()
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ---------- 浏览器 / 应用 ----------
            ["Autofill Data"] = "自动填充数据",
            ["Autoplay Preferences"] = "自动播放设置",
            ["Search Engine Preferences"] = "搜索引擎设置",
            ["Bookmark Backups"] = "书签备份",
            ["Bookmark Favicons"] = "书签图标",
            ["Default App Handlers"] = "默认应用关联",
            ["Download History"] = "下载历史记录",
            ["Extension Cookies"] = "扩展 Cookie",
            ["Progressive Web Apps"] = "渐进式 Web 应用",
            ["Threat Detection"] = "威胁检测",
            ["Shopping Insights"] = "购物洞察",
            ["Storage Quota Manager"] = "存储配额管理器",
            ["Sync Data"] = "同步数据",
            ["Web Browsing"] = "网页浏览",
            ["Web Storage"] = "网页存储",
            ["PDF Restore Data"] = "PDF 恢复数据",
            ["Pinned Tabs"] = "固定标签页",
            ["Privacy Sandbox"] = "隐私沙盒",
            ["DRM Data"] = "DRM 数据",
            ["Caches"] = "缓存",

            // ---------- 系统组件 ----------
            ["Credential Manager"] = "凭据管理器",
            ["Saved Credentials"] = "保存的凭据",
            ["Remote Desktop"] = "远程桌面",
            ["Saved Connections"] = "保存的连接",
            ["Power Efficiency Diagnostics"] = "电源效率诊断",
            ["Program Compatibility Assistant"] = "程序兼容性助手",
            ["Activity History"] = "活动历史记录",
            ["Recent Wallpaper Locations"] = "最近使用的壁纸位置",
            ["Recent Documents"] = "最近使用的文档",
            ["Theme Colors"] = "主题色",
            ["Event Logs"] = "事件日志",
            ["Event Viewer"] = "事件查看器",
            ["Device Manager"] = "设备管理器",
            ["Task Scheduler"] = "任务计划程序",
            ["Taskbar"] = "任务栏",
            ["Start Menu"] = "开始菜单",
            ["Recycle Bin"] = "回收站",
            ["Lock Screen"] = "锁屏",
            ["Voice Recorder"] = "录音机",
            ["Snipping Tool"] = "截图工具",
            ["Volume Mixer"] = "音量混音器",
            ["Media Extensions"] = "媒体扩展",
            ["Media Player"] = "媒体播放器",
            ["Package Manager"] = "包管理器",
            ["Registry Editor"] = "注册表编辑器",
            ["System Assessment Tool"] = "系统评估工具",
            ["Font Cache"] = "字体缓存",
            ["Language Libraries"] = "语言库",
            ["Get Help"] = "获取帮助",
            ["CD Burning Queue"] = "CD 刻录队列",
            ["AutoPlay Devices"] = "自动播放设备",
            ["Cryptographic Services"] = "加密服务",
            ["Error Reporting"] = "错误报告",
            ["HTML Help"] = "HTML 帮助",
            ["Management Console"] = "管理控制台",
            ["Sticky Notes"] = "便笺",
            ["Temporary Files"] = "临时文件",
            ["View Settings"] = "视图设置",
            ["Folder/Dialog Box"] = "文件夹/对话框",
            ["PowerShell"] = "PowerShell",
            ["Subsystems"] = "子系统",

            // ---------- 开发工具 / 游戏 ----------
            ["Installation Packages"] = "安装包",
            ["Unsaved Files"] = "未保存的文件",
            ["File History"] = "文件历史",
            ["Package Cache"] = "包缓存",
            ["Graphics Drivers"] = "显卡驱动",
            ["Control Panel"] = "控制面板",
            ["Shader Cache"] = "着色器缓存",
            ["Update Files"] = "更新文件",

            // ---------- 通用词 ----------
            ["Usernames"] = "用户名",
            ["Passwords"] = "密码",
            ["Telemetry"] = "遥测",
            ["Session"] = "会话",
            ["Security"] = "安全",
            ["Feedback Hub"] = "反馈中心",
            ["Notifications"] = "通知",
            ["Preferences"] = "设置",
            ["Settings"] = "设置",
            ["Downloads"] = "下载",
            ["Download"] = "下载",
            ["History"] = "历史记录",
            ["Logs"] = "日志",
            ["Log"] = "日志",
            ["Backups"] = "备份",
            ["Backup"] = "备份",
            ["Installers"] = "安装程序",
            ["Installer"] = "安装程序",
            ["Cookies"] = "Cookie",
            ["Cache"] = "缓存",
            ["Temp"] = "临时",
            ["Temporary"] = "临时",
            ["Unsaved"] = "未保存",
            ["Saved"] = "保存的",
            ["Recent"] = "最近使用的",
            ["Files"] = "文件",
            ["File"] = "文件",
            ["Folders"] = "文件夹",
            ["Folder"] = "文件夹",
            ["Devices"] = "设备",
            ["Connections"] = "连接",
            ["Reports"] = "报告",
            ["Dumps"] = "转储",
            ["Thumbnails"] = "缩略图",
            ["Icons"] = "图标",
            ["Data"] = "数据",
            ["Database"] = "数据库",
            ["Photos"] = "照片",
            ["Camera"] = "相机",
            ["Clock"] = "时钟",
            ["Calculator"] = "计算器",
            ["Notepad"] = "记事本",
            ["News"] = "新闻",
            ["Store"] = "应用商店",
            ["Search"] = "搜索",
            ["Printing"] = "打印",
            ["Update"] = "更新",
            ["Widgets"] = "小组件",
            ["Libraries"] = "库",
            ["Platform"] = "平台",
        };

        return pairs
            .OrderByDescending(p => p.Key.Length)
            .Select(p => (
                new Regex(
                    "(?<![A-Za-z0-9_])" + Regex.Escape(p.Key) + "(?![A-Za-z0-9_])",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                p.Value))
            .ToList();
    }
}
