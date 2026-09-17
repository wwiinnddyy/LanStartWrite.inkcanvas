using System.Runtime.InteropServices;
using Jalium.UI;

namespace LanStartWrite.Inkcanvas;

/// <summary>Keep a floating pen menu inside its toolbar monitor's work area.</summary>
internal static class FlyoutPlacement
{
    internal static bool Position(Window anchor, Window flyout)
    {
        if (anchor.Handle == IntPtr.Zero || flyout.Handle == IntPtr.Zero ||
            !GetWindowRect(anchor.Handle, out var anchorRect) ||
            !GetWindowRect(flyout.Handle, out var flyoutRect)) return false;
        var monitor = MonitorFromWindow(anchor.Handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var point = Calculate(anchorRect.ToRect(), new Size(flyoutRect.Right - flyoutRect.Left,
            flyoutRect.Bottom - flyoutRect.Top), info.Work.ToRect(), anchor.DpiScale);
        // The app is per-monitor-DPI-aware: use native physical coordinates, not a mixture
        // of the toolbar's DIP scale and a newly created flyout's previous monitor scale.
        return SetWindowPos(flyout.Handle, IntPtr.Zero, (int)Math.Round(point.X), (int)Math.Round(point.Y),
            0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    internal static Point Calculate(Rect anchor, Size size, Rect work, double dpiScale)
    {
        // Both floating windows have a 6 DIP transparent inset. Overlap those insets
        // to retain a 4 DIP visible gap between their actual rounded surfaces.
        var insetOverlap = 8 * dpiScale;
        var x = Math.Clamp(anchor.X, work.X, Math.Max(work.X, work.Right - size.Width));
        var y = anchor.Bottom - insetOverlap;
        if (y + size.Height > work.Bottom) y = anchor.Y - size.Height + insetOverlap;
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - size.Height));
        return new Point(x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
