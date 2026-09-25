using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 九色画笔色板。<b>它是应用级的一处常量，不是某个窗口的私有数组</b> ——
/// 笔菜单用它的颜色与名字，设置页的工具栏列表用它的名字把一支笔概括成一行字
/// （"红色 · 4 px"）。两处各存一份的话，改了色板就会有一处对不上。
/// </summary>
internal static class InkPalette
{
    /// <summary>色板颜色，顺序即格子里的顺序（3×3）。</summary>
    internal static readonly Color[] Colors =
    [
        Color.FromRgb(0x20, 0x20, 0x20), // 1
        Color.FromRgb(0xD1, 0x34, 0x38), // 2
        Color.FromRgb(0xF2, 0x6B, 0x1F), // 3
        Color.FromRgb(0xF2, 0xC8, 0x11), // 4
        Color.FromRgb(0x10, 0x7C, 0x10), // 5
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 6
        Color.FromRgb(0x00, 0x78, 0xD4), // 7
        Color.FromRgb(0x87, 0x64, 0xB8), // 8
        Color.FromRgb(0x73, 0x73, 0x73), // 9
    ];

    /// <summary>与 <see cref="Colors"/> 同序的名字，供工具提示与列表摘要使用。</summary>
    internal static readonly string[] Names =
        ["黑色", "红色", "橙色", "黄色", "绿色", "白色", "蓝色", "紫色", "灰色"];

    /// <summary>找与 <paramref name="argb"/> 最接近的那一格，返回它的下标。</summary>
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

    /// <summary>最近的那一格的名字。色板之外的颜色（将来接自定义取色时会有）落到最近的格子上，
    /// 因此摘要里永远不会出现空名字。</summary>
    internal static string NearestName(uint argb) => Names[NearestIndex(argb)];

    /// <summary>色板格子的画刷。每次新建 —— 画笔不会再改它，所以不需要缓存与失效一说。</summary>
    internal static Brush BrushAt(int index) => new SolidColorBrush(Colors[index]);
}
