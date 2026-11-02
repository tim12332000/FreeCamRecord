using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Text.Json;

namespace FreeCamRecord;

// Synthetic artwork and real control rendering only; never reads desktop pixels or the clipboard.
internal static class DesignSelfTest
{
    private static readonly List<string> results = new();
    private static string directory = "";

    internal static int Run()
    {
        directory = Path.Combine(Environment.CurrentDirectory, ".omx", "verification", "design-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var language in new[] { "zh-TW", "en" })
            {
                UiText.SetLanguage(language);
                using var frame = new CaptureFrame(new Preferences { Language = language });
                frame.Bounds = new Rectangle(140, 100, 485, 300 + CaptureFrame.Toolbar + CaptureFrame.Edge);
                frame.Show(); Application.DoEvents();
                Field<System.Windows.Forms.Timer>(frame, "timer").Stop();
                RenderFrame(frame, "frame-" + language + ".png");
                RenderMenu(frame, language);
                RenderDimensions(frame, language);
                RenderHelp(frame, language);
                frame.Width = 320;
                Application.DoEvents();
                RenderFrame(frame, "frame-compact-" + language + ".png");
                typeof(CaptureFrame).GetField("toolbarInsideSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(frame, true);
                Invoke(frame, "UpdateFrame");
                RenderFrame(frame, "frame-inside-" + language + ".png");
                typeof(CaptureFrame).GetField("toolbarInsideSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(frame, false);
                Invoke(frame, "UpdateFrame");
                frame.Hide();
                RenderSelector(language);
                using var document = CreateDocument(new Size(800, 450));
                using var preview = new SnipPreview(document, directory);
                preview.Show(); Application.DoEvents();
                Check(Field<PictureBox>(preview, "preview").Image!.Size == document.Size, "Preview retains original image dimensions: " + language);
                Check(preview.FormBorderStyle == FormBorderStyle.None, "Preview uses unified custom window chrome: " + language);
                var work = Screen.FromControl(preview).WorkingArea;
                if (work.Width - 40 >= document.Width + 96 && work.Height - 60 >= document.Height + 170)
                {
                    var canvas = Field<Panel>(preview, "canvas");
                    Check(!canvas.HorizontalScroll.Visible && !canvas.VerticalScroll.Visible, "Fitted preview has no unnecessary scrollbars: " + language);
                }
                Render(preview, "preview-" + language + ".png");
                preview.Hide();
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

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Invoke(object instance, string method) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        results.Add(message);
    }

    private static void Render(Control control, string filename)
    {
        using var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppRgb);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, filename), ImageFormat.Png);
    }

    private static void RenderFrame(CaptureFrame frame, string filename)
    {
        var capture = frame.CaptureArea;
        int toolbarBottom = CaptureFrame.Toolbar + (Field<bool>(frame, "toolbarInsideSelection") ? CaptureFrame.Edge : 0);
        for (int y = toolbarBottom - 10; y < toolbarBottom + CaptureFrame.Edge; y++)
            for (int x = 0; x < CaptureFrame.Edge; x++)
                Check(frame.Region!.IsVisible(x, y) && frame.Region.IsVisible(frame.Width - x - 1, y),
                    $"Toolbar joins both frame edges without a notch: {filename} x={x}, y={y}, width={frame.Width}");
        for (int x = 0; x < 12; x++)
            for (int y = 0; y < 12; y++)
                Check(frame.Region!.IsVisible(x, y) && frame.Region.IsVisible(frame.Width - x - 1, y),
                    "Toolbar has continuous square top corners: " + filename);
        using var backdrop = CreateDesktop(new Size(800, 540));
        using var rendered = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppRgb);
        frame.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
        var position = new Point((backdrop.Width - frame.Width) / 2, 92);
        var sample = new Point(position.X + CaptureFrame.Edge + 44, position.Y + CaptureFrame.Toolbar + 44);
        var before = backdrop.GetPixel(sample.X, sample.Y);
        using (var graphics = Graphics.FromImage(backdrop))
        {
            var state = graphics.Save();
            graphics.TranslateTransform(position.X, position.Y);
            using var clip = frame.Region!.Clone();
            graphics.SetClip(clip, CombineMode.Replace);
            graphics.DrawImageUnscaled(rendered, Point.Empty);
            graphics.Restore(state);
        }
        Check(!frame.Region!.IsVisible(new Point(CaptureFrame.Edge + 44, CaptureFrame.Toolbar + 44)), "Recording frame retains transparent input hole: " + filename);
        Check(backdrop.GetPixel(sample.X, sample.Y).ToArgb() == before.ToArgb(), "Frame composition preserves desktop in capture hole: " + filename);
        Check(frame.CaptureArea == capture, "Rendering does not change recording geometry: " + filename);
        backdrop.Save(Path.Combine(directory, filename), ImageFormat.Png);
    }

