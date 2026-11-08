using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArtkTools.Controls;
using ArtkTools.Helpers;
using ArtkTools.Services;

namespace ArtkTools.Views;

/// <summary>
/// 「进程占用」弹窗的内容：按 CPU 排序的进程列表，行数随窗口高度自适应。
/// 外壳（标题栏 / 图钉 / 缩放 / 定位）由 <see cref="ToolWindow"/> 负责。
/// </summary>
public sealed partial class ProcessDetailView : UserControl
{
    private const int DefaultRowHeight = 26;
    private const int MaxRows = 40;

    private readonly ProcessMonitor _monitor = new();
    private readonly List<ProcessRowView> _rows = new();
    private readonly DispatcherQueueTimer _timer;

    public ProcessDetailView()
    {
        InitializeComponent();

        // 计时器跟随本控件所在的 UI 线程
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Refresh();

        // 高度变化后重算能放几行
        RowsHost.SizeChanged += (_, _) => SyncRowCount();

        Loaded += (_, _) =>
        {
            _monitor.Sample(MaxRows); // 建立 CPU 基线
            SyncRowCount();
            _timer.Start();
        };

        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>按当前可用高度增减行控件，保证末尾不留大片空白。</summary>
    private void SyncRowCount()
    {
        double available = RowsHost.ActualHeight;
        if (available <= 0)
        {
            return;
        }

        double rowHeight = _rows.Count > 0 && _rows[0].ActualHeight > 0
            ? _rows[0].ActualHeight
            : DefaultRowHeight;

        int count = Math.Clamp((int)Math.Floor(available / rowHeight), 1, MaxRows);

        if (count != _rows.Count)
        {
            while (_rows.Count < count)
            {
                var row = new ProcessRowView();
                _rows.Add(row);
                RowsPanel.Children.Add(row);
            }

            while (_rows.Count > count)
            {
                int last = _rows.Count - 1;
                RowsPanel.Children.Remove(_rows[last]);
                _rows.RemoveAt(last);
            }
        }

        Refresh();
    }

    private void Refresh()
    {
        // 先更新时间戳：即使一行都放不下，时间也要跟着走
        UpdatedText.Text = DateTime.Now.ToString("HH:mm:ss");

        if (_rows.Count == 0)
        {
            return;
        }

        var top = _monitor.Sample(_rows.Count);

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (i >= top.Count)
            {
                row.Visibility = Visibility.Collapsed;
                continue;
            }

            var usage = top[i];
            row.Visibility = Visibility.Visible;
            row.Update(usage, ProcessIconLoader.TryGetCached(usage.ExecutablePath));
            _ = LoadIconAsync(row, usage);
        }
    }

    private static async System.Threading.Tasks.Task LoadIconAsync(ProcessRowView row, ProcessUsage usage)
    {
        if (string.IsNullOrEmpty(usage.ExecutablePath) ||
            ProcessIconLoader.TryGetCached(usage.ExecutablePath) is not null)
        {
            return;
        }

        var icon = await ProcessIconLoader.GetAsync(usage.ExecutablePath);
        if (icon is not null && row.CurrentProcessName == usage.Name)
        {
            row.Update(usage, icon);
        }
    }
}
