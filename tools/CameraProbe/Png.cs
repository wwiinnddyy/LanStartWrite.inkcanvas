using System.IO.Compression;
using Jalium.UI;

namespace LanStartWrite.Inkcanvas.Tools;

/// <summary>最小 PNG 编码器：BGRA 缓冲 → RGB PNG。</summary>
/// <remarks>
/// 为什么自己写而不是引一个图像包：这份探针的全部意义是<b>回答问题</b>，
/// 而引一个包就把"这个仓库要多少依赖"这个问题重新打开一次。
/// 顺带一提，这也是主应用那条规矩（<c>csproj</c> 里"渲染链里不许有第二个图形栈"）
/// 在工具侧的同一个道理。
/// <para>每行前置一个 0 过滤字节（None），整幅 zlib 压一道 —— 对截图足够，
/// 而"压缩得再小一点"对"人眼能不能认出这是那一帧"毫无帮助。</para>
/// </remarks>
internal static class Png
{
    internal static byte[] EncodeBgra(byte[] bgra, int width, int height)
    {
        var raw = new byte[height * (width * 3 + 1)];
        for (var y = 0; y < height; y++)
        {
            var dst = y * (width * 3 + 1);
            raw[dst] = 0;                                   // filter: None
            var src = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var s = src + x * 4;
                var d = dst + 1 + x * 3;
                raw[d] = bgra[s + 2];                      // R
                raw[d + 1] = bgra[s + 1];                  // G
                raw[d + 2] = bgra[s];                      // B
            }
        }

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteBe(ihdr, 0, width);
        WriteBe(ihdr, 4, height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 2;    // truecolor
        Chunk(ms, "IHDR", ihdr);

        byte[] idat;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true))
            {
                zs.Write(raw, 0, raw.Length);
            }
            idat = z.ToArray();
        }
        Chunk(ms, "IDAT", idat);
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len);
        var td = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        var crc = new byte[4];
        WriteBe(crc, 0, unchecked((int)Crc32(td)));
        s.Write(crc);
    }

    private static void WriteBe(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
