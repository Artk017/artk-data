using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArtkTools.Services;

/// <summary>软件页里的一个条目：要么有官方直链（应用内下载），要么自动解析，要么只能跳官网下载页。</summary>
public sealed class AppEntry
{
    /// <summary>服务端记录 Id；内置条目为空。</summary>
    public string? Id { get; init; }

    /// <summary>显示名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>厂商 / 一句话说明。</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>卡片上的一句介绍。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>分类：通讯 / 游戏 / 工具。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>图标底色（渐变主色），#RRGGBB。</summary>
    public string Color { get; init; } = string.Empty;

    /// <summary>图标图片地址；null 表示用首字母 + 固定底色。</summary>
    public string? IconUrl { get; init; }

    /// <summary>官网地址（没有直链时就是下载页）。</summary>
    public string HomeUrl { get; init; } = string.Empty;

    /// <summary>固定不变的官方安装包直链；null 表示要现场解析或手动抓。</summary>
    public string? DirectUrl { get; init; }

    /// <summary>拿不到固定直链时，用哪种方式现场解析。</summary>
    public ResolveKind Resolve { get; init; } = ResolveKind.None;

    /// <summary>安装包大约多大，用于列表提示。</summary>
    public string? SizeHint { get; init; }

    /// <summary>排序，小的在前。</summary>
    public int Sort { get; init; }

    /// <summary>备注（管理界面用）。</summary>
    public string? Note { get; init; }

    /// <summary>卡片图标上显示的字。</summary>
    public string Initial => Name[..1];

    public bool HasDirectLink => !string.IsNullOrEmpty(DirectUrl) || Resolve != ResolveKind.None;
}

/// <summary>
/// 常用软件清单。三种拿包方式：
///   1) DirectUrl 固定直链（微信 / 罗技 / VS Code / Bandizip / Steam / PotPlayer）；
///   2) Resolve 现场解析官网（KOOK 走版本接口，NVIDIA / 奇游 / QQ音乐 抓下载页里的地址）；
///   3) 都不行就跳官网（QQ 的地址要页面里点击才签发签名，可用「添加软件」打开网页点下载抓一次）。
///
/// 清单**只从服务器取**（`/api/catalog`），取不到就什么都不显示；本地的 `Builtins` 只当直链兜底数据用，
/// 不参与软件列表展示。
/// </summary>
public static class AppCatalog
{
    /// <summary>图标统一用的底色（不再让用户填颜色）。</summary>
    public const string FixedColor = "#6C4CF1";

    /// <summary>内置条目：只作为「直链 / 官网地址」的兜底数据（自动填充用），不作为软件列表来源。</summary>
    public static IReadOnlyList<AppEntry> Builtins { get; } = new AppEntry[]
    {
        new()
        {
            Name = "KOOK",
            Vendor = "杭州逍遥一下科技 · 语音开黑",
            Summary = "开黑语音、主题服务器，游戏内覆盖",
            Category = "通讯",
            Color = "#5B6CF9",
            HomeUrl = "https://www.kookapp.cn/download",
            Resolve = ResolveKind.KookApi,
        },
        new()
        {
            Name = "QQ",
            Vendor = "腾讯 · 即时通讯",
            Summary = "聊天、群、文件传输，NT 新架构版本",
            Category = "通讯",
            Color = "#12B7F5",
            HomeUrl = "https://im.qq.com/pcqq",
        },
        new()
        {
            Name = "微信",
            Vendor = "腾讯 · 聊天与支付",
            Summary = "Windows 桌面版，聊天记录可与手机同步",
            Category = "通讯",
            Color = "#07C160",
            HomeUrl = "https://pc.weixin.qq.com/",
            DirectUrl = "https://dldir1.qq.com/weixin/Windows/WeChatSetup.exe",
            SizeHint = "约 270 MB",
        },
        new()
        {
            Name = "QQ音乐",
            Vendor = "腾讯 · 在线音乐",
            Summary = "曲库、歌词与本地音乐管理",
            Category = "工具",
            Color = "#31C27C",
            HomeUrl = "https://y.qq.com/download/index.html",
        },
        new()
        {
            Name = "Logitech G HUB",
            Vendor = "罗技 · 外设驱动",
            Summary = "鼠标键盘耳机统一设置、宏与灯光",
            Category = "工具",
            Color = "#00A8E0",
            HomeUrl = "https://www.logitechg.com/zh-cn/innovation/g-hub.html",
            DirectUrl = "https://download01.logi.com/web/ftp/pub/techsupport/gaming/lghub_installer.exe",
            SizeHint = "约 70 MB",
        },
        new()
        {
            Name = "NVIDIA App",
            Vendor = "NVIDIA · 驱动与游戏优化",
            Summary = "显卡驱动更新、游戏优化，替代 GeForce Experience",
            Category = "工具",
            Color = "#76B900",
            HomeUrl = "https://www.nvidia.cn/software/nvidia-app/",
            Resolve = ResolveKind.NvidiaPage,
        },
        new()
        {
            Name = "Visual Studio Code",
            Vendor = "Microsoft · 代码编辑器",
            Summary = "轻量编辑器，插件生态丰富，用户级安装",
            Category = "工具",
            Color = "#007ACC",
            HomeUrl = "https://code.visualstudio.com/",
            DirectUrl = "https://code.visualstudio.com/sha/download?build=stable&os=win32-x64-user",
            SizeHint = "约 240 MB",
        },
        new()
        {
            Name = "PotPlayer 64 位",
            Vendor = "Kakao · 视频播放器",
            Summary = "本地播放器，硬解、字幕与音轨都齐全",
            Category = "工具",
            Color = "#F57C00",
            HomeUrl = "https://potplayer.tv/",
            DirectUrl = "https://t1.daumcdn.net/potplayer/PotPlayer/Version/Latest/PotPlayerSetup64.exe",
        },
        new()
        {
            Name = "Bandizip",
            Vendor = "Bandisoft · 压缩软件",
            Summary = "免费压缩解压，支持 40 多种格式",
            Category = "工具",
            Color = "#1B75BB",
            HomeUrl = "https://cn.bandisoft.com/bandizip/",
            DirectUrl = "https://www.bandisoft.com/bandizip/dl.php?web",
            SizeHint = "约 11 MB",
        },
        new()
        {
            Name = "Steam",
            Vendor = "Valve · 游戏平台",
            Summary = "游戏商店与库，装完即可登录",
            Category = "游戏",
            Color = "#4B9FD5",
            HomeUrl = "https://store.steampowered.com/about/",
            DirectUrl = "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe",
            SizeHint = "约 2.3 MB",
        },
        new()
        {
            Name = "奇游电竞加速器",
            Vendor = "奇游 · 游戏加速",
            Summary = "外服游戏加速，支持主流网游与主机",
            Category = "游戏",
            Color = "#2C6BF2",
            HomeUrl = "https://www.qiyou.cn/",
            Resolve = ResolveKind.QiyouPage,
        },
    };

