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
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
