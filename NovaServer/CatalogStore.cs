using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaServer;

/// <summary>
/// 清单存储：一个 JSON 文件 + 进程内读写锁。
/// 这个量级（几十条、偶尔改一次）用文件比数据库省事：能直接看、能手工改、备份就是拷一个文件。
/// 写入先落到临时文件再原子替换，断电也不会写坏。
/// </summary>
public sealed class CatalogStore
{
    private static readonly Regex ColorPattern = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new("^https?://", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] ResolveKinds = { "none", "kook", "nvidia", "qiyou" };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private CatalogFile _data = new();

    public CatalogStore(string path)
    {
        _path = path;

        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string FilePath => _path;

    /// <summary>启动时调用：有文件就加载，没有就写一份内置清单。</summary>
    public void Initialize()
    {
        if (File.Exists(_path))
        {
            try
            {
                string json = File.ReadAllText(_path);
                CatalogFile? loaded = JsonSerializer.Deserialize<CatalogFile>(json, Json.Options);
                if (loaded is { Items.Count: > 0 })
                {
                    _data = loaded;
                    Normalize(_data);
                    ServerLog.Info($"清单已加载：{_data.Items.Count} 款（v{_data.Version}）");
                    return;
                }
            }
            catch (Exception ex)
            {
                // 文件坏了先备份，再用内置清单重建，别让服务起不来
                string backup = _path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                try { File.Move(_path, backup); } catch (Exception) { }
                Console.WriteLine($"[警告] 数据文件解析失败（{ex.Message}），已备份为 {Path.GetFileName(backup)}");
                ServerLog.Warning($"清单文件解析失败，已备份为 {Path.GetFileName(backup)}：{ex.Message}");
            }
        }

        _data = new CatalogFile { Version = 1, UpdatedAt = DateTimeOffset.Now, Items = Seed() };
        Normalize(_data);
        Save(_data).GetAwaiter().GetResult();
        Console.WriteLine($"[初始化] 写入内置清单 {_data.Items.Count} 条 -> {_path}");
        ServerLog.Info($"已写入内置清单：{_data.Items.Count} 款");
    }

    public async Task<CatalogFile> ReadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _data;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppItem?> GetAsync(string id)
    {
        CatalogFile data = await ReadAsync().ConfigureAwait(false);
        return data.Items.FirstOrDefault(a => a.Id == id);
    }

    public async Task<AppItem> CreateAsync(AppItem item)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            item.Id = string.IsNullOrWhiteSpace(item.Id) ? NewId() : item.Id;
            item.UpdatedAt = DateTimeOffset.Now;
            if (item.Sort == 0)
            {
                item.Sort = _data.Items.Count == 0 ? 10 : _data.Items.Max(a => a.Sort) + 10;
            }

            _data.Items.Add(item);
            await PersistLockedAsync().ConfigureAwait(false);
            return item;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppItem?> UpdateAsync(string id, AppItem incoming)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            AppItem? existing = _data.Items.FirstOrDefault(a => a.Id == id);
            if (existing is null)
            {
                return null;
            }

            existing.Name = incoming.Name;
            existing.Vendor = incoming.Vendor;
            existing.Summary = incoming.Summary;
            existing.Category = incoming.Category;
            existing.Color = incoming.Color;
            existing.HomeUrl = incoming.HomeUrl;
            existing.DirectUrl = string.IsNullOrWhiteSpace(incoming.DirectUrl) ? null : incoming.DirectUrl;
            existing.IconUrl = string.IsNullOrWhiteSpace(incoming.IconUrl) ? null : incoming.IconUrl;
            existing.Resolve = incoming.Resolve;
            existing.SizeHint = incoming.SizeHint;
            existing.Note = incoming.Note;
            existing.Sort = incoming.Sort;
            existing.Enabled = incoming.Enabled;
            existing.UpdatedAt = DateTimeOffset.Now;

            await PersistLockedAsync().ConfigureAwait(false);
            return existing;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            int removed = _data.Items.RemoveAll(a => a.Id == id);
            if (removed == 0)
            {
                return false;
            }

            await PersistLockedAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>整表覆盖（第一次从桌面端导上来，或者从备份恢复）。</summary>
    public async Task<int> ReplaceAllAsync(List<AppItem> items)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var fresh = new List<AppItem>(items.Count);
            int sort = 10;

            foreach (AppItem item in items)
            {
                item.Id = string.IsNullOrWhiteSpace(item.Id) ? NewId() : item.Id;
                item.Sort = item.Sort == 0 ? sort : item.Sort;
                item.UpdatedAt = DateTimeOffset.Now;
                sort += 10;
                fresh.Add(item);
            }

            _data.Items = fresh;
            await PersistLockedAsync().ConfigureAwait(false);
            return fresh.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReorderAsync(List<string> ids)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            int sort = 10;
            foreach (string id in ids)
            {
                AppItem? item = _data.Items.FirstOrDefault(a => a.Id == id);
                if (item is not null)
                {
                    item.Sort = sort;
                    item.UpdatedAt = DateTimeOffset.Now;
                    sort += 10;
                }
            }

            await PersistLockedAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>校验提交上来的字段，返回人话错误。</summary>
    public static List<string> Validate(AppItem item)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(item.Name))
        {
            errors.Add("name 不能为空");
        }

        if (item.Name.Length > 60)
        {
            errors.Add("name 太长（最多 60 字）");
        }

        if (string.IsNullOrWhiteSpace(item.HomeUrl) || !UrlPattern.IsMatch(item.HomeUrl))
        {
            errors.Add("homeUrl 必须是 http:// 或 https:// 开头的地址");
        }