    /// <summary>当前生效的清单：只来自服务器，拿不到就是空列表（不再退回本地 / 内置）。</summary>
    public static IReadOnlyList<AppEntry> All { get; private set; } = Array.Empty<AppEntry>();

    /// <summary>这次清单是从哪来的（给界面显示）；失败时是一句失败原因。</summary>
    public static string Source { get; private set; } = "清单加载中…";

    /// <summary>
    /// 重新加载清单：**只从服务器取**。
    /// 取不到就把清单清空并返回失败原因，界面据此不显示任何软件。
    /// </summary>
    public static async Task<string> ReloadAsync(CancellationToken token = default)
    {
        try
        {
            List<AppDto> fetched = await CatalogServer.FetchAsync(token).ConfigureAwait(false);
            All = fetched.Select(FromDto).ToList();
            Source = "服务器清单";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            All = Array.Empty<AppEntry>();
            Source = $"获取不到清单（{ex.Message}）";
        }

        return Source;
    }

    public static AppDto ToDto(AppEntry entry) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        Vendor = entry.Vendor,
        Summary = entry.Summary,
        Category = entry.Category,
        Color = FixedColor,
        HomeUrl = entry.HomeUrl,
        DirectUrl = entry.DirectUrl,
        IconUrl = entry.IconUrl,
        Resolve = entry.Resolve switch
        {
            ResolveKind.KookApi => "kook",
            ResolveKind.NvidiaPage => "nvidia",
            ResolveKind.QiyouPage => "qiyou",
            _ => "none",
        },
        SizeHint = entry.SizeHint,
        Note = entry.Note,
        Sort = entry.Sort,
        Enabled = true,
    };

    public static AppEntry FromDto(AppDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        Vendor = dto.Vendor,
        Summary = dto.Summary,
        Category = string.IsNullOrWhiteSpace(dto.Category) ? "工具" : dto.Category,
        Color = FixedColor,
        HomeUrl = dto.HomeUrl,
        DirectUrl = string.IsNullOrWhiteSpace(dto.DirectUrl) ? null : dto.DirectUrl,
        IconUrl = string.IsNullOrWhiteSpace(dto.IconUrl) ? null : dto.IconUrl,
        Resolve = dto.Resolve switch
        {
            "kook" => ResolveKind.KookApi,
            "nvidia" => ResolveKind.NvidiaPage,
            "qiyou" => ResolveKind.QiyouPage,
            _ => ResolveKind.None,
        },
        SizeHint = dto.SizeHint,
        Note = dto.Note,
        Sort = dto.Sort,
    };
}
