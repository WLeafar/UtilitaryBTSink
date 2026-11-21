using System;
using System.IO;
using System.Runtime.InteropServices;

namespace UtilitaryBTSink.Services;

/// <summary>
/// Manages the Windows notification area (System Tray) icon using Win32 Shell_NotifyIconW and ComCtl32 SetWindowSubclass.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 42;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    private const uint IDM_OPEN = 1001;
    private const uint IDM_RECONNECT = 1002;
    private const uint IDM_EXIT = 1003;
    private static readonly UIntPtr SubclassId = new(4201);

    private readonly IntPtr _hwnd;
    private readonly IntPtr _customIcon;
    private readonly IntPtr _trayIconHandle;
    private readonly SubclassProcDelegate _subclassProc;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? ReconnectRequested;
    public event Action? ExitRequested;

    public TrayIconManager(IntPtr hwnd, string initialTooltip)
    {
        _hwnd = hwnd;
        _customIcon = LoadCustomTrayIcon();
        _trayIconHandle = _customIcon != IntPtr.Zero
            ? _customIcon
            : LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION fallback

        _subclassProc = CustomSubclassProc;
        SetWindowSubclass(_hwnd, _subclassProc, SubclassId, IntPtr.Zero);

        var data = CreateIconData(initialTooltip);
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private static IntPtr LoadCustomTrayIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (!File.Exists(iconPath)) return IntPtr.Zero;

            int cx = GetSystemMetrics(SM_CXSMICON);
            int cy = GetSystemMetrics(SM_CYSMICON);
            if (cx <= 0) cx = 16;
            if (cy <= 0) cy = 16;

            return LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, cx, cy, LR_LOADFROMFILE);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        var data = CreateIconData(tooltip);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA CreateIconData(string tip)
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _trayIconHandle,
            szTip = tip.Length > 127 ? tip[..127] : tip
        };
    }

    private IntPtr CustomSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WM_TRAYICON)
        {
            uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                OpenRequested?.Invoke();
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, new UIntPtr(IDM_OPEN), "Open UtilitaryBTSink");
            AppendMenu(menu, MF_STRING, new UIntPtr(IDM_RECONNECT), "Reconnect Now");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            AppendMenu(menu, MF_STRING, new UIntPtr(IDM_EXIT), "Exit UtilitaryBTSink");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);
            uint cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);

            switch (cmd)
            {
                case IDM_OPEN: OpenRequested?.Invoke(); break;
                case IDM_RECONNECT: ReconnectRequested?.Invoke(); break;
                case IDM_EXIT: ExitRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreateIconData(string.Empty);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        if (_customIcon != IntPtr.Zero)
        {
            DestroyIcon(_customIcon);
        }
    }

    private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProcDelegate pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