        if (!string.IsNullOrWhiteSpace(item.DirectUrl) && !UrlPattern.IsMatch(item.DirectUrl))
        {
            errors.Add("directUrl 必须是 http:// 或 https:// 开头的地址");
        }

        if (!string.IsNullOrWhiteSpace(item.IconUrl) && !UrlPattern.IsMatch(item.IconUrl))
        {
            errors.Add("iconUrl 必须是 http:// 或 https:// 开头的地址");
        }

        if (!ColorPattern.IsMatch(item.Color))
        {
            errors.Add("color 必须是 #RRGGBB");
        }

        if (!ResolveKinds.Contains(item.Resolve))
        {
            errors.Add("resolve 只能是 none / kook / nvidia / qiyou");
        }

        if (item.Category is not ("通讯" or "游戏" or "工具"))
        {
            errors.Add("category 只能是 通讯 / 游戏 / 工具");
        }

        return errors;
    }

    private static string NewId() => Guid.NewGuid().ToString("n")[..12];

    private async Task PersistLockedAsync()
    {
        _data.Version++;
        _data.UpdatedAt = DateTimeOffset.Now;
        await Save(_data).ConfigureAwait(false);
    }

    private async Task Save(CatalogFile data)
    {
        string json = JsonSerializer.Serialize(data, Json.Options);
        string temp = _path + ".tmp";

        await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);

        if (File.Exists(_path))
        {
            File.Replace(temp, _path, null);
        }
        else
        {
            File.Move(temp, _path);
        }
    }

    private static void Normalize(CatalogFile data)
    {
        data.Items ??= new List<AppItem>();

        for (int i = 0; i < data.Items.Count; i++)
        {
            AppItem item = data.Items[i];
            item.Id = string.IsNullOrWhiteSpace(item.Id) ? NewId() : item.Id;
            item.Color = ColorPattern.IsMatch(item.Color) ? item.Color : "#6C4CF1";
            item.Resolve = ResolveKinds.Contains(item.Resolve) ? item.Resolve : "none";
            item.Category = item.Category is "通讯" or "游戏" or "工具" ? item.Category : "工具";

            if (item.Sort == 0)
            {
                item.Sort = (i + 1) * 10;
            }
        }
    }

    /// <summary>首次启动写进去的清单，和桌面端内置的一致。</summary>
    private static List<AppItem> Seed() => new()
    {
        New("KOOK", "杭州逍遥一下科技 · 语音开黑", "开黑语音、主题服务器，游戏内覆盖", "通讯", "#5B6CF9",
            "https://www.kookapp.cn/download", null, "kook", null, 10),
        New("QQ", "腾讯 · 即时通讯", "聊天、群、文件传输，NT 新架构版本", "通讯", "#12B7F5",
            "https://im.qq.com/pcqq", null, "none", null, 20),
        New("微信", "腾讯 · 聊天与支付", "Windows 桌面版，聊天记录可与手机同步", "通讯", "#07C160",
            "https://pc.weixin.qq.com/", "https://dldir1.qq.com/weixin/Windows/WeChatSetup.exe", "none", "约 270 MB", 30),
        New("QQ音乐", "腾讯 · 在线音乐", "曲库、歌词与本地音乐管理", "工具", "#31C27C",
            "https://y.qq.com/download/index.html", null, "none", null, 40),
        New("Logitech G HUB", "罗技 · 外设驱动", "鼠标键盘耳机统一设置、宏与灯光", "工具", "#00A8E0",
            "https://www.logitechg.com/zh-cn/innovation/g-hub.html",
            "https://download01.logi.com/web/ftp/pub/techsupport/gaming/lghub_installer.exe", "none", "约 70 MB", 50),
        New("NVIDIA App", "NVIDIA · 驱动与游戏优化", "显卡驱动更新、游戏优化，替代 GeForce Experience", "工具",
            "#76B900", "https://www.nvidia.cn/software/nvidia-app/", null, "nvidia", null, 60),
        New("Visual Studio Code", "Microsoft · 代码编辑器", "轻量编辑器，插件生态丰富，用户级安装", "工具", "#007ACC",
            "https://code.visualstudio.com/",
            "https://code.visualstudio.com/sha/download?build=stable&os=win32-x64-user", "none", "约 240 MB", 70),
        New("PotPlayer 64 位", "Kakao · 视频播放器", "本地播放器，硬解、字幕与音轨都齐全", "工具", "#F57C00",
            "https://potplayer.tv/",
            "https://t1.daumcdn.net/potplayer/PotPlayer/Version/Latest/PotPlayerSetup64.exe", "none", null, 80),
        New("Bandizip", "Bandisoft · 压缩软件", "免费压缩解压，支持 40 多种格式", "工具", "#1B75BB",
            "https://cn.bandisoft.com/bandizip/", "https://www.bandisoft.com/bandizip/dl.php?web", "none", "约 11 MB", 90),
        New("Steam", "Valve · 游戏平台", "游戏商店与库，装完即可登录", "游戏", "#4B9FD5",
            "https://store.steampowered.com/about/",
            "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe", "none", "约 2.3 MB", 100),
        New("奇游电竞加速器", "奇游 · 游戏加速", "外服游戏加速，支持主流网游与主机", "游戏", "#2C6BF2",
            "https://www.qiyou.cn/", null, "qiyou", null, 110),
    };

    private static AppItem New(
        string name, string vendor, string summary, string category, string color,
        string home, string? direct, string resolve, string? sizeHint, int sort) => new()
        {
            Id = NewId(),
            Name = name,
            Vendor = vendor,
            Summary = summary,
            Category = category,
            Color = color,
            HomeUrl = home,
            DirectUrl = direct,
            Resolve = resolve,
            SizeHint = sizeHint,
            Sort = sort,
            Enabled = true,
            UpdatedAt = DateTimeOffset.Now,
        };
}
