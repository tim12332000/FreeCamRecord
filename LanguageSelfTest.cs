using System.Reflection;
using System.Text.Json;

namespace FreeCamRecord;

// Hidden controls and generated pixels only. No Show/ShowDialog, input, desktop capture or devices.
internal static class LanguageSelfTest
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    internal static int Run()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, ".omx", "verification", "language-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        var summaries = new List<object>();
        try
        {
            foreach (string language in new[] { "en", "zh-TW" })
            {
                using var form = new CaptureFrame(new Preferences { Language = language });
                bool english = language == "en";
                Check(UiText.IsEnglish == english, "Stored language applied");
                Check(form.Controls.OfType<Button>().Any(b => b.Text == (english ? "REC" : "錄影")), "Record button translated");
                Check(form.Controls.OfType<Button>().Any(b => b.AccessibleName == (english ? "Take photo" : "拍照") && b.Image != null), "Photo button translated and has an icon");
                foreach (int width in new[] { 320, 359, 360, 485 })
                {
                    form.Width = width;
                    var toolbar = form.Controls.OfType<Button>().Where(b => b.Width > 0 && (b.AccessibleName != (english ? "Select recording area" : "選取錄製區域") || width >= 360)).ToArray();
                    foreach (var button in toolbar)
                    {
                        Check(button.Left >= 0 && button.Right <= form.Width, "Toolbar button stays inside frame");
                        Check(toolbar.All(other => other == button || !other.Bounds.IntersectsWith(button.Bounds)), "Toolbar buttons do not overlap");
                    }
                    using var bar = new Bitmap(width, CaptureFrame.Toolbar);
                    foreach (var button in toolbar)
                    {
                        using var tile = new Bitmap(button.Width, button.Height);
                        button.DrawToBitmap(tile, new Rectangle(Point.Empty, tile.Size));
                        using var graphics = Graphics.FromImage(bar);
                        graphics.DrawImageUnscaled(tile, button.Location);
                    }
                    bar.Save(Path.Combine(directory, $"toolbar-{language}-{width}.png"));
                }
                Check(form.Controls.OfType<Button>().Any(b => b.AccessibleName == (english ? "Select recording area" : "選取錄製區域")), "Accessibility translated");
                typeof(CaptureFrame).GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                var menu = (ContextMenuStrip)typeof(CaptureFrame).GetField("menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                string[] labels = menu.Items.Cast<ToolStripItem>().Select(i => i.Text ?? "").ToArray();
                Check(labels.Any(s => s.StartsWith(english ? "Format:" : "格式：")), "Format menu translated");
                Check(labels.Contains(english ? "System audio" : "系統音效"), "System audio menu translated");
                Check(labels.Any(s => s.StartsWith(english ? "Help / About (v0.5.0" : "Help／說明與關於（v0.5.0")), "Help displays current version");
                var languages = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Language / 語言");
                Check(languages.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Checked).Text == (english ? "English" : "繁體中文"), "Language choice checked");
                Check(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(new Preferences { Language = language }))!.Language == language, "Language serializes without modifying user settings");
                foreach (var item in new[] { (Text: UiText.T("錄影", "REC"), Width: 87, Icon: true), (Text: UiText.T("停止", "Stop"), Width: 87, Icon: true), (Text: UiText.T("儲存中…", "Saving…"), Width: 118, Icon: false) })
                    Check(TextRenderer.MeasureText(item.Text, form.Font).Width + (item.Icon ? 24 : 4) <= item.Width, "Toolbar text fits: " + item.Text);
                using var image = new Bitmap(1280, 720);
                using (var graphics = Graphics.FromImage(image)) { graphics.Clear(Color.FromArgb(12, 20, 32)); graphics.FillRectangle(Brushes.SlateGray, 350, 80, 580, 600); }
                using var selector = new RegionSelector(image, Point.Empty, new Size(312, 120));
                var buttons = selector.Controls.OfType<Panel>().Single().Controls.OfType<Button>().ToArray();
                Check(buttons.Any(b => b.Text == (english ? "Use area" : "確定範圍")), "Selection confirmation translated");
                foreach (var button in buttons)
                    Check(TextRenderer.MeasureText(button.Text, button.Font).Width + 12 <= button.Width, "Selection text fits: " + button.Text);
                typeof(RegionSelector).GetField("selection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(selector, new Rectangle(350, 80, 580, 600));
                typeof(RegionSelector).GetField("pointer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(selector, new Point(650, 300));
                typeof(RegionSelector).GetMethod("RefreshInstructions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(selector, null);
                var panel = selector.Controls.OfType<Panel>().Single(); panel.Location = new Point(300, 615);
                foreach (var label in panel.Controls.OfType<Label>()) Check(TextRenderer.MeasureText(label.Text, label.Font).Width + 4 <= label.Width, "Selection instruction fits");
                using var preview = new Bitmap(1280, 720);
                using (var graphics = Graphics.FromImage(preview))
                {
                    typeof(RegionSelector).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(selector, new object[] { new PaintEventArgs(graphics, new Rectangle(Point.Empty, preview.Size)) });
                    using var background = new SolidBrush(panel.BackColor); graphics.FillRectangle(background, panel.Bounds);
                    foreach (Control control in panel.Controls)
                    {
                        var bounds = control.Bounds; bounds.Offset(panel.Location);
                        using var fill = new SolidBrush(control.BackColor.A == 0 ? panel.BackColor : control.BackColor);
                        graphics.FillRectangle(fill, bounds);
                        TextRenderer.DrawText(graphics, control.Text, control.Font, bounds, control.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                preview.Save(Path.Combine(directory, "selection-" + language + ".png"));
                summaries.Add(new { Language = language, Labels = labels, SelectorButtons = buttons.Select(b => b.Text).ToArray() });
            }
            UiText.SetLanguage("invalid"); Check(!UiText.IsEnglish, "Unsupported languages fall back to Traditional Chinese");
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Pass = true, Checks = checks, Languages = summaries }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString()); return 1; }
        finally { UiText.SetLanguage("zh-TW"); }
    }
}
