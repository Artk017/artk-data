using System;
using System.Threading.Tasks;

namespace WinUIDemo.Services;

/// <summary>
/// 读取 Windows 的激活状态。
/// 走 WMI 的 SoftwareLicensingProduct：LicenseStatus == 1 表示已授权。
/// 用 COM 晚绑定（WbemScripting）而不是 System.Management，省掉一个依赖。
/// </summary>
public static class ActivationStatus
{
    private const string Query =
        "SELECT LicenseStatus, PartialProductKey FROM SoftwareLicensingProduct " +
        "WHERE PartialProductKey IS NOT NULL " +
        "AND ApplicationId='55c92734-d682-4d71-983e-d6ec3f16059f'";

    /// <summary>已激活返回 true；未激活返回 false；查不到（被策略禁用等）返回 null。</summary>
    public static bool? IsActivated()
    {
        try
        {
            Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
            {
                return null;
            }

            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic service = locator.ConnectServer(".", @"root\CIMV2");
            dynamic results = service.ExecQuery(Query);

            if ((int)results.Count == 0)
            {
                return null;
            }

            dynamic first = results.ItemIndex(0);
            dynamic property = first.Properties_.Item("LicenseStatus");
            uint status = Convert.ToUInt32(property.Value);

            // 1 = Licensed；2 = OOB Grace；3 = OOT Grace；4 = Non-Genuine Grace；5 = Notification；6 = Extended Grace
            return status == 1;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>在后台线程查询，避免 WMI 的百毫秒级延迟卡住界面。</summary>
    public static Task<bool?> IsActivatedAsync() => Task.Run(IsActivated);
}
