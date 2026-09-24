using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 白板底色那三档。<b>形状与 <see cref="InkPalette"/> 同源</b>：一处常量，
/// 设置页的色点、摘要里念出来的名字、存档读写时的归一化都读它 —— 抄两份就会有一处对不上。
/// <para>
/// 三档<b>全是浅底</b>，这不是审美选择而是正确性选择：默认墨色是黑的（<see cref="ToolbarTool.DefaultColorArgb"/>），
/// 而九色里六色偏深，深色底会让"刚打开白板就写不出可见的字"成为默认体验。
/// 真要一块黑板绿，得同时决定"深底时默认墨色翻白"，那是另一件事。
/// </para>
/// </summary>
internal static class CanvasBackgroundPalette
{
    /// <summary>三档颜色，顺序即设置页色点的顺序。</summary>
    internal static readonly Color[] Colors =
    [
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 白纸
        Color.FromRgb(0xEC, 0xEA, 0xE4), // 米灰
        Color.FromRgb(0xDD, 0xEE, 0xE1), // 浅绿
    ];

    /// <summary>与 <see cref="Colors"/> 同序的名字。</summary>
    internal static readonly string[] Names = ["白色", "米灰", "浅绿"];

    /// <summary>没配过时的底色：白纸。</summary>
    internal static readonly uint DefaultArgb = Argb.Pack(Colors[0]);

    /// <summary>最近的那一格（下标）。色板之外的颜色落到最近的一格上。</summary>
    internal static int NearestIndex(uint argb)
    {
        var color = Argb.Unpack(argb);
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < Colors.Length; i++)
        {
            var candidate = Colors[i];
            var dr = color.R - candidate.R;
            var dg = color.G - candidate.G;
            var db = color.B - candidate.B;
            var distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }

        return best;
    }

    /// <summary>
    /// 把任意一个存进来的值归到某一档上。<b>读档与运行时写都要过这道</b>：
    /// 白板是一块要盖住桌面的底，所以它<b>不能带透明度</b> —— 一个 alpha 为 0 的存档
    /// 会让白板变成屏幕上一个大洞，而不报错，只是"我写的字飘在桌面上"。
    /// 落回最近的一格顺带把这件事挡住了。
    /// </summary>
    internal static uint Normalize(uint argb) => Argb.Pack(Colors[NearestIndex(argb)]);

    /// <summary>这一档的名字，供摘要句与色点提示用。</summary>
    internal static string NameAt(int index) => Names[index];

    /// <summary>给定颜色对应的名字（不在档上时取最近的一格，因此永远不会念出空名字）。</summary>
    internal static string NearestName(uint argb) => Names[NearestIndex(argb)];

    /// <summary>色点画刷。每次新建，理由同 <see cref="InkPalette.BrushAt"/>。</summary>
    internal static Brush BrushAt(int index) => new SolidColorBrush(Colors[index]);
}
