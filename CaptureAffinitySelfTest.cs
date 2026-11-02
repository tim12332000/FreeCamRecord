using System.Reflection;
using System.Text.Json;

namespace FreeCamRecord;

// Exercises real window affinity without reading screen pixels, recording, or using the clipboard.
internal static class CaptureAffinitySelfTest
{
    private static readonly List<string> results = new();

    internal static int Run()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".omx", "verification", "capture-affinity-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        results.Clear();
        try
        {
            UiText.SetLanguage("zh-TW");
            using var frame = new CaptureFrame(new Preferences { Language = "zh-TW", Directory = directory, SystemAudio = false, Microphone = false });
            frame.Bounds = new Rectangle(140, 100, 485, 344);
            frame.Show();
            Application.DoEvents();
            Field<System.Windows.Forms.Timer>(frame, "timer").Stop();
            AssertAffinity(frame, 0, "Idle window remains available to Windows Snipping Tool");
            AssertGeometry(frame, frame.Bounds, frame.CaptureArea);

            var bounds = frame.Bounds;
            var captureArea = frame.CaptureArea;
            bool supported = SetExclusion(frame, true);
            if (supported)
            {
                AssertAffinity(frame, 0x11, "Capture exclusion enables native affinity and state");
                AssertGeometry(frame, bounds, captureArea);
                Check(SetExclusion(frame, false), "Capture exclusion can be cleared");
                AssertAffinity(frame, 0, "Completed capture returns the window to normal affinity");
                AssertGeometry(frame, bounds, captureArea);

                typeof(CaptureFrame).GetField("toolbarInsideSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(frame, true);
                typeof(CaptureFrame).GetMethod("UpdateFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(frame, null);
                captureArea = frame.CaptureArea;
                Check(SetExclusion(frame, true), "Inside-selection toolbar supports exclusion");
                AssertAffinity(frame, 0x11, "Inside-selection capture excludes the toolbar");
                AssertGeometry(frame, bounds, captureArea);
                Check(SetExclusion(frame, false), "Inside-selection exclusion can be cleared");
                AssertAffinity(frame, 0, "Idle inside-selection toolbar remains capturable");
                AssertGeometry(frame, bounds, captureArea);
            }
            else
            {
                AssertAffinity(frame, 0, "Unsupported exclusion leaves native affinity and state normal");
                results.Add("SKIPPED: This Windows environment rejected WDA_EXCLUDEFROMCAPTURE; enabled-affinity checks require native support.");
            }

            frame.Hide();
            frame.Show();
            Application.DoEvents();
            AssertAffinity(frame, 0, "Hide/show does not reapply capture exclusion");
            frame.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            Check(frame.WindowState == FormWindowState.Minimized, "Window can minimize");
            AssertAffinity(frame, 0, "Minimized idle window retains normal affinity");
            frame.WindowState = FormWindowState.Normal;
            Application.DoEvents();
            Check(frame.WindowState == FormWindowState.Normal, "Window can restore");
            AssertAffinity(frame, 0, "Restored idle window retains normal affinity");
            AssertGeometry(frame, bounds, captureArea);

            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Pass = true, CaptureExclusionSupported = supported, Checks = results.Count(x => !x.StartsWith("SKIPPED:")), Results = results }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString());
            return 1;
        }
        finally { UiText.SetLanguage("zh-TW"); }
    }

    private static bool SetExclusion(CaptureFrame frame, bool exclude)
    {
        try
        {
            return (bool)typeof(CaptureFrame).GetMethod("SetCaptureExclusion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(frame, new object[] { exclude })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw new InvalidOperationException("SetCaptureExclusion failed during affinity verification.", ex.InnerException);
        }
    }

    private static void AssertAffinity(CaptureFrame frame, uint expected, string message)
    {
        Check(Native.GetWindowDisplayAffinity(frame.Handle, out uint actual), "Native window affinity can be queried");
        Check(actual == expected, message + $" (expected 0x{expected:X}, actual 0x{actual:X})");
        Check(Field<bool>(frame, "excludedFromCapture") == (expected == 0x11), "Managed exclusion state agrees with native affinity");
    }

    private static void AssertGeometry(CaptureFrame frame, Rectangle bounds, Rectangle captureArea)
    {
        Check(frame.Bounds == bounds, "Affinity lifecycle preserves frame bounds");
        Check(frame.CaptureArea == captureArea, "Affinity lifecycle preserves capture bounds");
        Check(frame.Region != null && !frame.Region.IsVisible(CaptureFrame.Edge + 44, CaptureFrame.Toolbar + 44), "Capture area retains its transparent input hole");
    }

    private static T Field<T>(CaptureFrame frame, string name) => (T)typeof(CaptureFrame).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        results.Add(message);
    }
}
