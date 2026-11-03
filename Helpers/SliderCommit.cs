using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinUIDemo.Helpers;

/// <summary>
/// 滑动条提交策略：拖动过程中只预览（更新文字），不触发写入；
/// 松手（DragCompleted）或非拖动方式改值（键盘、点击轨道）时才提交最终值。
/// </summary>
public sealed class SliderCommit
{
    private readonly Slider _slider;
    private readonly Action<int> _preview;
    private readonly Action<int> _commit;

    private bool _dragging;
    private bool _hasPending;
    private int _pending;
    private bool _silent;

    public SliderCommit(Slider slider, Action<int> preview, Action<int> commit)
    {
        _slider = slider;
        _preview = preview;
        _commit = commit;

        _slider.ValueChanged += OnValueChanged;
        _slider.Loaded += OnLoaded;
    }

    /// <summary>程序化赋值（回填/纠偏）时不要触发回调。</summary>
    public void SetSilent(int value)
    {
        _silent = true;
        _slider.Value = value;
        _silent = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 1) Thumb 自身的拖动事件：最精确，覆盖“按住滑块拖动”
        var thumb = FindThumb(_slider);
        if (thumb is not null)
        {
            thumb.DragStarted += (_, _) => _dragging = true;
            thumb.DragCompleted += (_, _) =>
            {
                _dragging = false;
                CommitPending();
            };
        }

        // 2) 指针事件：覆盖“按在轨道上再拖”的情况——这条路径不触发 Thumb 的 Drag 事件
        _slider.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => _dragging = true), handledEventsToo: true);
        _slider.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, _) =>
            {
                _dragging = false;
                CommitPending();
            }), handledEventsToo: true);
        _slider.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler((_, _) =>
            {
                _dragging = false;
                CommitPending();
            }), handledEventsToo: true);
    }

    private void OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_silent)
        {
            return;
        }

        int value = (int)Math.Round(e.NewValue);
        _preview(value);

        // 按在轨道上时，滑块会先跳到点击位置——这次值变化早于 PointerPressed 到达我们这里，
        // 所以再用“鼠标左键是否按住”兜底一次，保证这段也只在松手后提交。
        if (_dragging || IsLeftButtonDown())
        {
            _pending = value;
            _hasPending = true;
            return;
        }

        _hasPending = false;
        _commit(value);
    }

    private void CommitPending()
    {
        if (!_hasPending)
        {
            return;
        }

        _hasPending = false;
        _commit(_pending);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static bool IsLeftButtonDown()
    {
        return (GetAsyncKeyState(0x01) & 0x8000) != 0;
    }

    private static Thumb? FindThumb(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Thumb thumb)
            {
                return thumb;
            }

            var nested = FindThumb(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
