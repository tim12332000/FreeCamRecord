using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FreeCamRecord;

internal enum ArtIcon { Menu, Record, Stop, Close, Speaker, Microphone, Folder, Size, Check }

internal static class ArtIcons
{
    // The caller owns the returned bitmap. Coordinates share a 24-unit optical grid.
    internal static Bitmap Create(ArtIcon kind, int size, Color color)
    {
        if (size < 1 || size > 1024) throw new ArgumentOutOfRangeException(nameof(size));
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 24f, size / 24f);
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        void Line(float x, float y, float x2, float y2) => g.DrawLine(pen, x, y, x2, y2);
        switch (kind)
        {
            case ArtIcon.Menu:
                Line(5, 6, 19, 6); Line(5, 12, 19, 12); Line(5, 18, 19, 18); break;
            case ArtIcon.Record:
                g.FillEllipse(brush, 5, 5, 14, 14); break;
            case ArtIcon.Stop:
                g.FillRectangle(brush, 5, 5, 14, 14); break;
            case ArtIcon.Close:
                Line(6, 6, 18, 18); Line(18, 6, 6, 18); break;
            case ArtIcon.Speaker:
                g.DrawPolygon(pen, new PointF[] { new(4, 9), new(8, 9), new(13, 5), new(13, 19), new(8, 15), new(4, 15) });
                g.DrawArc(pen, 10, 7, 8, 10, -60, 120); g.DrawArc(pen, 10, 3, 13, 18, -55, 110); break;
            case ArtIcon.Microphone:
                using (var capsule = new GraphicsPath())
                {
                    capsule.AddArc(9, 3, 6, 6, 180, 180); capsule.AddArc(9, 9, 6, 6, 0, 180); capsule.CloseFigure();
                    g.DrawPath(pen, capsule);
                }
                g.DrawArc(pen, 6, 7, 12, 11, 0, 180); Line(12, 18, 12, 21); Line(9, 21, 15, 21); break;
            case ArtIcon.Folder:
                g.DrawPolygon(pen, new PointF[] { new(3, 7), new(3, 19), new(21, 19), new(21, 8), new(12, 8), new(10, 5), new(3, 5) });
                Line(4, 10, 20, 10); break;
            case ArtIcon.Size:
                Line(4, 9, 4, 4); Line(4, 4, 9, 4); Line(15, 4, 20, 4); Line(20, 4, 20, 9);
                Line(20, 15, 20, 20); Line(20, 20, 15, 20); Line(9, 20, 4, 20); Line(4, 20, 4, 15); break;
            case ArtIcon.Check:
                Line(5, 12, 10, 17); Line(10, 17, 19, 7); break;
            default: bitmap.Dispose(); throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return bitmap;
    }
}
