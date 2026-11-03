using System;
using System.Collections.Generic;
using System.Text;

namespace WinUIDemo.Services;

/// <summary>把 adb / fastboot 的英文输出翻译成中文（只翻译常见提示，未知内容原样保留）。</summary>
public static class PhoneCommandText
{
    private static readonly (string From, string To)[] Phrases =
    {
        ("< waiting for any device >", "< 正在等待设备连接… >"),
        ("< waiting for device >", "< 正在等待设备… >"),
        ("no devices/emulators found", "未找到任何设备/模拟器"),
        ("more than one device/emulator", "当前连接了多台设备，请用 -s <序列号> 指定"),
        ("device unauthorized", "设备未授权（请在手机上允许 USB 调试）"),
        ("device still authorizing", "设备仍在授权中，请稍候"),
        ("error: closed", "错误：连接已关闭"),
        ("error: device offline", "错误：设备已离线"),
        ("cannot connect to daemon", "无法连接 adb 服务"),
        ("failed to get feature set", "获取设备特性失败"),
        ("protocol fault", "协议错误"),
        ("insufficient permissions", "权限不足"),
        ("Permission denied", "权限不足"),
        ("Finished. Total time:", "已完成，耗时："),
        ("Sending '", "正在发送 '"),
        ("Writing '", "正在写入 '"),
        ("FAILED (remote:", "失败（设备返回："),
        ("FAILED (status read failed", "失败（状态读取失败"),
        ("< waiting for", "< 正在等待"),
        ("fastboot: error:", "fastboot 错误："),
        ("adb: error:", "adb 错误："),
    };

    /// <summary>本地化命令输出。tool 为 adb / fastboot，args 是用户输入的命令参数（不含 -s）。</summary>
    public static string Localize(string tool, IReadOnlyList<string> args, string output)
    {
        if (args.Count > 0 && args[0].Equals("devices", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizeDevices(output ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return output;
        }

        string text = output;

        foreach ((string from, string to) in Phrases)
        {
            text = text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>把 `adb install` 的输出翻成中文（保留原始错误码，方便查资料）。</summary>
    public static string LocalizeInstall(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return output;
        }

        string text = output;

        foreach ((string from, string to) in InstallPhrases)
        {
            text = text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private static readonly (string From, string To)[] InstallPhrases =
    {
        ("Performing Streamed Install", "正在安装…"),
        ("Performing Push Install", "正在推送并安装…"),
        ("Success", "安装成功"),
        ("INSTALL_FAILED_ALREADY_EXISTS", "已存在同名应用，请先卸载"),
        ("INSTALL_FAILED_UPDATE_INCOMPATIBLE", "与已安装版本签名不一致，请先卸载旧版"),
        ("INSTALL_FAILED_VERSION_DOWNGRADE", "版本低于已安装版本（加 -d 可允许降级）"),
        ("INSTALL_FAILED_INSUFFICIENT_STORAGE", "手机存储空间不足"),
        ("INSTALL_FAILED_USER_RESTRICTED", "系统限制安装（请在开发者选项允许「通过 USB 安装」）"),
        ("INSTALL_FAILED_INVALID_APK", "APK 文件无效"),
        ("INSTALL_PARSE_FAILED_NOT_APK", "这不是有效的 APK 文件"),
        ("INSTALL_PARSE_FAILED_NO_CERTIFICATES", "APK 缺少签名"),
        ("INSTALL_FAILED_TEST_ONLY", "测试版 APK，需要加 -t 才能安装"),
        ("INSTALL_FAILED_ABORTED", "安装被取消或在手机上拒绝了"),
        ("INSTALL_FAILED_OLDER_SDK", "APK 要求的系统版本高于手机"),
        ("INSTALL_FAILED_VERIFICATION_FAILURE", "系统校验失败"),
        ("INSTALL_FAILED_INTERNAL_ERROR", "设备内部错误"),
        ("device offline", "设备已离线"),
        ("Permission Denial", "权限被拒绝"),
        ("Exception", "异常"),
        ("failed to install", "安装失败"),
        ("Failure", "安装失败"),
        ("Error:", "错误："),
    };

    /// <summary>把 `adb devices` / `fastboot devices` 的表格改成中文列表。</summary>
    private static string LocalizeDevices(string output)
    {
        var rows = new List<string>();

        foreach (string line in output.Split('\n'))
        {
            string item = line.Trim();

            if (item.Length == 0
                || item.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(item);
        }

        if (rows.Count == 0)
        {
            return "未检测到已连接的设备。";
        }

        var sb = new StringBuilder("已连接的设备列表：");

        foreach (string row in rows)
        {
            string[] parts = row.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string serial = parts[0];
            string state = parts.Length > 1 ? parts[1] : string.Empty;

            sb.Append('\n').Append("  ").Append(serial);

            if (state.Length > 0)
            {
                sb.Append("    ").Append(StateToChinese(state));
            }
        }

        return sb.ToString();
    }

    private static string StateToChinese(string state) => state.ToLowerInvariant() switch
    {
        "device" => "已连接、",
        "unauthorized" => "未授权、",
        "offline" => "已离线、",
        "recovery" => "recovery 模式",
        "sideload" => "sideload 模式",
        "bootloader" => "fastboot 模式",
        "fastboot" => "fastboot 模式",
        "fastbootd" => "fastbootd 模式",
        "no" => "无权限",
        _ => state,
    };
}
