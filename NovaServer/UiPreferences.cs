using System.Text.Json;

namespace NovaServer;

/// <summary>关闭窗口时的行为。</summary>
internal enum CloseAction
{
    MinimizeToTray,
    Exit,
}

/// <summary>
/// 界面自己的偏好（目前只有关闭行为），跟着 server.json 放在安装根目录的 ui.json，
/// 和清单数据一样不会因为升级程序而丢。
/// </summary>
internal static class UiPreferences
{
    private static string _path = string.Empty;

    public static CloseAction CloseBehavior { get; set; } = CloseAction.MinimizeToTray;

    public static void Load(string homeDir)
    {
        _path = Path.Combine(homeDir, "ui.json");

        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            PreferencesFile? data = JsonSerializer.Deserialize<PreferencesFile>(File.ReadAllText(_path));
            CloseBehavior = data?.CloseAction == "exit" ? CloseAction.Exit : CloseAction.MinimizeToTray;
        }
        catch
        {
            // ui.json 坏了就按默认：最小化至托盘
            CloseBehavior = CloseAction.MinimizeToTray;
        }
    }

    public static void Save()
    {
        if (_path.Length == 0)
        {
            return;
        }

        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new PreferencesFile
            {
                CloseAction = CloseBehavior == CloseAction.Exit ? "exit" : "tray",
            }));
        }
        catch
        {
            // 写不进安装目录就算了，本次运行仍然按界面上的选择走
        }
    }

    private sealed class PreferencesFile
    {
        public string CloseAction { get; set; } = "tray";
    }
}
