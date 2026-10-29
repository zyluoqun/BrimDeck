using BrimDeck.Native;

internal static class ChromeExecutableTests
{
    internal static void Run(Action<string, bool> check)
    {
        const string portable = @"D:\Program Files\Chrome\chrome.exe";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { portable };
        check("A closed unregistered portable Chrome is found on another drive, including a path with spaces",
            ChromeChartBridge.FindChrome([], [@"C:\", @"D:\"], files.Contains) == portable);
        const string installed = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        files.Add(installed);
        check("An existing installed Chrome retains priority over portable candidates",
            ChromeChartBridge.FindChrome([installed], [@"D:\"], files.Contains) == installed);
        check("A stale registered Chrome path falls back to the portable executable",
            ChromeChartBridge.FindChrome([@"C:\missing\chrome.exe"], [@"D:\"], files.Contains) == portable);
        files.Clear(); files.Add(@"E:\Chrome\chrome.exe");
        check("Portable Chrome is also detected in a drive's Chrome directory",
            ChromeChartBridge.FindChrome([], [@"E:\"], files.Contains) == @"E:\Chrome\chrome.exe");
        files.Clear(); files.Add(@"D:\Program Files (x86)\Chrome\chrome.exe");
        check("Portable Chrome supports the Program Files x86 directory",
            ChromeChartBridge.FindChrome([], [@"D:\"], files.Contains) == @"D:\Program Files (x86)\Chrome\chrome.exe");
        files.Clear();
        bool missing = false;
        try { ChromeChartBridge.FindChrome([], [@"D:\"], files.Contains); }
        catch (InvalidOperationException) { missing = true; }
        check("Missing portable paths cannot be returned as executable files", missing);
    }
}
