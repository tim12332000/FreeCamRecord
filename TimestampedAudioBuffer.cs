namespace FreeCamRecord;

// Samples retain their absolute QPC-derived 48 kHz positions, even after stalls or overflow.
internal sealed class TimestampedAudioBuffer
{
    internal const int SampleRate = 48000;
    private readonly object gate = new();
    private readonly int capacity;
    private readonly float[] samples;
    private readonly long[] positions;
    private long newest = -1;

    internal TimestampedAudioBuffer(int capacityFrames = SampleRate * 2)
    {
        if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        capacity = capacityFrames;
        samples = new float[checked(capacityFrames * 2)];
        positions = new long[capacityFrames];
        Array.Fill(positions, -1L);
    }

    internal static long Qpc100nsToFrame(long ticks)
    {
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        return checked(ticks / 10_000_000 * SampleRate + ticks % 10_000_000 * SampleRate / 10_000_000);
    }

    internal void WriteAt(long firstFrame, ReadOnlySpan<float> stereo)
    {
        if (firstFrame < 0 || (stereo.Length & 1) != 0) throw new ArgumentOutOfRangeException(nameof(firstFrame));
        int frames = stereo.Length / 2;
        if (frames == 0) return;
        long lastFrame = checked(firstFrame + frames - 1);
        lock (gate)
        {
            newest = Math.Max(newest, lastFrame);
            long oldest = Math.Max(0, newest - capacity + 1);
            // A delayed packet may fill retained gaps, but may not overwrite newer ring slots.
            int start = (int)Math.Min(frames, Math.Max(0, oldest - firstFrame));
            for (int frame = start; frame < frames; frame++)
            {
                long absolute = firstFrame + frame;
                int slot = (int)(absolute % capacity);
                positions[slot] = absolute;
                samples[slot * 2] = stereo[frame * 2];
                samples[slot * 2 + 1] = stereo[frame * 2 + 1];
            }
        }
    }

    internal void AddAt(long firstFrame, Span<float> destination)
    {
        if (firstFrame < 0 || (destination.Length & 1) != 0) throw new ArgumentOutOfRangeException(nameof(firstFrame));
        _ = checked(firstFrame + destination.Length / 2);
        lock (gate)
        {
            long oldest = Math.Max(0, newest - capacity + 1);
            for (int frame = 0; frame < destination.Length / 2; frame++)
            {
                long absolute = firstFrame + frame;
                if (absolute < oldest || absolute > newest) continue;
                int slot = (int)(absolute % capacity);
                if (positions[slot] != absolute) continue;
                destination[frame * 2] += samples[slot * 2];
                destination[frame * 2 + 1] += samples[slot * 2 + 1];
            }
        }
    }
}

// Linear resampling is placed onto the physical clock, rather than onto a dequeue counter.
internal sealed class TimestampedAudioResampler
{
    private readonly TimestampedAudioBuffer destination;
    private float[] output = Array.Empty<float>();
    private bool previousAvailable, expectedAvailable;
    private double previousPosition, expectedQpc100ns;
    private float previousLeft, previousRight;
    private long nextOutput, fallbackPackets;
    internal long TimestampFallbackPackets => Interlocked.Read(ref fallbackPackets);

    internal TimestampedAudioResampler(TimestampedAudioBuffer destination) => this.destination = destination;

    internal void ProcessPacket(ReadOnlySpan<float> stereo, int sourceRate, long packetQpc100ns, uint flags, long arrivalQpc100ns)
    {
        if ((stereo.Length & 1) != 0 || sourceRate < 8000 || sourceRate > 384000)
            throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (arrivalQpc100ns < 0) throw new ArgumentOutOfRangeException(nameof(arrivalQpc100ns));
        int frames = stereo.Length / 2;
        if (frames == 0) return;
        bool discontinuity = (flags & 1) != 0;
        double packetTime;
        if ((flags & 4) == 0 && packetQpc100ns > 0 && packetQpc100ns - arrivalQpc100ns <= 10_000_000)
        {
            packetTime = packetQpc100ns;
        }
        else
        {
            // A broken device timestamp cannot be recovered exactly. Preserve a known continuous
            // packet clock; after a glitch/startup, estimate the first sample from packet arrival.
            Interlocked.Increment(ref fallbackPackets);
            packetTime = expectedAvailable && !discontinuity
                ? expectedQpc100ns
                : Math.Max(0, arrivalQpc100ns - frames * 10_000_000.0 / sourceRate);
        }
        expectedQpc100ns = packetTime + frames * 10_000_000.0 / sourceRate;
        expectedAvailable = true;
        double start = packetTime * (TimestampedAudioBuffer.SampleRate / 10_000_000.0);
        double step = (double)TimestampedAudioBuffer.SampleRate / sourceRate;
        if (!previousAvailable || discontinuity || Math.Abs(start - previousPosition - step) > 2.0)
        {
            previousAvailable = false;
            nextOutput = (long)Math.Ceiling(start - 0.000001);
        }
        int required = checked(((int)Math.Ceiling(frames * step) + 4) * 2);
        if (output.Length < required) output = new float[required];
        int count = 0;
        long firstOutput = nextOutput;
        for (int frame = 0; frame < frames; frame++)
        {
            double position = start + frame * step;
            float left = stereo[frame * 2], right = stereo[frame * 2 + 1];
            if (!previousAvailable)
            {
                previousPosition = position; previousLeft = left; previousRight = right;
                previousAvailable = true;
            }
            if (position < previousPosition) continue;
            while (nextOutput <= position + 0.000001)
            {
                double fraction = position > previousPosition ? (nextOutput - previousPosition) / (position - previousPosition) : 1;
                float weight = (float)Math.Clamp(fraction, 0, 1);
                output[count++] = previousLeft + (left - previousLeft) * weight;
                output[count++] = previousRight + (right - previousRight) * weight;
                nextOutput++;
            }
            previousPosition = position; previousLeft = left; previousRight = right;
        }
        if (count != 0) destination.WriteAt(firstOutput, output.AsSpan(0, count));
    }
}
