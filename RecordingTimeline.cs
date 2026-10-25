using System.Diagnostics;

namespace FreeCamRecord;

internal readonly record struct VideoTimeSlot(long Index, long Timestamp, long Duration, long Skipped);

// Independently implements the clock-aligned slot approach documented by ShareX/OBS.
// A slot index is elapsed media time, not the number of successfully encoded pictures.
internal sealed class RecordingTimeline
{
    internal const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private readonly int fps;
    private readonly Func<long> now;
    private long nextSlot;
    private long stopped = -1;
    internal long Origin { get; }
    internal long Elapsed { get { long end = Interlocked.Read(ref stopped); return end >= 0 ? end : Math.Max(0, now() - Origin); } }
    internal long SkippedSlots { get; private set; }
    internal long NextVideoTime => SlotTime(nextSlot);

    internal RecordingTimeline(int fps, Func<long>? clock = null)
    {
        if (fps is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(fps));
        this.fps = fps; now = clock ?? QpcNow; Origin = now();
    }

    internal static long QpcNow()
    {
        long counter = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency;
        return checked(counter / frequency * TicksPerSecond + counter % frequency * TicksPerSecond / frequency);
    }

    internal static long AudioFramesAt(long ticks) => Scale(Math.Max(0, ticks), 48000, TicksPerSecond);
    internal static long Scale(long value, long multiplier, long divisor) =>
        checked(value / divisor * multiplier + value % divisor * multiplier / divisor);
    private long SlotTime(long index) => Scale(index, TicksPerSecond, fps);

    internal bool TryTakeVideo(out VideoTimeSlot slot)
    {
        long elapsed = Elapsed;
        if (Interlocked.Read(ref stopped) >= 0 || elapsed < NextVideoTime) { slot = default; return false; }
        // The origin is established only after the initial picture is available.
        long due = nextSlot == 0 ? 0 : Math.Max(nextSlot, Scale(elapsed, fps, TicksPerSecond));
        long skipped = due - nextSlot;
        long time = SlotTime(due);
        slot = new(due, time, SlotTime(due + 1) - time, skipped);
        SkippedSlots += skipped; nextSlot = due + 1;
        return true;
    }

    internal void Stop() => Interlocked.CompareExchange(ref stopped, Math.Max(0, now() - Origin), -1);
}

// Audio progresses in 10ms blocks on the same clock, even when no video slot is written.
internal sealed class AudioTimelinePump
{
    private readonly Func<long, int, byte[]> read;
    private readonly Action<byte[], long> write;
    internal long WrittenFrames { get; private set; }
    internal AudioTimelinePump(Func<long, int, byte[]> read, Action<byte[], long> write) { this.read = read; this.write = write; }

    internal void DrainUntil(long time, bool final = false)
    {
        long end = RecordingTimeline.AudioFramesAt(time);
        while (WrittenFrames + 480 <= end || (final && WrittenFrames < end))
        {
            int count = (int)Math.Min(480, end - WrittenFrames);
            byte[] pcm = read(WrittenFrames, count);
            if (pcm.Length != count * 4) throw new InvalidOperationException(UiText.T("音訊時間軸資料長度不符。", "Audio timeline data length does not match."));
            write(pcm, WrittenFrames); WrittenFrames += count;
        }
    }
}
