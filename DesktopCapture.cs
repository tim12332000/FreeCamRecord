using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FreeCamRecord;

// Reuses the desktop DC, memory DC and a top-down DIB for the whole recording.
// Capture returns borrowed storage: callers must neither dispose nor retain it across captures.
internal sealed class DesktopCapture : IDisposable
{
    private readonly Rectangle area;
    private IntPtr desktopDc, memoryDc, dib, previous;
    private Bitmap? image;
    private bool disposed;

    internal DesktopCapture(Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0) throw new ArgumentOutOfRangeException(nameof(area));
        this.area = area;
        int stride = checked(area.Width * 4);
        _ = checked(stride * area.Height);
        try
        {
            desktopDc = GetDC(IntPtr.Zero);
            if (desktopDc == IntPtr.Zero) Fail("取得桌面繪圖裝置");
            memoryDc = CreateCompatibleDC(desktopDc);
            if (memoryDc == IntPtr.Zero) Fail("建立錄影繪圖裝置");
            var info = new BitmapInfo
            {
                Size = 40, Width = area.Width, Height = -area.Height,
                Planes = 1, Bits = 32
            };
            dib = CreateDIBSection(desktopDc, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || pixels == IntPtr.Zero) Fail("建立錄影像素緩衝區");
            previous = SelectObject(memoryDc, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) Fail("選取錄影像素緩衝區");
            // GDI screen copies do not define the alpha byte. RGB explicitly treats it as opaque.
            image = new Bitmap(area.Width, area.Height, stride, PixelFormat.Format32bppRgb, pixels);
        }
        catch { Dispose(); throw; }
    }

    internal Bitmap Capture()
    {
        if (disposed) throw new ObjectDisposedException(nameof(DesktopCapture));
        if (!BitBlt(memoryDc, 0, 0, area.Width, area.Height, desktopDc, area.X, area.Y, 0x00CC0020))
            Fail("擷取桌面畫面");
        Native.DrawCursor(memoryDc, area);
        // Complete this thread's batched GDI writes before the encoder reads the shared pixels.
        if (!GdiFlush()) Fail("同步錄影像素緩衝區");
        return image!;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        image?.Dispose();
        image = null;
        if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memoryDc, previous);
        if (dib != IntPtr.Zero) DeleteObject(dib);
        if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
        if (desktopDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, desktopDc);
        desktopDc = memoryDc = dib = previous = IntPtr.Zero;
    }

    private static void Fail(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation + "失敗。");

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool GdiFlush();
}
