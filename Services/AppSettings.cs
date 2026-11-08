using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ArtkTools.Services;

/// <summary>
/// 应用设置，持久化到 %LOCALAPPDATA%\Nova\settings.json。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 清单服务器地址：客户端固定连这一台，不在设置里暴露、也不写进 settings.json。
    /// 要换服务器只能改这里重新打包。
    /// </summary>
    public const string CatalogServerUrl = "http://156.226.183.55:5080";

    /// <summary>
    /// 内置的服务器 API Key（与服务端 server.json 的 apiKey 一致）。
    /// 客户端所有 /api 请求都带 X-Api-Key，写接口和读接口一起校验。
    /// </summary>
    public const string CatalogApiKey = "nova-061a213b92373820";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>System | Light | Dark</summary>
    public string Theme { get; set; } = "System";

    /// <summary>是否启用 Mica 背景材质</summary>
    public bool UseMica { get; set; } = true;

    /// <summary>启动时打开的模块：windows | game | safe</summary>
    public string StartupSection { get; set; } = "windows";

    /// <summary>
    /// 软件页无感安装的目标目录；空表示用各软件默认目录。
    /// 会随安装包类型翻译成对应静默参数（NSIS /D=、MSI INSTALLDIR、Inno /DIR=）。
    /// </summary>
    public string InstallDir { get; set; } = string.Empty;

    /// <summary>Windows 清理页已勾选项目的名称，用于下次打开时恢复勾选状态。</summary>
    public List<string> CleanerSelection { get; set; } = new();

    /// <summary>是否已经保存过勾选状态；用于区分“从未选择”和“刻意全部取消”。</summary>
    public bool CleanerSelectionSaved { get; set; }

    public static AppSettings Current { get; private set; } = Load();

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");

    /// <summary>Nova 的数据目录（设置 / 清单缓存 / 下载记录）。自卸载时可一并删除。</summary>
    public static string DataFolder => Folder;

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static string Location => FilePath;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏或不可读时回落到默认值，不让启动失败
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch
        {
            // 写盘失败不阻断界面操作
        }
    }

    public void Reset()
    {
        Theme = "System";
        UseMica = true;
        StartupSection = "windows";
        InstallDir = string.Empty;
        CleanerSelection.Clear();
        CleanerSelectionSaved = false;
        Save();
    }
}
