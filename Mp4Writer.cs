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
    private long audioSamples;
    private IntPtr pendingVideo;
    private long pendingVideoTime, pendingVideoDuration, lastVideoTime = -1;
    private bool videoCompleted;
    private bool started, disposed;
    internal int EncodedWidth { get; }
    internal int EncodedHeight { get; }
    internal long Length => File.Exists(path) ? new FileInfo(path).Length : 0;

    internal Mp4Writer(string path, int width, int height, int fps, bool audio, bool allowHardware = true)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (fps < 1 || fps > 1000) throw new ArgumentOutOfRangeException(nameof(fps));
        if (File.Exists(path)) throw new IOException(UiText.T("錄影檔案已存在，無法覆蓋。", "The recording file already exists and cannot be overwritten."));
        this.path = path; this.width = width; this.height = height; this.fps = fps; this.audio = audio;
        EncodedWidth = checked((width + 1) & ~1); EncodedHeight = checked((height + 1) & ~1);
        canvas = new Bitmap(EncodedWidth, EncodedHeight, PixelFormat.Format32bppArgb);
        try
        {
            Check(MFStartup(0x20070, 0), UiText.T("啟動 Windows MP4 編碼器", "Start Windows MP4 encoder"));
            started = true;
            try { Configure(allowHardware); }
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
            Check(MFCreateAttributes(out attributes, 3), UiText.T("建立 MP4 設定", "Create MP4 settings"));
            SetGuid(attributes, "150ff23f-4abc-478b-ac4f-e1916fba1cca", "dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
            SetUInt(attributes, "a634a91c-822b-41b9-a494-4de4643612b0", hardware ? 1u : 0u);
            SetUInt(attributes, "08b845d8-2b74-4afe-9d53-be16d2d5ae4f", 1);
            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out writer), UiText.T("建立 MP4 檔案", "Create MP4 file"));
            output = CreateType();
            SetGuid(output, MajorType, VideoType);
            SetGuid(output, Subtype, "34363248-0000-0010-8000-00aa00389b71"); // H264
            SetUInt(output, "20332624-fb0d-4d9e-bd0d-cbf6786c102e", (uint)Math.Clamp((long)EncodedWidth * EncodedHeight * fps / 6, 2_000_000, 40_000_000));
            SetVideoGeometry(output);
            Check(Method<AddStream>(writer, 3)(writer, output, out videoStream), UiText.T("設定 H.264 輸出", "Configure H.264 output"));
            input = CreateType();
            SetGuid(input, MajorType, VideoType);
            SetGuid(input, Subtype, "00000016-0000-0010-8000-00aa00389b71"); // RGB32
            SetVideoGeometry(input);
            SetUInt(input, "644b4e48-1e02-4516-b0eb-c01ca9d49ac6", (uint)(EncodedWidth * 4));
            Check(Method<SetInputType>(writer, 4)(writer, videoStream, input, IntPtr.Zero), UiText.T("設定錄影畫面格式", "Configure recording video format"));
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
                Check(Method<AddStream>(writer, 3)(writer, output, out audioStream), UiText.T("設定 AAC 輸出", "Configure AAC output"));
                input = CreateType();
                SetGuid(input, MajorType, AudioType);
                SetGuid(input, Subtype, "00000001-0000-0010-8000-00aa00389b71");
                SetAudioGeometry(input);
                SetUInt(input, "f2deb57f-40fa-4764-aa33-ed4f2d1ff669", 16);
                SetUInt(input, "322de230-9eeb-43bd-ab7a-ff412251541d", 4);
                SetUInt(input, AudioBytes, 192_000);
                Check(Method<SetInputType>(writer, 4)(writer, audioStream, input, IntPtr.Zero), UiText.T("設定錄音格式", "Configure recording audio format"));
            }
            Check(Method<SimpleCall>(writer, 5)(writer), UiText.T("開始 MP4 編碼", "Start MP4 encoding"));
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

    internal void WriteVideo(Bitmap bitmap, long timestamp, long duration, bool discontinuity = false)
    {
        if (disposed) throw new ObjectDisposedException(nameof(Mp4Writer));
        if (videoCompleted) throw new InvalidOperationException(UiText.T("影片時間軸已結束。", "The video timeline has ended."));
        if (timestamp < 0 || timestamp <= lastVideoTime || duration <= 0) throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (bitmap.Width != width || bitmap.Height != height) throw new ArgumentException(UiText.T("錄影尺寸在編碼期間不能改變。", "Recording dimensions cannot change during encoding."), nameof(bitmap));
        // Keep input sample duration short. Extending it across a long gap can make
        // Media Foundation's RGB converter manufacture a fixed-rate catch-up burst.
        FlushVideo(pendingVideoDuration);
        Bitmap source = bitmap;
        if ((bitmap.PixelFormat != PixelFormat.Format32bppArgb && bitmap.PixelFormat != PixelFormat.Format32bppRgb) || bitmap.Width != EncodedWidth || bitmap.Height != EncodedHeight)
        {
            using var graphics = Graphics.FromImage(canvas);
            graphics.Clear(Color.Black);
            graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height), 0, 0, width, height, GraphicsUnit.Pixel);
            source = canvas;
        }
        var locked = source.LockBits(new Rectangle(0, 0, EncodedWidth, EncodedHeight), ImageLockMode.ReadOnly, source.PixelFormat);
        try
        {
            int rowBytes = EncodedWidth * 4;
            // An explicit positive MF_MT_DEFAULT_STRIDE keeps the desktop's top-down row order.
            // Copy directly into the native sample, avoiding an extra full-frame managed copy.
            pendingVideo = CreateSample(checked(rowBytes * EncodedHeight), target =>
                Check(MFCopyImage(target, rowBytes, locked.Scan0, locked.Stride, (uint)rowBytes, (uint)EncodedHeight), UiText.T("複製錄影畫面", "Copy recording frame")),
                timestamp, duration);
            if (discontinuity) SetUInt(pendingVideo, "9cdf01d9-a0f0-43ba-b077-eaa06cbd728a", 1);
            pendingVideoTime = lastVideoTime = timestamp; pendingVideoDuration = duration;
        }
        finally { source.UnlockBits(locked); }
    }

    internal void WriteAudio(byte[] pcm, long firstFrame)
    {
        if (disposed) throw new ObjectDisposedException(nameof(Mp4Writer));
        if (!audio) throw new InvalidOperationException(UiText.T("此錄影未啟用音訊。", "Audio is not enabled for this recording."));
        if (firstFrame < audioSamples || pcm.Length % 4 != 0) throw new ArgumentException(UiText.T("音訊時間戳記或 PCM16 雙聲道資料無效。", "The audio timestamp or PCM16 stereo data is invalid."), nameof(pcm));
        if (pcm.Length > 0)
        {
            long time = RecordingTimeline.Scale(firstFrame, TimeSpan.TicksPerSecond, 48000);
            long end = checked(firstFrame + pcm.Length / 4);
            IntPtr sample = CreateSample(pcm.Length, target => Marshal.Copy(pcm, 0, target, pcm.Length), time, RecordingTimeline.Scale(end, TimeSpan.TicksPerSecond, 48000) - time);
            try { Check(Method<WriteSampleCall>(writer, 6)(writer, audioStream, sample), UiText.T("寫入 MP4 音訊", "Write MP4 audio")); }
            finally { Release(ref sample); }
            audioSamples = end;
        }
    }

    internal void CompleteVideoAt(long end)
    {
        if (disposed || videoCompleted) return;
        long remaining = Math.Max(1, end - pendingVideoTime);
        if (pendingVideo != IntPtr.Zero && remaining > pendingVideoDuration * 2)
        {
            // One terminal copy, not one copy per missing slot, preserves the stop
            // time even for a silent recording containing only a single picture.
            long tailTime = end - pendingVideoDuration;
            IntPtr tail = CloneVideoSample(pendingVideo, tailTime, pendingVideoDuration);
            try
            {
                FlushVideo(pendingVideoDuration);
                Check(Method<WriteSampleCall>(writer, 6)(writer, videoStream, tail), UiText.T("寫入錄影結束畫面", "Write recording end frame"));
            }
            finally { Release(ref tail); }
        }
        else FlushVideo(remaining);
        videoCompleted = true;
    }

    private IntPtr CloneVideoSample(IntPtr source, long time, long duration)
    {
        IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            Check(Method<GetBufferCall>(source, 40)(source, 0, out buffer), UiText.T("讀取最後畫面緩衝區", "Read final frame buffer"));
            Check(MFCreateSample(out sample), UiText.T("建立錄影結束畫面", "Create recording end frame"));
            Check(Method<PointerCall>(sample, 42)(sample, buffer), UiText.T("保留最後畫面", "Retain final frame"));
            Check(Method<LongCall>(sample, 36)(sample, time), UiText.T("設定錄影結束時間", "Set recording end time"));
            Check(Method<LongCall>(sample, 38)(sample, duration), UiText.T("設定結束畫面時長", "Set end frame duration"));
            SetUInt(sample, "9cdf01d9-a0f0-43ba-b077-eaa06cbd728a", 1);
            IntPtr result = sample; sample = IntPtr.Zero; return result;
        }
        finally { Release(ref buffer); Release(ref sample); }
    }

    private void FlushVideo(long duration)
    {
        if (pendingVideo == IntPtr.Zero) return;
        try
        {
            Check(Method<LongCall>(pendingVideo, 38)(pendingVideo, Math.Max(1, duration)), UiText.T("設定畫面持續時間", "Set frame duration"));
            Check(Method<WriteSampleCall>(writer, 6)(writer, videoStream, pendingVideo), UiText.T("寫入 MP4 影格", "Write MP4 frame"));
        }
        finally { Release(ref pendingVideo); }
    }

    private IntPtr CreateSample(int length, Action<IntPtr> copy, long time, long duration)
    {
        IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Check(MFCreateMemoryBuffer((uint)length, out buffer), UiText.T("配置編碼緩衝區", "Allocate encoding buffer"));
            Check(Method<BufferLock>(buffer, 3)(buffer, out var target, out _, out _), UiText.T("鎖定編碼緩衝區", "Lock encoding buffer"));
            try { copy(target); }
            finally { Check(Method<SimpleCall>(buffer, 4)(buffer), UiText.T("解鎖編碼緩衝區", "Unlock encoding buffer")); }
            Check(Method<UIntCall>(buffer, 6)(buffer, (uint)length), UiText.T("設定編碼資料長度", "Set encoded data length"));
            Check(MFCreateSample(out sample), UiText.T("建立編碼影格", "Create encoded frame"));
            Check(Method<PointerCall>(sample, 42)(sample, buffer), UiText.T("加入編碼影格", "Add encoded frame"));
            Check(Method<LongCall>(sample, 36)(sample, time), UiText.T("設定影格時間", "Set frame time"));
            Check(Method<LongCall>(sample, 38)(sample, duration), UiText.T("設定影格時長", "Set frame duration"));
            IntPtr result = sample; sample = IntPtr.Zero;
            return result;
        }
        finally { Release(ref sample); Release(ref buffer); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            FlushVideo(pendingVideoDuration);
            if (writer != IntPtr.Zero) Check(Method<SimpleCall>(writer, 11)(writer), UiText.T("完成 MP4 儲存", "Finish saving MP4"));
        }
        finally
        {
            Release(ref writer);
            Release(ref pendingVideo);
            canvas.Dispose();
            if (started) { MFShutdown(); started = false; }
        }
    }

    private const string MajorType = "48eba18e-f8c9-4687-bf11-0a74c9f96a8f";
    private const string Subtype = "f7e34c9a-42e8-4714-b74b-cb29d72c35e5";
    private const string VideoType = "73646976-0000-0010-8000-00aa00389b71";
    private const string AudioType = "73647561-0000-0010-8000-00aa00389b71";
    private const string AudioBytes = "1aab75c8-cfef-451c-ab95-ac034b8e1731";

    private static IntPtr CreateType() { Check(MFCreateMediaType(out var type), UiText.T("建立媒體格式", "Create media format")); return type; }
    private static void SetUInt(IntPtr attributes, string key, uint value)
    {
        Guid id = new(key); Check(Method<SetUIntCall>(attributes, 21)(attributes, ref id, value), UiText.T("設定媒體屬性", "Set media attribute"));
    }
    private static void SetGuid(IntPtr attributes, string key, string value)
    {
        Guid id = new(key), data = new(value); Check(Method<SetGuidCall>(attributes, 24)(attributes, ref id, ref data), UiText.T("設定媒體格式屬性", "Set media format attribute"));
    }
    private static void SetPair(IntPtr attributes, string key, uint high, uint low)
    {
        Guid id = new(key); Check(Method<SetULongCall>(attributes, 22)(attributes, ref id, ((ulong)high << 32) | low), UiText.T("設定媒體尺寸或幀率", "Set media dimensions or frame rate"));
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Release(ref IntPtr value) { if (value != IntPtr.Zero) { Marshal.Release(value); value = IntPtr.Zero; } }
    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new COMException(UiText.T($"{operation}失敗（0x{hr:X8}）：{Marshal.GetExceptionForHR(hr)?.Message}", $"{operation} failed (0x{hr:X8}): {Marshal.GetExceptionForHR(hr)?.Message}"), hr);
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
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBufferCall(IntPtr instance, uint index, out IntPtr buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(uint size, out IntPtr buffer);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCopyImage(IntPtr destination, int destinationStride, IntPtr source, int sourceStride, uint widthBytes, uint lines);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr writer);
}
