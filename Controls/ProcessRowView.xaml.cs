using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ArtkTools.Services;

namespace ArtkTools.Controls;

public sealed partial class ProcessRowView : UserControl
{
    public ProcessRowView()
    {
        InitializeComponent();
    }

    public string CurrentProcessName => NameText.Text;

    public void Update(ProcessUsage usage, ImageSource? icon)
    {
        if (NameText.Text != usage.Name)
        {
            NameText.Text = usage.Name;
            IconImage.Source = null; // 换进程时先清掉旧图标
        }

        if (icon is not null)
        {
            IconImage.Source = icon;
        }

        CpuText.Text = $"{usage.CpuPercent:0.0}%";
        MemoryText.Text = $"{usage.MemoryMb:0} MB";
    }
}
