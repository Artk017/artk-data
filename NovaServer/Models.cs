using System.Text.Json.Serialization;

namespace NovaServer;

/// <summary>一款软件。字段和桌面端 AppEntry 一一对应，多的几个（Id/Sort/Enabled/UpdatedAt）给服务端用。</summary>
public sealed class AppItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Vendor { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    /// <summary>通讯 / 游戏 / 工具。</summary>
    public string Category { get; set; } = "工具";

    /// <summary>图标底色 #RRGGBB。</summary>
    public string Color { get; set; } = "#6C4CF1";

    /// <summary>官网地址（没有直链时就是下载页）。</summary>
    public string HomeUrl { get; set; } = string.Empty;

    /// <summary>固定官方直链；空表示要现场解析或手动抓。</summary>
    public string? DirectUrl { get; set; }

    /// <summary>图标图片地址（添加软件时从官网抓取）；空表示用首字母 + 固定底色。</summary>
    public string? IconUrl { get; set; }

    /// <summary>自动解析方式：none / kook / nvidia / qiyou。</summary>
    public string Resolve { get; set; } = "none";

    /// <summary>安装包大小提示，例如「约 270 MB」。</summary>
    public string? SizeHint { get; set; }

    /// <summary>备注（卡片不显示，给管理界面看）。</summary>
    public string? Note { get; set; }

    /// <summary>排序，小的在前。</summary>
    public int Sort { get; set; }

    /// <summary>下架后桌面端不显示，但记录还留着。</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>存盘结构：整体 version 每次写入 +1，桌面端可以按 version 判断要不要更新。</summary>
public sealed class CatalogFile
{
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public List<AppItem> Items { get; set; } = new();
}

/// <summary>GET /api/catalog 的返回体。</summary>
public sealed class CatalogResponse
{
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Count { get; set; }

    public List<AppItem> Apps { get; set; } = new();
}

/// <summary>服务端配置（server.json）。</summary>
public sealed class ServerConfig
{
    /// <summary>内置的默认密钥：和客户端写死的那一份保持一致，装完不用手填。</summary>
    public const string DefaultApiKey = "nova-061a213b92373820";

    /// <summary>监听端口。</summary>
    public int Port { get; set; } = 5080;

    /// <summary>客户端请求要带的密钥（X-Api-Key）；清空表示不校验（不建议）。</summary>
    public string ApiKey { get; set; } = DefaultApiKey;

    /// <summary>数据文件路径，相对路径按 exe 所在目录算。</summary>
    public string DataFile { get; set; } = "data/catalog.json";

    /// <summary>读接口是否也要密钥。默认要，免得光有地址就能把整份清单抓走。</summary>
    public bool RequireKeyForRead { get; set; } = true;

    /// <summary>数据源根地址（GitHub Release 资产目录）。留空用内置的 artk-data 仓库。</summary>
    public string DataBaseUrl { get; set; } = "";

    /// <summary>数据源是私有仓库（或匿名被限流）时填 GitHub token；公开仓库留空即可。</summary>
    public string DataToken { get; set; } = "";

    /// <summary>启动时是否强制重新下载数据文件（默认只在缺失时下载，避免覆盖服务器上的本地改动）。</summary>
    public bool RefreshDataOnStart { get; set; }

    /// <summary>清单改动后是否自动发布回 GitHub Release（默认关；要用得先配 publishToken）。</summary>
    public bool AutoPublish { get; set; }

    /// <summary>发布到哪个仓库，写「owner/repo」；留空则从 dataBaseUrl 里解析，再兜底 Artk017/artk-data。</summary>
    public string PublishRepo { get; set; } = "";

    /// <summary>发布到哪个 Release tag；留空则从 dataBaseUrl 里解析，再兜底 artk-tools。</summary>
    public string PublishTag { get; set; } = "";

    /// <summary>有 contents:write 权限的 GitHub token；留空时回退用 dataToken。</summary>
    public string PublishToken { get; set; } = "";

    /// <summary>发布时是否连 data 目录下的 *.reg 一起传（默认只传 catalog.json）。</summary>
    public bool PublishRegFiles { get; set; }

    /// <summary>GitHub API 根地址（企业版填 https://host/api/v3）；留空用 api.github.com。</summary>
    public string PublishApiBase { get; set; } = "";
}

/// <summary>统一的 JSON 设置：camelCase、缩进、中文不转义（数据文件要能直接看）。</summary>
internal static class Json
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
