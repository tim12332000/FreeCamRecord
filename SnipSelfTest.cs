using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;

namespace FreeCamRecord;

// Synthetic selection and PNG tests; no desktop capture, dialogs, or clipboard mutation.
internal static class SnipSelfTest
{
    private static readonly List<string> results = new();

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        results.Add(description);
    }

    private static void Mouse(RegionSelector selector, string method, Point point)
    {
        typeof(RegionSelector).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(selector, new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) });
    }

    private static void Drag(RegionSelector selector, Point start, Point end)
    {
        Mouse(selector, "OnMouseDown", start);
        Mouse(selector, "OnMouseMove", end);
        Mouse(selector, "OnMouseUp", end);
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void Click(Button button)
    {
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(button, new object[] { EventArgs.Empty });
        Application.DoEvents();
    }

    private static void CheckChrome(SnipPreview form, bool english)
    {
        Check(form.FormBorderStyle == FormBorderStyle.None, "Preview uses its own window chrome");
        var buttons = new[] { Field<Button>(form, "saveButton"), Field<Button>(form, "copyButton"),
            Field<Button>(form, "minimizeButton"), Field<Button>(form, "maximizeButton"), Field<Button>(form, "closeButton") };
        Check(buttons[2].AccessibleName == (english ? "Minimize preview" : "最小化預覽"), "Minimize preview accessibility translated");
        Check(buttons[3].AccessibleName == (english ? "Maximize or restore preview" : "最大化或還原預覽"), "Maximize/restore preview accessibility translated");
        Check(buttons[4].AccessibleName == (english ? "Close preview" : "關閉預覽"), "Close preview accessibility translated");
        foreach (var button in buttons)
        {
            Check(button.Enabled && button.Width > 0 && button.Height > 0 && button.Image != null, "Preview action remains available with an icon: " + button.AccessibleName);
            Check(button.Left >= 0 && button.Top >= 0 && button.Right <= button.Parent!.ClientSize.Width && button.Bottom <= button.Parent.ClientSize.Height, "Preview action stays inside chrome: " + button.AccessibleName);
            Check(buttons.All(other => other == button || !other.Bounds.IntersectsWith(button.Bounds)), "Preview actions do not overlap: " + button.AccessibleName);
        }
    }

    private static void CheckNaturalImage(SnipPreview form, Size expected)
    {
        var picture = Field<PictureBox>(form, "preview");
        var canvas = Field<Panel>(form, "canvas");
        Check(picture.SizeMode == PictureBoxSizeMode.AutoSize && picture.Size == expected && picture.Image!.Size == expected, "Preview retains natural image dimensions");
        Check(picture.Left == (canvas.ClientSize.Width - expected.Width) / 2 && picture.Top == (canvas.ClientSize.Height - expected.Height) / 2,
            "Small image remains centered inside preview canvas");
    }

    private static void ComparePixels(Bitmap actual, Bitmap expected, string description)
    {
        Check(actual.Size == expected.Size, description + ": dimensions");
        for (int y = 0; y < actual.Height; y += 7)
            for (int x = 0; x < actual.Width; x += 7)
                if (actual.GetPixel(x, y).ToArgb() != expected.GetPixel(x, y).ToArgb())
                    throw new InvalidOperationException(description + $": wrong pixel at {x},{y}");
        Check(actual.GetPixel(actual.Width - 1, actual.Height - 1).ToArgb() == expected.GetPixel(expected.Width - 1, expected.Height - 1).ToArgb(), description + ": original pixels preserved");
    }

    internal static int Run()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".omx", "verification", "snip-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        try
        {
            using var snapshot = new Bitmap(1280, 720, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(snapshot))
            {
                graphics.Clear(Color.FromArgb(25, 38, 55));
                graphics.FillRectangle(Brushes.CornflowerBlue, 90, 80, 310, 250);
                graphics.FillRectangle(Brushes.OrangeRed, 170, 140, 90, 75);
                graphics.FillRectangle(Brushes.Gold, 280, 240, 80, 60);
            }
            var origin = new Point(-1280, -120);
            var localArea = new Rectangle(100, 100, 300, 200);
            using (var selector = new RegionSelector(snapshot, origin, new Size(1, 1), screenshotMode: true))
            {
                Drag(selector, localArea.Location, new Point(localArea.Right, localArea.Bottom));
                Check(selector.DialogResult == DialogResult.OK, "Screenshot drag opens preview without extra confirmation");
                Check(selector.SelectedArea == new Rectangle(localArea.X + origin.X, localArea.Y + origin.Y, localArea.Width, localArea.Height), "Selection preserves negative monitor coordinates");
                var cropArea = selector.SelectedArea;
                cropArea.Offset(-origin.X, -origin.Y);
                using var cropped = snapshot.Clone(cropArea, PixelFormat.Format32bppRgb);
                using var expected = snapshot.Clone(localArea, PixelFormat.Format32bppRgb);
                ComparePixels(cropped, expected, "Crop uses unmodified desktop snapshot");
            }
            using (var selector = new RegionSelector(snapshot, origin, new Size(1, 1), screenshotMode: true))
            {
                Drag(selector, new Point(400, 300), new Point(100, 100));
                Check(selector.DialogResult == DialogResult.OK && selector.SelectedArea.Size == localArea.Size, "Reverse-direction screenshot drag selects exact dimensions");
            }
            foreach (bool reverse in new[] { false, true })
            {
                using var selector = new RegionSelector(snapshot, origin, new Size(1, 1), screenshotMode: true);
                Drag(selector, new Point(500, 400), new Point(reverse ? 499 : 501, reverse ? 399 : 401));
                Check(selector.DialogResult == DialogResult.OK && selector.SelectedArea.Size == new Size(1, 1), $"One-pixel drag selects screenshot (reverse={reverse})");
            }
            using (var selector = new RegionSelector(snapshot, origin, new Size(1, 1), screenshotMode: true))
            {
                Mouse(selector, "OnMouseDown", new Point(600, 500));
                Mouse(selector, "OnMouseUp", new Point(600, 500));
                Check(selector.DialogResult != DialogResult.OK, "Empty selection does not open a preview");
            }
            using (var selector = new RegionSelector(snapshot, origin, new Size(1, 1), screenshotMode: true))
            {
                typeof(RegionSelector).GetField("selection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(selector, new Rectangle(123, 145, 1, 1));
                Mouse(selector, "OnMouseDown", new Point(123, 145));
                Mouse(selector, "OnMouseUp", new Point(123, 145));
                Check(selector.DialogResult == DialogResult.OK && selector.SelectedArea.Size == new Size(1, 1), "One-pixel screenshot proposal remains valid");
            }
            using (var selector = new RegionSelector(snapshot, origin, new Size(312, 120)))
            {
                Drag(selector, new Point(100, 100), new Point(500, 350));
                Check(selector.DialogResult != DialogResult.OK, "Recording selection still requires explicit confirmation");
            }

            foreach (string language in new[] { "zh-TW", "en" })
            {
                UiText.SetLanguage(language);
                bool english = language == "en";
                var input = snapshot.Clone(localArea, PixelFormat.Format32bppRgb);
                using var expected = (Bitmap)input.Clone();
                using var form = new SnipPreview(input, directory);
                input.Dispose();
                Check(form.Text == (english ? "Screenshot preview" : "截圖預覽"), "Preview title translated: " + language);
                var save = Field<Button>(form, "saveButton");
                var copy = Field<Button>(form, "copyButton");
                Check(save.AccessibleName == (english ? "Save screenshot" : "儲存截圖") && copy.AccessibleName == (english ? "Copy screenshot" : "複製截圖"), "Save and Copy accessible names translated: " + language);
                Check(save.Enabled && copy.Enabled, "Save and Copy are available before either action: " + language);
                Check(save.Parent == copy.Parent && !save.Bounds.IntersectsWith(copy.Bounds), "Preview action buttons do not overlap: " + language);
                foreach (var button in new[] { save, copy })
                {
                    Check(button.Left >= 0 && button.Right <= button.Parent!.ClientSize.Width, "Preview action stays inside toolbar: " + language);
                    Check(TextRenderer.MeasureText(button.Text, button.Font).Width + (button.Image == null ? 8 : 30) <= button.Width, "Preview action label fits: " + language);
                }
                using var displayed = new Bitmap(Field<PictureBox>(form, "preview").Image!);
                ComparePixels(displayed, expected, "Preview owns an independent image after source disposal");
                var png = Path.Combine(directory, "saved-" + language + ".png");
                form.SaveImage(png);
                using (var saved = new Bitmap(png)) ComparePixels(saved, expected, "Saved PNG preserves the selected image");
                bool failed = false;
                try { form.SaveImage(directory); }
                catch { failed = true; }
                Check(failed, "Invalid PNG output path reports failure");
                Check(save.Enabled && copy.Enabled, "Saving does not remove Copy or Save actions");
                form.Show();
                Application.DoEvents();
                form.ClientSize = new Size(720, 480);
                Application.DoEvents();
                CheckChrome(form, english);
                CheckNaturalImage(form, expected.Size);
                var beforeWindowState = form.ClientSize;
                Click(Field<Button>(form, "minimizeButton"));
                Check(form.WindowState == FormWindowState.Minimized, "Custom minimize button minimizes preview");
                form.WindowState = FormWindowState.Normal;
                Application.DoEvents();
                Click(Field<Button>(form, "maximizeButton"));
                if (form.WindowState == FormWindowState.Maximized)
                {
                    CheckChrome(form, english);
                    Click(Field<Button>(form, "maximizeButton"));
                    Check(form.WindowState == FormWindowState.Normal && form.ClientSize == beforeWindowState, "Custom maximize button restores previous preview dimensions");
                }
                else results.Add("NOT TESTED: native preview maximization is unavailable on this test desktop");
                form.WindowState = FormWindowState.Normal;
                form.ClientSize = new Size(920, 620);
                Application.DoEvents();
                CheckNaturalImage(form, expected.Size);
                var resizedPng = Path.Combine(directory, "resized-" + language + ".png");
                form.SaveImage(resizedPng);
                using (var resized = new Bitmap(resizedPng)) ComparePixels(resized, expected, "Resizing preview preserves exact saved pixels");
                form.ClientSize = new Size(720, 480);
                Application.DoEvents();
                using var screenshot = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
                screenshot.Save(Path.Combine(directory, "preview-" + language + ".png"), ImageFormat.Png);
                Click(Field<Button>(form, "closeButton"));
                Check(form.IsDisposed, "Custom close button closes preview");
            }
            using (var large = new Bitmap(1600, 1000, PixelFormat.Format32bppRgb))
            {
                using (var graphics = Graphics.FromImage(large))
                {
                    graphics.Clear(Color.FromArgb(36, 53, 65));
                    graphics.FillRectangle(Brushes.MediumAquamarine, 80, 70, 580, 440);
                    graphics.FillRectangle(Brushes.OrangeRed, 1100, 760, 500, 240);
                }
                using var form = new SnipPreview(large, directory);
                form.ClientSize = new Size(720, 480);
                form.Show();
                Application.DoEvents();
                var canvas = Field<Panel>(form, "canvas");
                var picture = Field<PictureBox>(form, "preview");
                Check(canvas.AutoScroll && canvas.HorizontalScroll.Visible && canvas.VerticalScroll.Visible, "Large screenshot supports horizontal and vertical scrolling");
                var beforeScroll = picture.Location;
                canvas.AutoScrollPosition = new Point(700, 350);
                Application.DoEvents();
                Check(canvas.AutoScrollPosition.X < 0 && canvas.AutoScrollPosition.Y < 0 && picture.Location != beforeScroll, "Scrolling reveals another part of the natural-size screenshot");
                form.ClientSize = new Size(960, 640);
                Application.DoEvents();
                Check(picture.Size == large.Size && picture.Image!.Size == large.Size, "Large screenshot remains unscaled after resize and scroll");
                var largePng = Path.Combine(directory, "large-after-resize.png");
                form.SaveImage(largePng);
                using (var saved = new Bitmap(largePng)) ComparePixels(saved, large, "Large saved PNG preserves pixels after resize and scroll");
                using var screenshot = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
                screenshot.Save(Path.Combine(directory, "preview-large-scrolled.png"), ImageFormat.Png);
                form.Close();
            }
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Pass = true, Checks = results.Count, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString());
            return 1;
        }
        finally { UiText.SetLanguage("zh-TW"); }
    }
}
