using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FreeCamRecord;

internal record RecordingResult(string Path, string? Error, long CapturedFrames = 0, double Seconds = 0, string CaptureBackend = "GDI");

internal sealed class ScreenRecorder
{
    private readonly Rectangle area;
    private readonly string finalPath, temporaryPath, format;
    private readonly int fps;
    private readonly bool systemAudio, microphone;
    private readonly Stopwatch watch = new();
    private volatile bool stopping;
    private string? stopReason;
    private AudioCapture? sound;
    private AviWriter? avi;
    private Mp4Writer? mp4;
    private Bitmap? bitmap;
    private DesktopCapture? desktop;
    private DxgiDesktopCapture? gpu;
    private string captureBackend = "GDI";
    private readonly ImageCodecInfo jpegEncoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
    private readonly EncoderParameters jpegParameters = new(1);
    private byte[]? currentJpeg;
    private bool preciseTimer;
    private long captures;
    private double captureMilliseconds;
    private long frameCount;
    public Task<RecordingResult> Completion { get; private set; } = null!;
    public TimeSpan Elapsed => watch.Elapsed;

    internal ScreenRecorder(Rectangle area, string directory, bool systemAudio, bool microphone, int fps = 30, string format = "mp4")
    {
        if (fps is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(fps));
        if (format is not ("mp4" or "avi")) throw new ArgumentOutOfRangeException(nameof(format));
        this.area = area; this.systemAudio = systemAudio; this.microphone = microphone;
        this.fps = fps; this.format = format;
        finalPath = Path.Combine(directory, $"錄影_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}.{format}");
        temporaryPath = finalPath + ".partial";
        jpegParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
    }

    internal Task StartAsync()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (format == "mp4") mp4 = new Mp4Writer(temporaryPath, area.Width, area.Height, fps, systemAudio || microphone);
                else avi = new AviWriter(temporaryPath, area.Width, area.Height, fps, systemAudio || microphone);
                if (Environment.GetEnvironmentVariable("FREECAM_CAPTURE_BACKEND") != "gdi")
                {
                    try { gpu = new DxgiDesktopCapture(area); captureBackend = "DXGI"; }
                    catch (Exception ex) when (ex is COMException or NotSupportedException or System.ComponentModel.Win32Exception) { gpu = null; }
                }
                if (gpu == null) desktop = new DesktopCapture(area);
                if (systemAudio || microphone) { sound = new AudioCapture(systemAudio, microphone); sound.Start(); }
                preciseTimer = Native.timeBeginPeriod(1) == 0;
                for (int attempt = 0; bitmap == null && attempt < 10; attempt++) Capture();
                if (bitmap == null) { gpu?.Dispose(); gpu = null; desktop = new DesktopCapture(area); captureBackend = "GDI fallback"; Capture(); }
                watch.Start(); WriteFrame();
            }
            catch (Exception ex)
            {
                try { ReleaseResources(); } catch { /* Keep the original startup error. */ }
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { /* Preserve the startup error. */ }
                ready.TrySetException(ex);
                return new RecordingResult(temporaryPath, ex.Message);
            }
            ready.TrySetResult(true);
            return RecordLoop();
        });
        return ready.Task;
    }

    private void Capture()
    {
        long start = Stopwatch.GetTimestamp();
        Bitmap? image;
        if (gpu != null)
        {
            try
            {
                image = gpu.Capture(bitmap == null ? 50 : Math.Clamp(1000 / fps, 1, 16));
                if (image != null) gpu.OverlayCursor(area);
            }
            catch (COMException)
            {
                gpu.Dispose(); gpu = null; captureBackend = "GDI fallback";
                desktop = new DesktopCapture(area); image = desktop.Capture();
            }
        }
        else image = desktop!.Capture();
        double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        captureMilliseconds = captures == 0 ? elapsed : captureMilliseconds * 0.7 + elapsed * 0.3;
        if (image == null) return;
        bitmap = image;
        currentJpeg = null;
        captures++;
    }

    private void WriteFrame()
    {
        // Rational accumulation also keeps audio aligned at 144/165 Hz and other non-divisors of 48 kHz.
        int audioFrames = (int)((frameCount + 1) * 48000 / fps - frameCount * 48000 / fps);
        byte[]? pcm = sound?.ReadFrames(audioFrames);
        if (mp4 != null) mp4.WriteFrame(bitmap!, pcm);
        else
        {
            if (currentJpeg == null)
            {
                using var buffer = new MemoryStream();
                bitmap!.Save(buffer, jpegEncoder, jpegParameters);
                currentJpeg = buffer.ToArray();
            }
            avi!.WriteFrame(currentJpeg, pcm);
        }
        frameCount++;
    }

    private RecordingResult RecordLoop()
    {
        string? error = null;
        try
        {
            while (!stopping)
            {
                if (sound?.Error is string audioError) throw new InvalidOperationException(audioError);
                double due = frameCount * 1000.0 / fps;
                // Screen readback may block until the next display refresh. Start that work
                // before the frame deadline instead of sleeping and then waiting another refresh.
                double lead = Math.Min(1000.0 / fps, captureMilliseconds);
                int delay = (int)(due - lead - watch.Elapsed.TotalMilliseconds);
                if (delay > 0) { Thread.Sleep(Math.Min(delay, 10)); continue; }
                Capture();
                while (!stopping && watch.Elapsed.TotalMilliseconds < due)
                    Thread.Sleep(Math.Clamp((int)(due - watch.Elapsed.TotalMilliseconds), 1, 5));
                if (stopping) break;
                // Repeat a late frame so playback duration remains aligned with elapsed time.
                // Limit catch-up work so a slow encoder cannot trap the recording loop.
                int repeats = 0;
                do { WriteFrame(); repeats++; }
                while (frameCount <= watch.Elapsed.TotalSeconds * fps && !stopping && repeats < fps);
                if (avi?.Length > 1_800_000_000)
                {
                    error = "已達第一版單檔 1.8 GB 上限，已自動停止並儲存。";
                    break;
                }
            }
        }
        catch (Exception ex) { error = $"錄影中斷：{ex.Message}"; }
        finally
        {
            watch.Stop();
            try { ReleaseResources(); }
            catch (Exception ex) { error = $"儲存失敗，暫存檔保留：{ex.Message}"; }
        }
        if (error?.StartsWith("儲存失敗") == true) return new RecordingResult(temporaryPath, error, captures, watch.Elapsed.TotalSeconds, captureBackend);
        try
        {
            File.Move(temporaryPath, finalPath);
            return new RecordingResult(finalPath, error ?? stopReason, captures, watch.Elapsed.TotalSeconds, captureBackend);
        }
        catch (Exception ex) { return new RecordingResult(temporaryPath, $"儲存失敗：{ex.Message}；暫存檔保留於 {temporaryPath}", captures, watch.Elapsed.TotalSeconds, captureBackend); }
    }

    private void ReleaseResources()
    {
        try { sound?.Dispose(); }
        finally
        {
            try { avi?.Dispose(); mp4?.Dispose(); }
            finally
            {
                gpu?.Dispose(); desktop?.Dispose(); jpegParameters.Dispose();
                if (preciseTimer) { Native.timeEndPeriod(1); preciseTimer = false; }
            }
        }
    }

    internal Task<RecordingResult> StopAsync(string? reason = null) { stopReason ??= reason; stopping = true; return Completion; }
}
