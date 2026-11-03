using System;
using Microsoft.UI.Xaml;
using WinUIDemo.Views;
using Windows.Graphics;

namespace WinUIDemo.Helpers;

/// <summary>
/// 一个弹窗的「槽位」：负责懒创建、复用（已开着就提到前面）以及关闭后释放引用。
/// 页面里每个弹窗放一个只读字段即可，打开只要一行：
/// <code>private readonly ToolWindowSlot _info = new("系统信息", "\uE946", new SizeInt32(480, 285), () => new SystemInfoView());</code>
/// <code>_info.Show();</code>
/// </summary>
public sealed class ToolWindowSlot
{
    private readonly string _title;
    private readonly string _glyph;
    private readonly SizeInt32 _size;
    private readonly SizeInt32? _minSize;
    private readonly Func<UIElement> _contentFactory;

    private ToolWindow? _window;

    public ToolWindowSlot(string title, string glyph, SizeInt32 size, Func<UIElement> contentFactory, SizeInt32? minSize = null)
    {
        _title = title;
        _glyph = glyph;
        _size = size;
        _minSize = minSize;
        _contentFactory = contentFactory;
    }

    /// <summary>窗口被关闭时触发（可用于恢复主页面采样）。</summary>
    public event EventHandler? Closed;

    public bool IsOpen => _window is not null;

    /// <param name="anchor">左上角目标位置（屏幕物理像素）；为空则居中于主窗口。</param>
    public ToolWindow Show(PointInt32? anchor = null)
    {
        if (_window is not null)
        {
            if (anchor is PointInt32 point)
            {
                _window.MoveNear(point);
            }

            _window.Activate();
            return _window;
        }

        var window = new ToolWindow(_title, _glyph, _contentFactory(), _size, anchor, _minSize);
        window.WindowClosed += (_, _) =>
        {
            _window = null;
            Closed?.Invoke(this, EventArgs.Empty);
        };

        _window = window;
        window.Activate();
        return window;
    }
}
