using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FreeCamRecord;

// Windows Media Foundation only: H.264 video and optional AAC audio in an MP4 container.
// GUIDs and COM slot order follow the Windows SDK mfapi.h, mfobjects.h and mfreadwrite.h.
internal sealed class Mp4Writer : IDisposable
{
    private readonly string path;
    private readonly int width, height, fps;
    private readonly bool audio;
    private readonly Bitmap canvas;
    private IntPtr writer;
    private uint videoStream, audioStream;
    private long frameCount, audioSamples;
    private bool started, disposed;
    internal int EncodedWidth { get; }
    internal int EncodedHeight { get; }
    internal long Length => File.Exists(path) ? new FileInfo(path).Length : 0;

    internal Mp4Writer(string path, int width, int height, int fps, bool audio)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (fps < 1 || fps > 1000) throw new ArgumentOutOfRangeException(nameof(fps));
        if (File.Exists(path)) throw new IOException("錄影檔案已存在，無法覆蓋。");
        this.path = path; this.width = width; this.height = height; this.fps = fps; this.audio = audio;
        EncodedWidth = checked((width + 1) & ~1); EncodedHeight = checked((height + 1) & ~1);
        canvas = new Bitmap(EncodedWidth, EncodedHeight, PixelFormat.Format32bppArgb);
        try
        {
            Check(MFStartup(0x20070, 0), "啟動 Windows MP4 編碼器");
            started = true;
            try { Configure(true); }
            catch (COMException)
            {
                Release(ref writer);
                if (File.Exists(path)) File.Delete(path);
                Configure(false);
            }
        }
        catch
        {
            Release(ref writer);
            canvas.Dispose();
            if (started) { MFShutdown(); started = false; }
            throw;
        }
    }

    private void Configure(bool hardware)
    {
        IntPtr attributes = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            Check(MFCreateAttributes(out attributes, 3), "建立 MP4 設定");
            SetGuid(attributes, "150ff23f-4abc-478b-ac4f-e1916fba1cca", "dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
            SetUInt(attributes, "a634a91c-822b-41b9-a494-4de4643612b0", hardware ? 1u : 0u);
            SetUInt(attributes, "08b845d8-2b74-4afe-9d53-be16d2d5ae4f", 1);
            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out writer), "建立 MP4 檔案");
            output = CreateType();
            SetGuid(output, MajorType, VideoType);
            SetGuid(output, Subtype, "34363248-0000-0010-8000-00aa00389b71"); // H264
            SetUInt(output, "20332624-fb0d-4d9e-bd0d-cbf6786c102e", (uint)Math.Clamp((long)EncodedWidth * EncodedHeight * fps / 6, 2_000_000, 40_000_000));
            SetVideoGeometry(output);
            Check(Method<AddStream>(writer, 3)(writer, output, out videoStream), "設定 H.264 輸出");
            input = CreateType();
            SetGuid(input, MajorType, VideoType);
            SetGuid(input, Subtype, "00000016-0000-0010-8000-00aa00389b71"); // RGB32
            SetVideoGeometry(input);
            SetUInt(input, "644b4e48-1e02-4516-b0eb-c01ca9d49ac6", (uint)(EncodedWidth * 4));
            Check(Method<SetInputType>(writer, 4)(writer, videoStream, input, IntPtr.Zero), "設定錄影畫面格式");
            Release(ref output); Release(ref input);
            if (audio)
            {
                output = CreateType();
                SetGuid(output, MajorType, AudioType);
                SetGuid(output, Subtype, "00001610-0000-0010-8000-00aa00389b71"); // AAC
                SetAudioGeometry(output);
                SetUInt(output, AudioBytes, 20_000); // 160 kbit/s stereo AAC
                SetUInt(output, "bfbabe79-7434-4d1c-94f0-72a3b9e17188", 0);
                SetUInt(output, "7632f0e6-9538-4d61-acda-ea29c8c14456", 0x29);
                Check(Method<AddStream>(writer, 3)(writer, output, out audioStream), "設定 AAC 輸出");
                input = CreateType();
                SetGuid(input, MajorType, AudioType);
                SetGuid(input, Subtype, "00000001-0000-0010-8000-00aa00389b71");
                SetAudioGeometry(input);
                SetUInt(input, "f2deb57f-40fa-4764-aa33-ed4f2d1ff669", 16);
                SetUInt(input, "322de230-9eeb-43bd-ab7a-ff412251541d", 4);
                SetUInt(input, AudioBytes, 192_000);
                Check(Method<SetInputType>(writer, 4)(writer, audioStream, input, IntPtr.Zero), "設定錄音格式");
            }
            Check(Method<SimpleCall>(writer, 5)(writer), "開始 MP4 編碼");
        }
        finally { Release(ref input); Release(ref output); Release(ref attributes); }
    }

    private void SetVideoGeometry(IntPtr type)
    {
        SetUInt(type, "e2724bb8-e676-4806-b4b2-a8d6efb44ccd", 2); // progressive
        SetPair(type, "1652c33d-d6b2-4012-b834-72030849a37d", (uint)EncodedWidth, (uint)EncodedHeight);
        SetPair(type, "c459a2e8-3d2c-4e44-b132-fee5156c7bb0", (uint)fps, 1);
        SetPair(type, "c6376a1e-8d0a-4027-be45-6d9a0ad39bb6", 1, 1);
    }

    private static void SetAudioGeometry(IntPtr type)
    {
        SetUInt(type, "37e48bf5-645e-4c5b-89de-ada9e29b696a", 2);
        SetUInt(type, "5faeeae7-0290-4c31-9e8a-c534f68d9dba", 48000);
    }

    internal void WriteFrame(Bitmap bitmap, byte[]? pcm)
    {
        if (disposed) throw new ObjectDisposedException(nameof(Mp4Writer));
        if (bitmap.Width != width || bitmap.Height != height) throw new ArgumentException("錄影尺寸在編碼期間不能改變。", nameof(bitmap));
        if (audio && (pcm == null || pcm.Length % 4 != 0)) throw new ArgumentException("音訊必須為 48kHz 雙聲道 PCM16。", nameof(pcm));
        Bitmap source = bitmap;
        if ((bitmap.PixelFormat != PixelFormat.Format32bppArgb && bitmap.PixelFormat != PixelFormat.Format32bppRgb) || bitmap.Width != EncodedWidth || bitmap.Height != EncodedHeight)
        {
            using var graphics = Graphics.FromImage(canvas);
            graphics.Clear(Color.Black);
            graphics.DrawImageUnscaled(bitmap, 0, 0);
            source = canvas;
        }
        var locked = source.LockBits(new Rectangle(0, 0, EncodedWidth, EncodedHeight), ImageLockMode.ReadOnly, source.PixelFormat);
        long videoTime = frameCount * 10_000_000 / fps;
        try
        {
            int rowBytes = EncodedWidth * 4;
            // An explicit positive MF_MT_DEFAULT_STRIDE keeps the desktop's top-down row order.
            // Copy directly into the native sample, avoiding an extra full-frame managed copy.
            WriteSample(videoStream, checked(rowBytes * EncodedHeight), target =>
                Check(MFCopyImage(target, rowBytes, locked.Scan0, locked.Stride, (uint)rowBytes, (uint)EncodedHeight), "複製錄影畫面"),
                videoTime, (frameCount + 1) * 10_000_000 / fps - videoTime);
        }
        finally { source.UnlockBits(locked); }
        frameCount++;
        if (audio && pcm!.Length > 0)
        {
            long time = audioSamples * 10_000_000 / 48000;
            audioSamples += pcm.Length / 4;
            WriteSample(audioStream, pcm.Length, target => Marshal.Copy(pcm, 0, target, pcm.Length), time, audioSamples * 10_000_000 / 48000 - time);
        }
    }

    private void WriteSample(uint stream, int length, Action<IntPtr> copy, long time, long duration)
    {
        IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Check(MFCreateMemoryBuffer((uint)length, out buffer), "配置編碼緩衝區");
            Check(Method<BufferLock>(buffer, 3)(buffer, out var target, out _, out _), "鎖定編碼緩衝區");
            try { copy(target); }
            finally { Check(Method<SimpleCall>(buffer, 4)(buffer), "解鎖編碼緩衝區"); }
            Check(Method<UIntCall>(buffer, 6)(buffer, (uint)length), "設定編碼資料長度");
            Check(MFCreateSample(out sample), "建立編碼影格");
            Check(Method<PointerCall>(sample, 42)(sample, buffer), "加入編碼影格");
            Check(Method<LongCall>(sample, 36)(sample, time), "設定影格時間");
            Check(Method<LongCall>(sample, 38)(sample, duration), "設定影格時長");
            Check(Method<WriteSampleCall>(writer, 6)(writer, stream, sample), "寫入 MP4 影格");
        }
        finally { Release(ref sample); Release(ref buffer); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (writer != IntPtr.Zero) Check(Method<SimpleCall>(writer, 11)(writer), "完成 MP4 儲存");
        }
        finally
        {
            Release(ref writer);
            canvas.Dispose();
            if (started) { MFShutdown(); started = false; }
        }
    }

    private const string MajorType = "48eba18e-f8c9-4687-bf11-0a74c9f96a8f";
    private const string Subtype = "f7e34c9a-42e8-4714-b74b-cb29d72c35e5";
    private const string VideoType = "73646976-0000-0010-8000-00aa00389b71";
    private const string AudioType = "73647561-0000-0010-8000-00aa00389b71";
    private const string AudioBytes = "1aab75c8-cfef-451c-ab95-ac034b8e1731";

    private static IntPtr CreateType() { Check(MFCreateMediaType(out var type), "建立媒體格式"); return type; }
    private static void SetUInt(IntPtr attributes, string key, uint value)
    {
        Guid id = new(key); Check(Method<SetUIntCall>(attributes, 21)(attributes, ref id, value), "設定媒體屬性");
    }
    private static void SetGuid(IntPtr attributes, string key, string value)
    {
        Guid id = new(key), data = new(value); Check(Method<SetGuidCall>(attributes, 24)(attributes, ref id, ref data), "設定媒體格式屬性");
    }
    private static void SetPair(IntPtr attributes, string key, uint high, uint low)
    {
        Guid id = new(key); Check(Method<SetULongCall>(attributes, 22)(attributes, ref id, ((ulong)high << 32) | low), "設定媒體尺寸或幀率");
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Release(ref IntPtr value) { if (value != IntPtr.Zero) { Marshal.Release(value); value = IntPtr.Zero; } }
    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new COMException($"{operation}失敗（0x{hr:X8}）：{Marshal.GetExceptionForHR(hr)?.Message}", hr);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SimpleCall(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UIntCall(IntPtr instance, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LongCall(IntPtr instance, long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PointerCall(IntPtr instance, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUIntCall(IntPtr instance, ref Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetULongCall(IntPtr instance, ref Guid key, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuidCall(IntPtr instance, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddStream(IntPtr instance, IntPtr type, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInputType(IntPtr instance, uint index, IntPtr type, IntPtr attributes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WriteSampleCall(IntPtr instance, uint index, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BufferLock(IntPtr instance, out IntPtr pointer, out uint maximum, out uint current);

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(uint size, out IntPtr buffer);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCopyImage(IntPtr destination, int destinationStride, IntPtr source, int sourceStride, uint widthBytes, uint lines);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr writer);
}
