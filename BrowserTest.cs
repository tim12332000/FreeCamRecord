using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeCamRecord;

internal static class BrowserTest
{
    internal static int Run(string[] args)
    {
        string output = args[^1];
        Directory.CreateDirectory(output);
        try
        {
            int Number(int i) => int.Parse(args[i], CultureInfo.InvariantCulture);
            var area = new Rectangle(Number(0), Number(1), Number(2), Number(3));
            if (area.Width == 0)
            {
                IntPtr target = IntPtr.Zero;
                EnumWindows((window, _) =>
                {
                    var title = new StringBuilder(512); GetWindowText(window, title, title.Capacity);
                    if (IsWindowVisible(window) && title.ToString().StartsWith("FreeCamRecord Motion Bench")) { target = window; return false; }
                    return true;
                }, IntPtr.Zero);
                if (target == IntPtr.Zero || !GetWindowRect(target, out var bounds)) throw new InvalidOperationException("找不到網頁測試視窗。");
                area = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                area.Intersect(SystemInformation.VirtualScreen);
                SetWindowPos(target, new IntPtr(-1), 0, 0, 0, 0, 0x13);
            }
            var recording = new ScreenRecorder(area, output, false, false, Number(4), "mp4");
            recording.StartAsync().GetAwaiter().GetResult();
            Thread.Sleep(Number(5) * 1000);
            var result = recording.StopAsync().GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "recording.json"), JsonSerializer.Serialize(new { result.Path, result.Error, result.CapturedFrames, result.Seconds, result.CaptureBackend, CaptureRate = result.CapturedFrames / result.Seconds, Area = area, Fps = Number(4) }));
            return result.Error == null ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); return 1;
        }
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr order, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct WindowBounds { public int Left, Top, Right, Bottom; }
}
