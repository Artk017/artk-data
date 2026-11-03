using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinUIDemo.Services;

namespace WinUIDemo.Views;

/// <summary>仿 Windows 命令提示符的 adb / fastboot 命令行窗口。</summary>
public sealed class CommandConsoleDialog : ContentDialog
{
    private static readonly SolidColorBrush WindowBrush = new(Windows.UI.Color.FromArgb(255, 12, 12, 12));
    private static readonly SolidColorBrush TextBrush = new(Windows.UI.Color.FromArgb(255, 204, 204, 204));
    private static readonly SolidColorBrush DimBrush = new(Windows.UI.Color.FromArgb(255, 130, 130, 130));
    private static readonly SolidColorBrush PromptBrush = new(Windows.UI.Color.FromArgb(255, 60, 200, 90));
    private static readonly SolidColorBrush LineBrush = new(Windows.UI.Color.FromArgb(255, 60, 60, 60));

    private static readonly (string Cmd, string Desc)[] AdbCommands =
    {
        ("adb devices", "检查设备连接"),
        ("adb reboot", "重启设备"),
        ("adb reboot recovery", "重启到 recovery 模式"),
        ("adb reboot bootloader", "重启到 fastboot 模式"),
        ("adb shell getprop ro.product.device", "查看设备代号"),
        ("adb shell dumpsys battery", "查看电池信息"),
        ("adb shell pm list packages", "列出已安装应用"),
        ("adb install -r <apk路径>", "安装 APK"),
        ("adb push <本地文件> /sdcard/", "推送文件到手机"),
        ("adb pull /sdcard/<文件> .", "从手机拉取文件"),
        ("adb start-server", "启动 adb 服务"),
        ("adb kill-server", "结束 adb 服务"),
    };

    private static readonly (string Cmd, string Desc)[] FastbootCommands =
    {
        ("fastboot devices", "检查设备连接"),
        ("fastboot reboot", "重启"),
        ("fastboot oem edl", "进入 9008 模式"),
        ("fastboot flash boot <boot镜像>", "刷入 boot 分区"),
        ("fastboot flash init_boot <镜像>", "刷入 init_boot 分区"),
        ("fastboot flash recovery <镜像>", "刷入 TWRP / recovery"),
        ("fastboot set_active a", "切换到 A 分区"),
        ("fastboot set_active b", "切换到 B 分区"),
        ("fastboot reboot recovery", "重启到 recovery 模式"),
        ("fastboot reboot bootloader", "重启到 fastboot 模式"),
        ("fastboot reboot fastboot", "进入 fastbootd"),
        ("fastboot flashing unlock", "解锁 BL"),
        ("fastboot flashing lock", "回锁 BL"),
        ("fastboot oem device-info", "查看 BL 状态（false 解锁 / true 锁定）"),
    };

    private readonly PhoneInfo? _info;
    private readonly TextBlock _log;
    private readonly ScrollViewer _scroller;
    private readonly TextBox _input;
    private readonly TextBlock _prompt;
    private readonly Button _runButton;
    private readonly List<string> _history = new();
    private int _historyIndex;
    private bool _busy;

    public CommandConsoleDialog(PhoneInfo? info)
    {
        _info = info;

        Resources["ContentDialogMaxWidth"] = 1040d;
        Background = WindowBrush;
        CloseButtonText = "关闭";
        DefaultButton = ContentDialogButton.Close;

        _log = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            Padding = new Thickness(12, 10, 12, 10),
        };