    private static void RenderMenu(CaptureFrame frame, string language)
    {
        Invoke(frame, "BuildMenu");
        var menu = Field<ContextMenuStrip>(frame, "menu");
        menu.Show(frame, new Point(0, CaptureFrame.Toolbar + 6));
        Application.DoEvents();
        Check(menu.Width > 150 && menu.Height > 150, "Settings menu lays out complete actions: " + language);
        Render(menu, "menu-" + language + ".png");
        menu.Close();
    }

    private static void RenderDimensions(CaptureFrame frame, string language)
    {
        var original = frame.CaptureArea;
        bool rendered = false;
        Exception? error = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form != frame && form.Text == UiText.T("錄影尺寸（像素）", "Recording size (pixels)"));
            if (dialog == null) return;
            timer.Stop();
            try
            {
                var inputs = dialog.Controls.OfType<StudioPanel>().SelectMany(field => field.Controls.OfType<NumericUpDown>()).ToArray();
                Check(inputs.Length == 2, "Dimensions dialog retains both editable numeric fields: " + language);
                foreach (var input in inputs)
                {
                    var originalValue = input.Value;
                    try
                    {
                        var text = input.Controls.OfType<TextBox>().Single();
                        var arrows = input.Controls.OfType<StudioSpinButtons>().Single();
                        var increase = arrows.Controls.OfType<Button>().Single(button => button.AccessibleName!.StartsWith(UiText.T("增加", "Increase ")));
                        var decrease = arrows.Controls.OfType<Button>().Single(button => button.AccessibleName!.StartsWith(UiText.T("減少", "Decrease ")));
                        string fieldName = language + "/" + input.AccessibleName;
                        Check(text.Visible && text.Enabled && input.Enabled, "Numeric text entry remains visible and editable: " + fieldName);
                        Check(arrows.Visible && increase.Enabled && decrease.Enabled, "Themed numeric arrows remain available: " + fieldName);
                        Check(input.Maximum - input.Minimum >= input.Increment * 2, "Numeric field has room to verify stepping: " + fieldName);
                        decimal baseline = input.Minimum + input.Increment;
                        input.Value = baseline;
                        Click(increase);
                        Check(input.Value == baseline + input.Increment, "Themed increase arrow increments numeric value: " + fieldName);
                        Click(decrease);
                        Check(input.Value == baseline, "Themed decrease arrow decrements numeric value: " + fieldName);
                        input.Value = input.Minimum; Click(decrease);
                        Check(input.Value == input.Minimum, "Themed decrease arrow clamps at minimum: " + fieldName);
                        input.Value = input.Maximum; Click(increase);
                        Check(input.Value == input.Maximum, "Themed increase arrow clamps at maximum: " + fieldName);
                        decimal typed = input.Minimum + input.Increment * 2;
                        input.Text = typed.ToString(); ValidateNumericText(input);
                        Check(input.Value == typed, "Typed numeric edit is accepted: " + fieldName);
                        input.Text = (input.Maximum + 1).ToString(); ValidateNumericText(input);
                        Check(input.Value == input.Maximum, "Typed numeric edit clamps above maximum: " + fieldName);
                        input.Text = (input.Minimum - 1).ToString(); ValidateNumericText(input);
                        Check(input.Value == input.Minimum, "Typed numeric edit clamps below minimum: " + fieldName);
                    }
                    finally { input.Value = originalValue; }
                }
                Render(dialog, "dimensions-" + language + ".png");
                rendered = true;
            }
            catch (Exception ex) { error = ex; }
            finally { dialog.DialogResult = DialogResult.Cancel; }
        };
        timer.Start();
        Invoke(frame, "EditSize");
        timer.Stop();
        if (error != null) throw error;
        Check(rendered, "Actual dimensions dialog renders: " + language);
        Check(frame.CaptureArea == original, "Canceling dimensions retains capture geometry: " + language);
    }

    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, new object[] { EventArgs.Empty });
    private static void ValidateNumericText(NumericUpDown input) => typeof(NumericUpDown).GetMethod("ValidateEditText", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, null);

    private static void RenderHelp(CaptureFrame frame, string language)
    {
        bool rendered = false;
        Exception? error = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form != frame && form.Text == "FreeCamRecord");
            if (dialog == null) return;
            timer.Stop();
            try
            {
                Render(dialog, "help-" + language + ".png");
                var dismiss = dialog.Controls.OfType<Button>().Single(button => button.DialogResult == DialogResult.OK);
                foreach (var label in dialog.Controls.OfType<Label>())
                {
                    var measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak);
                    Check(measured.Height <= label.Height, $"Help text fits without clipping ({language}, y={label.Top})");
                    Check(dialog.ClientRectangle.Contains(label.Bounds), "Help label remains within dialog: " + language);
                    Check(!label.Bounds.IntersectsWith(dismiss.Bounds), "Help text does not overlap dismissal action: " + language);
                }
                rendered = true;
            }
            catch (Exception ex) { error = ex; }
            finally { dialog.DialogResult = DialogResult.OK; }
        };
        timer.Start();
        Invoke(frame, "ShowHelp");
        timer.Stop();
        if (error != null) throw error;
        Check(rendered, "Actual Help dialog renders: " + language);
    }

    private static void RenderSelector(string language)
    {
        using var desktop = CreateDesktop(new Size(1280, 800));
        using var selector = new RegionSelector(desktop, Point.Empty, new Size(312, 120));
        selector.Show(); Application.DoEvents();
        Field<System.Windows.Forms.Timer>(selector, "hover").Stop();
        typeof(RegionSelector).GetField("selection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(selector, new Rectangle(240, 100, 680, 420));
        typeof(RegionSelector).GetField("locked", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(selector, true);
        typeof(RegionSelector).GetField("pointer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(selector, new Point(1050, 220));
        Invoke(selector, "RefreshInstructions"); Invoke(selector, "PlaceActions");
        Check(selector.SelectedArea == new Rectangle(240, 100, 680, 420), "Selector retains explicit synthetic region: " + language);
        Render(selector, "selector-" + language + ".png");
        selector.Hide();
    }

    private static Bitmap CreateDesktop(Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var background = new LinearGradientBrush(new Rectangle(Point.Empty, size), Color.FromArgb(192, 205, 209), Color.FromArgb(151, 171, 178), 35);
        graphics.FillRectangle(background, new Rectangle(Point.Empty, size));
        var paper = new Rectangle(54, 46, size.Width - 108, size.Height - 92);
        using var shadow = new SolidBrush(Color.FromArgb(125, 146, 154));
        graphics.FillRectangle(shadow, paper.X + 6, paper.Y + 8, paper.Width, paper.Height);
        using var document = CreateDocument(paper.Size);
        graphics.DrawImageUnscaled(document, paper.Location);
        return bitmap;
    }

    private static Bitmap CreateDocument(Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.FromArgb(242, 240, 234));
        using var ink = new SolidBrush(Color.FromArgb(44, 57, 61));
        using var muted = new SolidBrush(Color.FromArgb(119, 128, 126));
        using var rule = new Pen(Color.FromArgb(211, 215, 207));
        using var small = new Font("Segoe UI", 8);
        using var title = new Font("Segoe UI", 22, FontStyle.Regular);
        using var label = new Font("Segoe UI", 9, FontStyle.Bold);
        graphics.DrawString("FIELDWORK     /     ARCHITECTURE & SPACE", small, muted, 30, 21);
        graphics.DrawLine(rule, 30, 49, size.Width - 30, 49);
        graphics.DrawString("A quieter place.", title, ink, 28, 66);
        graphics.DrawString("NORTH HOUSE   —   STUDY 04", small, muted, 31, 112);
        int artTop = 152, artHeight = Math.Max(100, size.Height - 202);
        int artWidth = size.Width - 60;
        var artwork = new Rectangle(30, artTop, artWidth, artHeight);
        using var sky = new LinearGradientBrush(artwork, Color.FromArgb(168, 193, 191), Color.FromArgb(217, 221, 205), 90);
        graphics.FillRectangle(sky, artwork);
        using var terrain = new SolidBrush(Color.FromArgb(146, 164, 149));
        graphics.FillPolygon(terrain, new Point[] { new(30, artTop + artHeight), new(30, artTop + artHeight * 2 / 3), new(size.Width / 3, artTop + artHeight / 2), new(size.Width - 30, artTop + artHeight * 3 / 4), new(size.Width - 30, artTop + artHeight) });
        int buildingWidth = artWidth / 2, buildingHeight = artHeight * 3 / 5;
        int left = size.Width / 2 - buildingWidth / 2, top = artTop + artHeight / 3;
        using var wall = new SolidBrush(Color.FromArgb(223, 216, 196));
        using var side = new SolidBrush(Color.FromArgb(190, 185, 168));
        using var glass = new SolidBrush(Color.FromArgb(69, 91, 91));
        graphics.FillRectangle(wall, left, top, buildingWidth, buildingHeight);
        graphics.FillPolygon(side, new Point[] { new(left + buildingWidth, top), new(left + buildingWidth + 30, top - 20), new(left + buildingWidth + 30, top + buildingHeight - 20), new(left + buildingWidth, top + buildingHeight) });
        graphics.FillRectangle(glass, left + buildingWidth / 5, top + buildingHeight / 4, buildingWidth * 3 / 5, buildingHeight / 2);
        using var mullion = new Pen(Color.FromArgb(180, 184, 165), 3);
        graphics.DrawLine(mullion, left + buildingWidth / 2, top + buildingHeight / 4, left + buildingWidth / 2, top + buildingHeight * 3 / 4);
        graphics.DrawString("01   Material, light and open space", small, muted, 31, size.Height - 30);
        graphics.DrawString("2026", small, muted, size.Width - 65, size.Height - 30);
        return bitmap;
    }
}
