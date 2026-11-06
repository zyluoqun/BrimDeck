using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BrimDeck.Native;

internal static class CapsuleWorkAreaChecks
{
    // Opt-in because this temporarily reserves real desktop space. Every path restores it in finally.
    public static void Run(Action<string, bool> check)
    {
        var (bounds, original, _) = WindowsHost.PrimaryScreen();
        var foreground = GetForegroundWindow();
        Window? window = null;
        var reservation = new CapsuleWorkArea(() => { });
        try
        {
            reservation.Update(bounds, 48); Pump();
            var work = WindowsHost.PrimaryScreen().Work;
            check("The shell reserves exactly 48 pixels above maximized windows", work.Top == original.Top + 48
                && work.Left == original.Left && work.Right == original.Right && work.Bottom == original.Bottom);
            check("The capsule retains its original work area", reservation.LayoutWorkArea(bounds, work) == original);
            check("Reserving space does not steal keyboard focus", GetForegroundWindow() == foreground);
            for (int i = 0; i < 10; i++) { reservation.Update(bounds, 48); Pump(); }
            check("Repeated work-area notifications do not accumulate height", WindowsHost.PrimaryScreen().Work == work);
            window = new Window { Title = "BrimDeck work-area test", Width = 400, Height = 300, ShowInTaskbar = false, ShowActivated = false };
            window.Show(); window.WindowState = WindowState.Maximized; Pump();
            DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 9, out var frame, Marshal.SizeOf<RECT>());
            check("A real maximized window stays below the reserved strip", frame.Top >= work.Top && frame.Top < work.Top + 16);
            reservation.Update(bounds, 64); Pump();
            check("Resizing the capsule updates rather than adds to its strip", WindowsHost.PrimaryScreen().Work.Top == original.Top + 64);
            DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 9, out frame, Marshal.SizeOf<RECT>());
            check("An already maximized window follows a changed reservation", frame.Top >= original.Top + 64 && frame.Top < original.Top + 80);
            reservation.Update(bounds, 0); Pump();
            check("Disabling avoidance restores the exact original work area", WindowsHost.PrimaryScreen().Work == original);
            DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 9, out frame, Marshal.SizeOf<RECT>());
            check("An already maximized window expands back after disabling", frame.Top >= original.Top && frame.Top < original.Top + 16);
            reservation.Update(bounds, 32); Pump();
            check("Avoidance can be enabled again", WindowsHost.PrimaryScreen().Work.Top == original.Top + 32);
        }
        finally { window?.Close(); reservation.Dispose(); Pump(); }
        check("Disposal releases all reserved desktop space", WindowsHost.PrimaryScreen().Work == original);
        reservation.Dispose();
        check("Repeated disposal leaves the work area unchanged", WindowsHost.PrimaryScreen().Work == original);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out RECT value, int size);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
}
