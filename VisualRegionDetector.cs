using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FreeCamRecord;

// Visual rectangle proposals, not semantic recognition. Never consults window handles or browser DOM.
internal sealed class VisualRegionDetector : IDisposable
{
    private readonly int width, height, scale;
    private byte[] pixels;
    private (int X, int Y, Size Minimum) lastKey;
    private IReadOnlyList<Rectangle>? lastResult;

    internal VisualRegionDetector(Bitmap screenshot)
    {
        width = screenshot.Width; height = screenshot.Height;
        scale = Math.Max(1, (int)Math.Ceiling(width / 800.0));
        using var copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(copy))
            graphics.DrawImage(screenshot, new Rectangle(0, 0, width, height), 0, 0, width, height, GraphicsUnit.Pixel);
        var bits = copy.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            pixels = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), pixels, y * width * 4, width * 4);
        }
        finally { copy.UnlockBits(bits); }
    }

    internal IReadOnlyList<Rectangle> FindCandidates(Point imagePoint, Size minimumSize)
    {
        if (pixels.Length == 0) throw new ObjectDisposedException(nameof(VisualRegionDetector));
        if (!new Rectangle(0, 0, width, height).Contains(imagePoint)) return Array.Empty<Rectangle>();
        var key = (imagePoint.X / 12, imagePoint.Y / 12, minimumSize);
        if (lastResult is { Count: > 0 } && key == lastKey && lastResult.All(r => r.Contains(imagePoint))) return lastResult;
        lastKey = key;
        int span = Math.Max(minimumSize.Height * 2, height / 5);
        int from = Math.Max(0, imagePoint.Y - span / 2), to = Math.Min(height - 1, imagePoint.Y + span / 2);
        var vertical = Edges(true, from, to, 0, width);
        var left = vertical.Where(e => e.Position <= imagePoint.X).OrderByDescending(e => e.Score).Take(7).ToList();
        var right = vertical.Where(e => e.Position > imagePoint.X).OrderByDescending(e => e.Score).Take(7).ToList();
        left.Add(new Edge(0, .12)); right.Add(new Edge(width, .12));
        var candidates = new List<(Rectangle Rectangle, double Score)>();
        foreach (var l in left)
        foreach (var r in right)
        {
            if (r.Position - l.Position < minimumSize.Width) continue;
            var wall = WallRange(l.Position, r.Position, imagePoint.Y);
            if (wall.Height >= minimumSize.Height)
            {
                var rectangle = new Rectangle(l.Position, wall.Top, r.Position - l.Position, wall.Height);
                double bounded = (l.Position > 0 ? .35 : 0) + (r.Position < width ? .35 : 0);
                double area = (double)rectangle.Width * rectangle.Height / (width * height);
                Add(rectangle, l.Score + r.Score + bounded + area * .8 + .6 + OutsideUniformity(rectangle) * .9);
            }
            // Horizontal transitions are the fallback when vertical walls are interrupted.
            var horizontal = Edges(false, l.Position + 4, r.Position - 4, 0, height);
            var tops = horizontal.Where(e => e.Position <= imagePoint.Y).OrderByDescending(e => e.Score).Take(3).ToList();
            var bottoms = horizontal.Where(e => e.Position > imagePoint.Y).OrderByDescending(e => e.Score).Take(3).ToList();
            tops.Add(new Edge(0, .1)); bottoms.Add(new Edge(height, .1));
            foreach (var top in tops)
            foreach (var bottom in bottoms)
            {
                var rectangle = Rectangle.FromLTRB(l.Position, top.Position, r.Position, bottom.Position);
                if (rectangle.Height < minimumSize.Height) continue;
                double area = (double)rectangle.Width * rectangle.Height / (width * height);
                Add(rectangle, l.Score + r.Score + (top.Score + bottom.Score) * .65 + area * .35 + OutsideUniformity(rectangle) * .9);
            }
        }
        var result = new List<Rectangle>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Score))
        {
            if (candidate.Score < 1.45) break;
            if (result.Any(r => Overlap(r, candidate.Rectangle) > .93)) continue;
            result.Add(candidate.Rectangle);
            if (result.Count == 4) break;
        }
        return lastResult = result;

        void Add(Rectangle rectangle, double score)
        {
            if (!rectangle.Contains(imagePoint) || rectangle.Width < minimumSize.Width || rectangle.Height < minimumSize.Height) return;
            if (rectangle.Width == width && rectangle.Height == height) return; // No false "detected" full desktop.
            candidates.Add((rectangle, score));
        }
    }

    private readonly record struct Edge(int Position, double Score);

    private List<Edge> Edges(bool vertical, int sampleFrom, int sampleTo, int searchFrom, int searchTo)
    {
        var edges = new List<Edge>();
        int size = vertical ? width : height;
        int gap = Math.Max(2, scale);
        for (int position = Math.Max(gap + 1, searchFrom); position < Math.Min(size - gap - 1, searchTo); position += scale)
        {
            double score = EdgeStrength(vertical, position, sampleFrom, sampleTo, gap);
            if (score < .47) continue;
            if (edges.Count > 0 && position - edges[^1].Position < Math.Max(8, scale * 3))
            {
                if (score > edges[^1].Score) edges[^1] = new Edge(position, score);
            }
            else edges.Add(new Edge(position, score));
        }
        // Refine coarse proposals in original screenshot pixels.
        for (int i = 0; i < edges.Count; i++)
        {
            var coarse = edges[i];
            var best = new Edge(coarse.Position, 0);
            for (int p = Math.Max(1, coarse.Position - scale - 2); p <= Math.Min(size - 1, coarse.Position + scale + 2); p++)
            {
                double score = EdgeStrength(vertical, p, sampleFrom, sampleTo, 0);
                if (score > best.Score) best = new Edge(p, score);
            }
            edges[i] = best.Score >= .44 ? best : coarse;
        }
        return edges;
    }

    private double EdgeStrength(bool vertical, int position, int from, int to, int gap)
    {
        int extent = vertical ? height : width;
        from = Math.Clamp(from, 0, extent - 1); to = Math.Clamp(to, from, extent - 1);
        int step = Math.Max(1, (to - from) / 40), hits = 0, samples = 0;
        double strength = 0;
        for (int coordinate = from; coordinate <= to; coordinate += step)
        {
            int distance = vertical ? Difference(position - Math.Max(1, gap), coordinate, position + gap, coordinate)
                                    : Difference(coordinate, position - Math.Max(1, gap), coordinate, position + gap);
            if (distance >= 18) hits++;
            strength += Math.Min(distance, 90) / 90.0;
            samples++;
        }
        double consistency = (double)hits / samples;
        return consistency < .44 ? 0 : consistency * .75 + strength / samples * .25;
    }

    private Rectangle WallRange(int left, int right, int pointerY)
    {
        int window = Math.Max(8, scale * 5);
        bool Supported(int y)
        {
            int hits = 0, count = 0;
            for (int row = Math.Max(0, y - window); row <= Math.Min(height - 1, y + window); row += Math.Max(1, scale))
            {
                int l = left == 0 ? 100 : Difference(left - 2, row, left + 2, row);
                int r = right == width ? 100 : Difference(right - 2, row, right + 2, row);
                if ((l >= 10 && r >= 10) || (l >= 30 && r >= 5) || (r >= 30 && l >= 5)) hits++;
                count++;
            }
            return hits >= count * .6;
        }
        if (!Supported(pointerY)) return Rectangle.Empty;
        int top = pointerY, bottom = pointerY;
        while (top > 0 && Supported(top - 1)) top--;
        while (bottom < height - 1 && Supported(bottom + 1)) bottom++;
        int Refine(int boundary)
        {
            if (boundary == 0 || boundary == height) return boundary;
            int best = boundary; double strength = .47;
            for (int row = Math.Max(1, boundary - window); row < Math.Min(height, boundary + window); row++)
            {
                double score = EdgeStrength(false, row, left + 4, right - 4, 0);
                if (score > strength) { best = row; strength = score; }
            }
            return best;
        }
        top = Refine(top); bottom = Refine(bottom + 1);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    private int Difference(int x, int y, int otherX, int otherY)
    {
        x = Math.Clamp(x, 0, width - 1); y = Math.Clamp(y, 0, height - 1);
        otherX = Math.Clamp(otherX, 0, width - 1); otherY = Math.Clamp(otherY, 0, height - 1);
        int a = (y * width + x) * 4, b = (otherY * width + otherX) * 4;
        return Math.Max(Math.Abs(pixels[a] - pixels[b]), Math.Max(Math.Abs(pixels[a + 1] - pixels[b + 1]), Math.Abs(pixels[a + 2] - pixels[b + 2])));
    }

    private double OutsideUniformity(Rectangle rectangle)
    {
        // A pane bounded by broad, quiet gutters is stronger than an equally sharp
        // dialog or card surrounded by textured content. This is visual evidence only.
        double Side(bool left)
        {
            if (left ? rectangle.Left == 0 : rectangle.Right == width) return 0;
            double[] sum = new double[3], square = new double[3]; int count = 0;
            for (int row = rectangle.Top + 3; row < rectangle.Bottom - 3; row += Math.Max(1, rectangle.Height / 32))
            foreach (int distance in new[] { 4, 10, 18 })
            {
                int column = Math.Clamp(left ? rectangle.Left - distance : rectangle.Right + distance - 1, 0, width - 1);
                int offset = (Math.Clamp(row, 0, height - 1) * width + column) * 4;
                for (int channel = 0; channel < 3; channel++) { int value = pixels[offset + channel]; sum[channel] += value; square[channel] += value * value; }
                count++;
            }
            if (count == 0) return 0;
            double variance = Enumerable.Range(0, 3).Max(c => Math.Max(0, square[c] / count - Math.Pow(sum[c] / count, 2)));
            return Math.Exp(-Math.Sqrt(variance) / 16);
        }
        return Side(true) + Side(false);
    }

    private static double Overlap(Rectangle first, Rectangle second)
    {
        var intersection = Rectangle.Intersect(first, second);
        double area = (double)intersection.Width * intersection.Height;
        return area / ((double)first.Width * first.Height + (double)second.Width * second.Height - area);
    }

    public void Dispose() { pixels = Array.Empty<byte>(); lastResult = null; }
}
