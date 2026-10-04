using System.Runtime.InteropServices;
namespace Recorder.App;

internal sealed class NativeTray : IDisposable
{
    private const uint Callback = 0x8000 + 88;
    private readonly IntPtr window;
    private readonly SubclassProc procedure;
    private readonly Action show, stop, exit;
    private NotifyIconData data;
    public NativeTray(IntPtr window, Action show, Action stop, Action exit)
    {
        this.window = window; this.show = show; this.stop = stop; this.exit = exit;
        procedure = WindowProcedure;
        if (!SetWindowSubclass(window, procedure, 88, IntPtr.Zero)) throw new InvalidOperationException("托盘窗口注册失败。");
        data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1, Flags = 1 | 2 | 4, Callback = Callback, Icon = LoadIconW(IntPtr.Zero, new IntPtr(32512)), Tip = "课堂记录", Info = "", Title = "" };
        if (!Shell_NotifyIconW(0, ref data)) throw new InvalidOperationException("托盘图标创建失败。");
    }
    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam, nuint id, IntPtr reference)
    {
        if (message == Callback)
        {
            if ((uint)lparam.ToInt64() == 0x203) show();
            if ((uint)lparam.ToInt64() == 0x205)
            {
                IntPtr menu = CreatePopupMenu();
                AppendMenuW(menu, 0, 1, "打开课堂记录"); AppendMenuW(menu, 0, 2, "停止并保存"); AppendMenuW(menu, 0, 3, "退出");
                GetCursorPos(out Point position); SetForegroundWindow(window);
                uint selected = TrackPopupMenu(menu, 0x100 | 0x2, position.X, position.Y, 0, window, IntPtr.Zero);
                DestroyMenu(menu);
                if (selected == 1) show(); else if (selected == 2) stop(); else if (selected == 3) exit();
            }
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wparam, lparam);
    }
    public void Dispose() { Shell_NotifyIconW(2, ref data); RemoveWindowSubclass(window, procedure, 88); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, nuint id, IntPtr reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, IntPtr reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
