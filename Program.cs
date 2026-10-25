namespace FreeCamRecord;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length > 0 && args[0] == "--browser-test")
        {
            Environment.ExitCode = BrowserTest.Run(args.Skip(1).ToArray()); return;
        }
        if (args.Contains("--self-test") || args.Contains("--media-test"))
        {
            Environment.ExitCode = SelfTest.Run(args.Contains("--media-test"));
            return;
        }
        Application.Run(new CaptureFrame());
    }
}
