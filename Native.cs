using System.Runtime.InteropServices;

namespace FreeCamRecord;

internal static class Native
{
    [DllImport("winmm.dll")] internal static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] internal static extern uint timeEndPeriod(uint milliseconds);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size; public int Flags; public IntPtr Cursor; public Point Position; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { public bool Icon; public int HotX; public int HotY; public IntPtr Mask; public IntPtr Color; }

    internal static void DrawCursor(IntPtr dc, Rectangle area)
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || cursor.Flags != 1 || !area.Contains(cursor.Position)) return;
        if (!GetIconInfo(cursor.Cursor, out var icon)) return;
        try
        {
            DrawIconEx(dc, cursor.Position.X - area.X - icon.HotX, cursor.Position.Y - area.Y - icon.HotY, cursor.Cursor, 0, 0, 0, IntPtr.Zero, 3);
        }
        finally
        {
            if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
        }
    }
}
