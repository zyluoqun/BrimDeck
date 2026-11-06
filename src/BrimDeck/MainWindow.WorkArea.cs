using System.Windows;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class MainWindow
{
    private CapsuleWorkArea? _capsuleWorkArea;

    private (Rect Bounds, Rect Work, double Scale) LayoutScreen()
    {
        var (bounds, work, scale) = WindowsHost.MonitorScreen(MonitorHandle) ?? WindowsHost.PrimaryScreen();
        return (bounds, _capsuleWorkArea?.LayoutWorkArea(bounds, work) ?? work, scale);
    }

    private void ApplyCapsuleWorkArea()
    {
        if (!IsLoaded) return;
        if (!Settings.CapsuleAvoidanceEnabled || HiddenByUser)
        {
            _capsuleWorkArea?.Dispose(); _capsuleWorkArea = null;
            return;
        }
        var (bounds, work, scale) = LayoutScreen();
        var monitor = new DipRect(0, 0, bounds.Width / scale, bounds.Height / scale);
        var area = new DipRect((work.Left - bounds.Left) / scale, (work.Top - bounds.Top) / scale, work.Width / scale, work.Height / scale);
        int height = WorkAreaReservation.Height(Settings, monitor, area, scale);
        _capsuleWorkArea ??= new CapsuleWorkArea(_app.DisplaysChanged);
        _capsuleWorkArea.Update(bounds, height);
    }
}
