using Microsoft.UI.Xaml.Controls;
using WinUIDemo.Helpers;

namespace WinUIDemo.Views;

/// <summary>
/// 「系统信息」弹窗的内容：设备名 / 处理器 / 内存 / 显卡 / 操作系统。
/// 外壳由 <see cref="ToolWindow"/> 负责。
/// </summary>
public sealed partial class SystemInfoView : UserControl
{
    public SystemInfoView()
    {
        InitializeComponent();

        DeviceNameText.Text = SystemFacts.DeviceName;
        ProcessorText.Text = SystemFacts.ProcessorName;
        MemoryText.Text = SystemFacts.MemoryText;
        GraphicsText.Text = SystemFacts.GraphicsText;
        OsText.Text = SystemFacts.OsText;
    }
}
