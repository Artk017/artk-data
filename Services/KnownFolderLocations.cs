using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinUIDemo.Services;

public enum KnownFolderKind
{
    Downloads,
    Documents,
    Pictures,
    Videos,
}

/// <summary>
/// 读取 / 修改 Windows「已知文件夹」（下载 / 文档 / 图片 / 视频）的实际存放位置。
/// 走 shell32 的 SHGetKnownFolderPath / SHSetKnownFolderPath，效果等同资源管理器里
/// 该文件夹 属性 →「位置 → 移动」，会同步更新资源管理器、快速访问等。
/// </summary>
public static class KnownFolderLocations
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid DocumentsId = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid PicturesId = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid VideosId = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

    private const uint KfFlagDefaultPath = 0x00000400;
    private const uint KfFlagDontVerify = 0x00004000;
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    public static string DisplayName(KnownFolderKind kind) => kind switch
    {
        KnownFolderKind.Downloads => "下载",
        KnownFolderKind.Documents => "文档",
        KnownFolderKind.Pictures => "图片",
        KnownFolderKind.Videos => "视频",
        _ => kind.ToString(),
    };

    /// <summary>当前实际路径；失败返回空串。</summary>
    public static string GetPath(KnownFolderKind kind) => Get(kind, 0) ?? string.Empty;

    /// <summary>Windows 的默认路径（多用于「恢复默认」）。</summary>
    public static string GetDefaultPath(KnownFolderKind kind)
        // 默认目录可能已被重定向掉、实际不存在，所以带上 DONT_VERIFY 才拿得到
        => Get(kind, KfFlagDefaultPath | KfFlagDontVerify) ?? string.Empty;

    /// <summary>
    /// 把某个已知文件夹改到 <paramref name="newPath"/>。
    /// <paramref name="moveFiles"/> 为真时，会先把旧位置里的文件和子文件夹搬到新位置。
    /// </summary>
    public static bool SetPath(KnownFolderKind kind, string newPath, bool moveFiles, out string error, out bool filesMoved)
    {
        error = string.Empty;
        filesMoved = false;

        if (string.IsNullOrWhiteSpace(newPath))
        {
            error = "路径不能为空。";
            return false;
        }

        try
        {
            newPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newPath));
        }
        catch (Exception ex)
        {
            error = "路径无效：" + ex.Message;
            return false;
        }

        string oldPath = GetPath(kind);

        try
        {
            Directory.CreateDirectory(newPath);
        }
        catch (Exception ex)
        {
            error = "无法创建目标目录：" + ex.Message;
            return false;
        }

        if (moveFiles && oldPath.Length > 0 &&
            !string.Equals(oldPath.TrimEnd('\\'), newPath, StringComparison.OrdinalIgnoreCase))
        {
            if (IsNested(oldPath, newPath))
            {
                error = "新旧位置互相包含，已跳过搬移文件。";
            }
            else
            {
                filesMoved = MoveContents(oldPath, newPath, out int failed);
                if (failed > 0)
                {
                    error = $"有 {failed} 项没能搬过去（可能被占用），其余已移动。";
                }
            }
        }

        Guid id = GuidOf(kind);
        int hr = SHSetKnownFolderPath(ref id, 0, IntPtr.Zero, newPath);
        if (hr != 0)
        {
            error = $"设置失败（HRESULT 0x{hr:X8}）。" + (error.Length > 0 ? " " + error : string.Empty);
            return false;
        }

        // 通知外壳刷新（快速访问 / 资源管理器里的「下载」等）
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private static bool IsNested(string a, string b)
    {
        string na = a.TrimEnd('\\') + "\\";
        string nb = b.TrimEnd('\\') + "\\";
        return na.StartsWith(nb, StringComparison.OrdinalIgnoreCase) ||
               nb.StartsWith(na, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MoveContents(string from, string to, out int failed)
    {
        failed = 0;
        bool movedAny = false;

        if (!Directory.Exists(from))
        {
            return false;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(from);
        }
        catch
        {
            return false;
        }

        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            string target = Path.Combine(to, name);

            try
            {
                if (Directory.Exists(entry))
                {
                    if (Directory.Exists(target))
                    {
                        failed++;
                        continue;
                    }

                    Directory.Move(entry, target);
                }
                else
                {
                    File.Move(entry, target, overwrite: true);
                }

                movedAny = true;
            }
            catch
            {
                failed++;
            }
        }

        return movedAny;
    }

    private static string? Get(KnownFolderKind kind, uint flags)
    {
        Guid id = GuidOf(kind);
        IntPtr pointer;

        if (SHGetKnownFolderPath(ref id, flags, IntPtr.Zero, out pointer) != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static Guid GuidOf(KnownFolderKind kind) => kind switch
    {
        KnownFolderKind.Downloads => DownloadsId,
        KnownFolderKind.Documents => DocumentsId,
        KnownFolderKind.Pictures => PicturesId,
        KnownFolderKind.Videos => VideosId,
        _ => DownloadsId,
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHSetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, string pszPath);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
