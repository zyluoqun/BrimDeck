namespace BrimDeck;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.FirstOrDefault()?.StartsWith("chrome-extension://", StringComparison.Ordinal) == true)
        {
            Native.ChromeChartBridge.RunHostAsync(args[0]).GetAwaiter().GetResult();
            return;
        }
        // A newer portable version started to replace this folder's files; it does that and exits without opening the app.
        if (Core.PortableUpdate.TryRun(args, out var exitCode)) { Environment.ExitCode = exitCode; return; }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