        _scroller = new ScrollViewer
        {
            Height = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 8, 8, 8)),
            Content = _log,
        };

        _prompt = new TextBlock
        {
            Text = "adb>",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = PromptBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0),
        };

        _input = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = TextBrush,
            Background = WindowBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 6, 2, 6),
            PlaceholderText = "输入命令，例如 adb devices",
            PlaceholderForeground = DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _input.KeyDown += Input_KeyDown;

        _runButton = new Button
        {
            Content = "执行",
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16, 4, 16, 4),
            Margin = new Thickness(6, 0, 12, 0),
        };

        _runButton.Click += (_, _) => _ = RunAsync();

        var hintsButton = new Button
        {
            Content = "常用命令",
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 4, 12, 4),
            Foreground = TextBrush,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
        };

        hintsButton.Flyout = BuildHintsFlyout();

        var clearButton = new Button
        {
            Content = "清空",
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 4, 12, 4),
            Foreground = TextBrush,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 0, 0, 0),
        };

        clearButton.Click += (_, _) => _log.Text = string.Empty;

        var header = new Grid { Padding = new Thickness(12, 10, 12, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = "Artk Tools 命令提示符 — adb / fastboot",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Foreground = DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var hint = new TextBlock
        {
            Text = "Enter 执行 · ↑↓ 历史命令 · cls 清屏",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(hint, 1);
        header.Children.Add(hint);

        var inputRow = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 18, 18)),
        };

        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        inputRow.Children.Add(_prompt);

        Grid.SetColumn(_input, 1);
        inputRow.Children.Add(_input);

        Grid.SetColumn(_runButton, 2);
        inputRow.Children.Add(_runButton);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Padding = new Thickness(12, 8, 12, 10),
        };

        footer.Children.Add(hintsButton);
        footer.Children.Add(clearButton);

        var root = new Grid { Background = WindowBrush };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(header);

        Grid.SetRow(_scroller, 1);
        root.Children.Add(_scroller);

        Grid.SetRow(inputRow, 2);
        root.Children.Add(inputRow);

        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = new Border
        {
            Background = WindowBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            MinWidth = 880,
            Child = root,
        };

        Append("输入 adb / fastboot 命令，例如：adb devices；↑↓ 翻历史，cls 清屏。");
        Append(string.Empty);

        Loaded += (_, _) => _input.Focus(FocusState.Programmatic);
    }

    private MenuFlyout BuildHintsFlyout()
    {
        var flyout = new MenuFlyout();

        var adb = new MenuFlyoutSubItem { Text = "ADB" };
        foreach ((string cmd, string desc) in AdbCommands)
        {
            adb.Items.Add(MakeItem(cmd, desc));
        }

        var fastboot = new MenuFlyoutSubItem { Text = "FASTBOOT" };
        foreach ((string cmd, string desc) in FastbootCommands)
        {
            fastboot.Items.Add(MakeItem(cmd, desc));
        }

        flyout.Items.Add(adb);
        flyout.Items.Add(fastboot);

        return flyout;
    }

    private MenuFlyoutItem MakeItem(string cmd, string desc)
    {
        var item = new MenuFlyoutItem { Text = cmd + "  ·  " + desc };

        item.Click += (_, _) =>
        {
            _input.Text = cmd;
            _input.Focus(FocusState.Programmatic);
            _input.SelectionStart = _input.Text.Length;
        };

        return item;
    }

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = RunAsync();
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Up)
        {
            if (_history.Count == 0)
            {
                return;
            }

            e.Handled = true;
            _historyIndex = Math.Max(0, _historyIndex - 1);
            _input.Text = _history[_historyIndex];
            _input.SelectionStart = _input.Text.Length;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Down)
        {
            if (_history.Count == 0)
            {
                return;
            }

            e.Handled = true;
            _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
            _input.Text = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
            _input.SelectionStart = _input.Text.Length;
        }
    }

    private async Task RunAsync()
    {
        if (_busy)
        {
            return;
        }

        string raw = _input.Text.Trim();
        if (raw.Length == 0)
        {
            return;
        }

        if (raw.Equals("cls", StringComparison.OrdinalIgnoreCase) || raw.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _log.Text = string.Empty;
            _input.Text = string.Empty;
            return;
        }

        _history.Add(raw);
        _historyIndex = _history.Count;

        string[] parts = SplitCommandLine(raw);
        string tool = parts[0].ToLowerInvariant();

        if (tool is not ("adb" or "fastboot"))
        {
            Append(_prompt.Text + " " + raw);
            Append("'" + parts[0] + "' 不是 adb 或 fastboot 命令。");
            _input.Text = string.Empty;
            return;
        }

        string? exe = tool == "adb" ? PhoneTools.AdbPath : PhoneTools.FastbootPath;
        if (string.IsNullOrEmpty(exe))
        {
            Append(_prompt.Text + " " + raw);
            Append("找不到 " + tool + "，请安装 platform-tools 或设置 NOVA_ADB / NOVA_FASTBOOT。");
            _input.Text = string.Empty;
            return;
        }

        var args = new List<string>(parts.Skip(1));
        string[] userArgs = parts.Skip(1).ToArray();

        // 有设备就自动带上序列号
        string serial = _info?.Serial ?? string.Empty;
        if (!args.Contains("-s") && serial.Length > 0 && serial != "—")
        {
            args.Insert(0, "-s");
            args.Insert(1, serial);
        }

        if (IsDestructive(parts))
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "确认执行",
                Content = "这条命令可能会修改设备数据或分区：\n\n" + raw + "\n\n确定要继续吗？",
                PrimaryButtonText = "执行",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        _busy = true;
        _runButton.IsEnabled = false;
        Append(_prompt.Text + " " + raw);
        _input.Text = string.Empty;

        try
        {
            (bool ok, string output) = await PhoneTools.RunRawAsync(exe, args.ToArray(), 30000);
            string text = PhoneCommandText.Localize(tool, userArgs, output).TrimEnd();

            if (text.Length > 20000)
            {
                text = text[^20000..];
            }

            Append(text.Length > 0 ? text : "(无输出)");
            Append(ok ? "[完成]" : "[失败]");
        }
        catch (Exception ex)
        {
            Append("执行出错：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _runButton.IsEnabled = true;
            _prompt.Text = tool + ">";
            _input.Focus(FocusState.Programmatic);
        }
    }

    private static bool IsDestructive(IEnumerable<string> parts)
    {
        string joined = string.Join(' ', parts).ToLowerInvariant();
        string[] keys = { "flash", "erase", "format", "wipe", "unlock", "edl", "set_active" };

        return keys.Any(key => joined.Contains(key, StringComparison.Ordinal));
    }

    private void Append(string text)
    {
        string current = _log.Text;

        if (current.Length > 100000)
        {
            current = current[^100000..];
        }

        _log.Text = current.Length == 0 ? text : current + Environment.NewLine + text;

        DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            _scroller.ChangeView(null, _scroller.ScrollableHeight, null, true));
    }

    private static string[] SplitCommandLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result.Count > 0 ? result.ToArray() : new[] { string.Empty };
    }
}
