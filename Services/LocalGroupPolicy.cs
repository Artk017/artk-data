using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArtkTools.Services;

/// <summary>
/// 本地组策略存储（<c>%windir%\System32\GroupPolicy\Machine\Registry.pol</c>）。
/// <para>
/// 组策略编辑器（gpedit.msc）里显示「已启用 / 已禁用 / 未配置」读的就是这个文件，不是注册表；
/// 组策略刷新（开机、<c>gpupdate</c>）也是拿这个文件去覆盖 <c>HKLM\SOFTWARE\Policies\...</c>。
/// 所以只改注册表的话，gpedit 里永远显示「未配置」，而且下一次组策略刷新会把值改回去或删掉。
/// </para>
/// <para>
/// 文件格式：<c>PReg</c> + 版本号(DWORD) + 若干条
/// <c>[键;值名;类型(DWORD);字节数(DWORD);数据]</c>，分隔符和字符串都是 UTF-16LE。
/// 这里只处理 DWORD 值（本应用用到的策略全是 DWORD），其它类型的条目原样保留。
/// </para>
/// </summary>
internal static class LocalGroupPolicy
{
    private const int TypeDword = 4;

    private static string PolicyFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "GroupPolicy",
        "Machine",
        "Registry.pol");

    private sealed class GpoEntry
    {
        public string Key { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int Type { get; set; }

        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    /// <summary>在本地组策略里把某个 DWORD 策略设为指定值（没有就新增，有就改）。</summary>
    public static bool SetDword(string key, string name, int value)
    {
        try
        {
            var entries = ReadEntries();
            byte[] data = BitConverter.GetBytes(value);

            GpoEntry? existing = entries.Find(e =>
                string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                entries.Add(new GpoEntry { Key = key, Name = name, Type = TypeDword, Data = data });
            }
            else
            {
                existing.Type = TypeDword;
                existing.Data = data;
            }

            return WriteEntries(entries);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"local gpo set {key}\\{name} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>把某个策略条目从本地组策略里删掉 = 恢复「未配置」。</summary>
    public static bool Remove(string key, string name)
    {
        try
        {
            var entries = ReadEntries();
            int removed = entries.RemoveAll(e =>
                string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            return removed == 0 || WriteEntries(entries);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"local gpo remove {key}\\{name} failed: {ex.Message}");
            return false;
        }
    }

    private static List<GpoEntry> ReadEntries()
    {
        var entries = new List<GpoEntry>();

        if (!File.Exists(PolicyFile))
        {
            return entries;
        }

        byte[] bytes = File.ReadAllBytes(PolicyFile);
        if (bytes.Length < 8)
        {
            return entries;
        }

        int i = 8; // 跳过 PReg + 版本号
        while (i + 2 <= bytes.Length)
        {
            if (bytes[i] != 0x5B || bytes[i + 1] != 0x00) // '['
            {
                break;
            }

            i += 2;
            string key = ReadString(bytes, ref i);
            string name = ReadString(bytes, ref i);

            if (i + 4 > bytes.Length)
            {
                break;
            }

            int type = BitConverter.ToInt32(bytes, i);
            i += 6; // 类型 + ';'
            int size = BitConverter.ToInt32(bytes, i);
            i += 6; // 字节数 + ';'

            if (size < 0 || i + size + 2 > bytes.Length)
            {
                break;
            }

            var data = new byte[size];
            Array.Copy(bytes, i, data, 0, size);
            i += size + 2; // 数据 + ']'

            entries.Add(new GpoEntry { Key = key, Name = name, Type = type, Data = data });
        }

        return entries;
    }

    /// <summary>读一个 UTF-16LE 字符串，顺便吃掉后面的 \0 和 ';'。</summary>
    private static string ReadString(byte[] bytes, ref int i)
    {
        var sb = new StringBuilder();
        while (i + 1 < bytes.Length && !(bytes[i] == 0 && bytes[i + 1] == 0))
        {
            sb.Append((char)BitConverter.ToUInt16(bytes, i));
            i += 2;
        }

        i += 4; // \0 + ';'
        return sb.ToString();
    }

    private static bool WriteEntries(List<GpoEntry> entries)
    {
        string? directory = Path.GetDirectoryName(PolicyFile);
        if (directory is null)
        {
            return false;
        }

        Directory.CreateDirectory(directory);

        // 第一次动之前留个备份，改坏了还能手工还原
        string backup = PolicyFile + ".nova-bak";
        if (File.Exists(PolicyFile) && !File.Exists(backup))
        {
            File.Copy(PolicyFile, backup, overwrite: false);
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(new byte[] { 0x50, 0x52, 0x65, 0x67 }); // PReg
            writer.Write(1);                                     // 版本

            foreach (GpoEntry entry in entries)
            {
                WriteChar(writer, '[');
                WriteUtf16(writer, entry.Key);
                writer.Write((byte)0);
                writer.Write((byte)0);
                WriteChar(writer, ';');

                WriteUtf16(writer, entry.Name);
                writer.Write((byte)0);
                writer.Write((byte)0);
                WriteChar(writer, ';');

                writer.Write(entry.Type);
                WriteChar(writer, ';');

                writer.Write(entry.Data.Length);
                WriteChar(writer, ';');

                writer.Write(entry.Data);
                WriteChar(writer, ']');
            }
        }

        // 先写临时文件再替换，避免组策略服务正读到一半
        string temp = PolicyFile + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());

        if (File.Exists(PolicyFile))
        {
            File.Replace(temp, PolicyFile, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, PolicyFile);
        }

        return true;
    }

    private static void WriteChar(BinaryWriter writer, char value)
    {
        writer.Write((byte)value);
        writer.Write((byte)0);
    }

    private static void WriteUtf16(BinaryWriter writer, string text)
    {
        foreach (char ch in text)
        {
            writer.Write((byte)ch);
            writer.Write((byte)(ch >> 8));
        }
    }
}
