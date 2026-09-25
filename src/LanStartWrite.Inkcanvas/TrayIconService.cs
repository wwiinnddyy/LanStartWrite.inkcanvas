using System.Runtime.InteropServices;
using Jalium.UI;
using Jalium.UI.Controls;

namespace LanStartWrite.Inkcanvas;

internal sealed class TrayIconService : IDisposable
{
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const int GwlpWndProc = -4;
    private static readonly IntPtr ApplicationIcon = new(32512);

    private readonly Window _owner;
    private readonly Action _openSettings;
    private readonly Action _restart;
    private readonly Action _exit;
    private readonly ContextMenu _menu;
    private readonly NotifyIcon? _crossPlatformIcon;
    private readonly WindowProc _windowProc;
    private IntPtr _oldWindowProc;
    private NotifyIconData _data;
    private uint _callbackMessage;
    private bool _registered;
    private bool _disposed;
    private int _registrationError;

    internal TrayIconService(
        Window owner,
        Action openSettings,
        Action restart,
        Action exit,
        bool registerIcon = true)
    {
        _owner = owner;
        _openSettings = openSettings;
        _restart = restart;
        _exit = exit;
        _menu = BuildMenu();
        _windowProc = OnWindowMessage;

        if (!OperatingSystem.IsWindows())
        {
            _crossPlatformIcon = new NotifyIcon
            {
                Text = "揽星书写",
                ContextMenu = _menu,
                Visible = registerIcon,
            };
            _crossPlatformIcon.Click += (_, _) => OpenMenu();
            _crossPlatformIcon.DoubleClick += (_, _) => OpenMenu();
            return;
        }

        if (!registerIcon || owner.Handle == IntPtr.Zero) return;

        _callbackMessage = RegisterWindowMessage("LanStartWrite.TrayIcon.Callback");
        if (_callbackMessage == 0)
        {
            _registrationError = Marshal.GetLastWin32Error();
            return;
        }

        _oldWindowProc = SetWindowLongPtr(
            owner.Handle,
            GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
        if (_oldWindowProc == IntPtr.Zero)
        {
            _registrationError = Marshal.GetLastWin32Error();
            return;
        }
        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = owner.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = _callbackMessage,
            hIcon = LoadIcon(IntPtr.Zero, ApplicationIcon),
            szTip = "揽星书写",
        };
        _registered = Shell_NotifyIcon(NimAdd, ref _data);
        if (!_registered)
        {
            _registrationError = Marshal.GetLastWin32Error();
            SetWindowLongPtr(owner.Handle, GwlpWndProc, _oldWindowProc);
            _oldWindowProc = IntPtr.Zero;
        }
    }

    internal ContextMenu Menu => _menu;

    internal bool IsRegistered => _registered || _crossPlatformIcon?.Visible == true;

    internal uint CallbackMessage => _callbackMessage;

    internal int RegistrationError => _registrationError;

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var settings = new MenuItem { Header = "打开设置" };
        settings.Click += (_, _) => _openSettings();
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());

        var restart = new MenuItem { Header = "重新启动应用" };
        restart.Click += (_, _) => _restart();
        menu.Items.Add(restart);
        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "退出应用" };
        exit.Click += (_, _) => _exit();
        menu.Items.Add(exit);
        return menu;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam)
    {
        if (message != _callbackMessage)
        {
            return CallWindowProc(_oldWindowProc, hwnd, message, wparam, lparam);
        }

        int eventCode = unchecked((ushort)wparam.ToInt64());
        if (eventCode is WmLButtonUp or WmLButtonDblClk or WmRButtonUp or WmContextMenu)
        {
            OpenMenu();
        }

        return IntPtr.Zero;
    }

    private void OpenMenu()
    {
        if (OperatingSystem.IsWindows())
        {
            OpenMenuAtCursor();
            return;
        }

        _menu.IsOpen = true;
    }

    private void OpenMenuAtCursor()
    {
        if (!GetCursorPos(out var point)) return;
        if (!ScreenToClient(_owner.Handle, ref point)) return;

        double scale = Math.Max(0.1, _owner.DpiScale);
        _menu.Open(new Point(point.X / scale, point.Y / scale));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registered)
        {
            _data.cbSize = (uint)Marshal.SizeOf<NotifyIconData>();
            Shell_NotifyIcon(NimDelete, ref _data);
            _registered = false;
        }

        if (_oldWindowProc != IntPtr.Zero && _owner.Handle != IntPtr.Zero)
        {
            SetWindowLongPtr(_owner.Handle, GwlpWndProc, _oldWindowProc);
            _oldWindowProc = IntPtr.Zero;
        }

        _menu.Close();
        _crossPlatformIcon?.Dispose();
    }

    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(
        IntPtr previous,
        IntPtr hwnd,
        uint message,
        IntPtr wparam,
        IntPtr lparam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref PointNative point);
}
