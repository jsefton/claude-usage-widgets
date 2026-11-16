using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>The few Win32 calls WPF and WinForms don't wrap.</summary>
static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static void MoveWindow(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x00040000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    public static void AddExtendedStyle(IntPtr hwnd, long style, long remove = 0)
    {
        var current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((current | style) & ~remove));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    /// <summary>Lets any process take the foreground (passes on the right this process was given by the user's launch).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    public const int ASFW_ANY = -1;

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    // Windows 11 rounded corners for our borderless windows.
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void UseRoundedCorners(IntPtr hwnd)
    {
        var preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    // Whether a full-screen app (game, video, presentation) is in front.
    private enum QueryUserNotificationState
    {
        NotPresent = 1, Busy = 2, RunningD3DFullScreen = 3, PresentationMode = 4,
        AcceptsNotifications = 5, QuietTime = 6, App = 7,
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out QueryUserNotificationState state);

    public static bool IsFullScreenAppInFront() =>
        SHQueryUserNotificationState(out var state) == 0
        && state is QueryUserNotificationState.Busy or QueryUserNotificationState.RunningD3DFullScreen
            or QueryUserNotificationState.PresentationMode or QueryUserNotificationState.App;
}
