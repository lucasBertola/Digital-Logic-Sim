using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// A zip that keeps Unix permissions, for the Mac and Linux releases built on Windows (user, 2026-10-06).
// Compress-Archive / .NET Framework's ZipArchive write "made by Windows" entries: macOS and Linux then ignore the
// permission bits, and the app's executable comes out without its x bit (it does not start). Each entry here is
// "made by Unix" with mode 0755 (executables) or 0644, directories 0755. Deflate, no zip64 (a player is far below 4 GB).
public static class UnixZip
{
    public static string Create(string sourceDir, string zipPath, string prefix, Func<string, bool> isExecutable)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        var entries = new List<(string name, long offset, uint crc, uint csize, uint usize, ushort method, uint mode)>();
        int exec = 0;
        if (File.Exists(zipPath)) File.Delete(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath)));
        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            // Unity's "..._DoNotShip" folders (Burst / IL2CPP debug info) are not part of the player
            var dirs = Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories).Select(d => Rel(sourceDir, d) + "/").Where(r => !r.Contains("DoNotShip"));
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories).Select(f => Rel(sourceDir, f)).Where(r => !r.Contains("DoNotShip"));
            foreach (string rel in dirs.OrderBy(x => x, StringComparer.Ordinal))
                entries.Add(WriteEntry(w, prefix + rel, Array.Empty<byte>(), 0x41ED)); // drwxr-xr-x
            foreach (string rel in files.OrderBy(x => x, StringComparer.Ordinal))
            {
                bool x = isExecutable(rel);
                if (x) exec++;
                entries.Add(WriteEntry(w, prefix + rel, File.ReadAllBytes(Path.Combine(sourceDir, rel)), x ? 0x81EDu : 0x81A4u)); // -rwxr-xr-x / -rw-r--r--
            }

            long cdStart = fs.Position;
            foreach (var e in entries)
            {
                byte[] name = Encoding.UTF8.GetBytes(e.name);
                w.Write(0x02014b50u);
                w.Write((ushort)(3 << 8 | 20)); // made by Unix, zip 2.0: the external attributes hold the Unix mode
                w.Write((ushort)20);
                w.Write((ushort)0x0800);        // UTF-8 names
                w.Write(e.method);
                w.Write(DosTime); w.Write(DosDate);
                w.Write(e.crc); w.Write(e.csize); w.Write(e.usize);
                w.Write((ushort)name.Length); w.Write((ushort)0); w.Write((ushort)0);
                w.Write((ushort)0); w.Write((ushort)0);
                w.Write(e.mode << 16);
                w.Write((uint)e.offset);
                w.Write(name);
            }
            long cdSize = fs.Position - cdStart;
            w.Write(0x06054b50u);
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)entries.Count); w.Write((ushort)entries.Count);
            w.Write((uint)cdSize); w.Write((uint)cdStart);
            w.Write((ushort)0);
        }
        return $"zip {zipPath}: {entries.Count} entries, {exec} executable, {new FileInfo(zipPath).Length / (1024 * 1024)} MB";
    }

    static string Rel(string root, string path) => path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');

    const ushort DosTime = 0, DosDate = (2026 - 1980) << 9 | 1 << 5 | 1;

    static (string, long, uint, uint, uint, ushort, uint) WriteEntry(BinaryWriter w, string name, byte[] data, uint mode)
    {
        long offset = w.BaseStream.Position;
        uint crc = Crc32(data);
        byte[] stored = data;
        ushort method = 0;
        if (data.Length > 0)
        {
            using var ms = new MemoryStream();
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(data, 0, data.Length);
            if (ms.Length < data.Length) { stored = ms.ToArray(); method = 8; }
        }
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        w.Write(0x04034b50u);
        w.Write((ushort)20);
        w.Write((ushort)0x0800);
        w.Write(method);
        w.Write(DosTime); w.Write(DosDate);
        w.Write(crc); w.Write((uint)stored.Length); w.Write((uint)data.Length);
        w.Write((ushort)nameBytes.Length); w.Write((ushort)0);
        w.Write(nameBytes);
        w.Write(stored);
        return (name, offset, crc, (uint)stored.Length, (uint)data.Length, method, mode);
    }

    static uint[] table;
    static uint Crc32(byte[] data)
    {
        if (table == null)
        {
            table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
