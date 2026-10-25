using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FreeCamRecord;

// Desktop Duplication: copies only the selected GPU rectangle back to a reusable CPU bitmap.
// Construct, Capture and Dispose on one recording worker. Returned bitmaps are borrowed.
internal sealed class DxgiDesktopCapture : IDisposable
{
    private const int NotFound = unchecked((int)0x887A0002);
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0026);
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly int width, height, stride;
    private Box sourceBox;
    private IntPtr device, context, duplication, staging, pixels, memoryDc, dib, previous;
    private Bitmap? bitmap;
    private bool disposed;
    private bool hasDesktopImage;
    internal bool NeedsCursorOverlay { get; private set; }
    internal long LastPresentTime { get; private set; }
    internal uint AccumulatedFrames { get; private set; }

    internal DxgiDesktopCapture(Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0) throw new ArgumentOutOfRangeException(nameof(area));
        width = area.Width; height = area.Height;
        stride = checked(width * 4);
        _ = checked(stride * height);
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero, output1 = IntPtr.Zero;
        try
        {
            Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
            Check(CreateDXGIFactory1(ref factoryId, out factory), "建立 DXGI 工廠");
            bool matched = false;
            for (uint adapterIndex = 0; !matched; adapterIndex++)
            {
                int hr = Method<Enumerate>(factory, 12)(factory, adapterIndex, out adapter);
                if (hr == NotFound) break;
                Check(hr, "列舉顯示卡");
                for (uint outputIndex = 0; !matched; outputIndex++)
                {
                    hr = Method<Enumerate>(adapter, 7)(adapter, outputIndex, out output);
                    if (hr == NotFound) break;
                    Check(hr, "列舉螢幕");
                    Check(Method<GetOutputDescription>(output, 7)(output, out var description), "讀取螢幕範圍");
                    var bounds = Rectangle.FromLTRB(description.Left, description.Top, description.Right, description.Bottom);
                    if (description.Attached != 0 && bounds.Contains(area))
                    {
                        if (description.Rotation > 1) throw new NotSupportedException("DXGI 錄影暫不支援旋轉螢幕，請使用一般桌面擷取。");
                        // Device must belong to the adapter that owns the selected output.
                        Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out _, out context), "建立錄影 GPU 裝置");
                        Guid outputId = new("00cddea8-939b-4b83-a340-a685226666cc");
                        Check(Method<QueryInterface>(output, 0)(output, ref outputId, out output1), "取得桌面複製介面");
                        Check(Method<DuplicateOutput>(output1, 22)(output1, device, out duplication), "啟動 GPU 桌面擷取");
                        sourceBox = new Box
                        {
                            Left = (uint)(area.Left - bounds.Left), Top = (uint)(area.Top - bounds.Top),
                            Right = (uint)(area.Right - bounds.Left), Bottom = (uint)(area.Bottom - bounds.Top), Back = 1
                        };
                        matched = true;
                    }
                    Release(ref output);
                }
                Release(ref adapter);
            }
            if (!matched) throw new NotSupportedException("DXGI 錄影範圍必須位於同一個螢幕內，請使用一般桌面擷取。");
            var texture = new TextureDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = 87, SampleCount = 1, Usage = 3, CpuAccess = 0x20000
            };
            Check(Method<CreateTexture>(device, 5)(device, ref texture, IntPtr.Zero, out staging), "建立 GPU 讀取緩衝區");
            memoryDc = CreateCompatibleDC(IntPtr.Zero);
            if (memoryDc == IntPtr.Zero) FailGdi("建立游標繪圖裝置");
            var imageInfo = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            dib = CreateDIBSection(memoryDc, ref imageInfo, 0, out pixels, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || pixels == IntPtr.Zero) FailGdi("建立 GPU 錄影像素緩衝區");
            previous = SelectObject(memoryDc, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) FailGdi("選取 GPU 錄影像素緩衝區");
            bitmap = new Bitmap(width, height, stride, PixelFormat.Format32bppRgb, pixels);
        }
        catch { Dispose(); throw; }
        finally { Release(ref output1); Release(ref output); Release(ref adapter); Release(ref factory); }
    }

    // Call once after a non-null Capture; the next GPU readback restores the clean desktop pixels.
    internal void OverlayCursor(Rectangle area)
    {
        if (disposed) throw new ObjectDisposedException(nameof(DxgiDesktopCapture));
        if (ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("GPU 擷取必須在原本的錄影執行緒操作。");
        if (!NeedsCursorOverlay) return;
        Native.DrawCursor(memoryDc, area);
        if (!GdiFlush()) FailGdi("同步錄影游標");
    }

    internal Bitmap? Capture(int timeoutMilliseconds)
    {
        if (disposed) throw new ObjectDisposedException(nameof(DxgiDesktopCapture));
        if (ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("GPU 擷取必須在原本的錄影執行緒操作。");
        if (timeoutMilliseconds < 0 || timeoutMilliseconds > 1000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        IntPtr resource = IntPtr.Zero, texture = IntPtr.Zero;
        bool acquired = false, mapped = false;
        try
        {
            int hr = Method<AcquireFrame>(duplication, 8)(duplication, (uint)timeoutMilliseconds, out var info, out resource);
            if (hr == WaitTimeout) return null;
            if (hr == AccessLost) throw new InvalidOperationException("GPU 桌面擷取已失效（螢幕或顯示模式已改變）。");
            Check(hr, "取得 GPU 桌面影格");
            acquired = true;
            if (info.LastMouseUpdateTime != 0 || !hasDesktopImage) NeedsCursorOverlay = info.PointerVisible != 0;
            // Mouse-only updates still need a clean copy of the last desktop before drawing
            // the new pointer position. Reuse staging rather than leaving cursor trails.
            if (info.LastPresentTime == 0 && (!hasDesktopImage || info.LastMouseUpdateTime == 0)) return null;
            if (info.LastPresentTime != 0)
            {
                Guid textureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                Check(Method<QueryInterface>(resource, 0)(resource, ref textureId, out texture), "取得 GPU 畫面紋理");
                Method<CopyRegion>(context, 46)(context, staging, 0, 0, 0, 0, texture, 0, ref sourceBox);
            }
            Check(Method<MapResource>(context, 14)(context, staging, 0, 1, 0, out var mapping), "讀取 GPU 錄影像素");
            mapped = true;
            if (mapping.RowPitch < stride) throw new InvalidOperationException("GPU 錄影像素列長度無效。");
            Check(MFCopyImage(pixels, stride, mapping.Data, (int)mapping.RowPitch, (uint)stride, (uint)height), "複製 GPU 錄影像素");
            hasDesktopImage = true;
            LastPresentTime = info.LastPresentTime;
            AccumulatedFrames = info.AccumulatedFrames;
            return bitmap;
        }
        finally
        {
            if (mapped) Method<UnmapResource>(context, 15)(context, staging, 0);
            Release(ref texture); Release(ref resource);
            if (acquired) Check(Method<SimpleCall>(duplication, 14)(duplication), "釋放 GPU 桌面影格");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        bitmap?.Dispose(); bitmap = null;
        if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memoryDc, previous);
        if (dib != IntPtr.Zero) DeleteObject(dib);
        if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
        pixels = memoryDc = dib = previous = IntPtr.Zero;
        Release(ref staging); Release(ref duplication); Release(ref context); Release(ref device);
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Release(ref IntPtr value) { if (value != IntPtr.Zero) { Marshal.Release(value); value = IntPtr.Zero; } }
    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new COMException($"{operation}失敗（0x{hr:X8}）：{Marshal.GetExceptionForHR(hr)?.Message}", hr);
    }
    private static void FailGdi(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation + "失敗。");

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom, Attached;
        public uint Rotation;
        public IntPtr Monitor;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality;
        public uint Usage, Bind, CpuAccess, Misc;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapping { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced, ProtectedContentMasked, PointerX, PointerY, PointerVisible;
        public uint MetadataBytes, PointerShapeBytes;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(IntPtr instance, uint index, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterface(IntPtr instance, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescription(IntPtr instance, out OutputDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutput(IntPtr instance, IntPtr device, out IntPtr duplication);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(IntPtr instance, ref TextureDescription description, IntPtr initialData, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireFrame(IntPtr instance, uint timeout, out FrameInfo info, out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SimpleCall(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyRegion(IntPtr instance, IntPtr target, uint targetSubresource, uint x, uint y, uint z, IntPtr source, uint sourceSubresource, ref Box region);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapResource(IntPtr instance, IntPtr resource, uint subresource, uint mode, uint flags, out Mapping mapping);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapResource(IntPtr instance, IntPtr resource, uint subresource);

    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out uint level, out IntPtr context);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCopyImage(IntPtr target, int targetStride, IntPtr source, int sourceStride, uint widthBytes, uint lines);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr source);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool GdiFlush();
}
