using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BrimDeck.Native;

// A transparent, click-through shell appbar reserves the strip; the overlay keeps its own layout and animations.
// The shell releases the space when this HWND is destroyed, even after a crash.
internal sealed class CapsuleWorkArea : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action _changed;
    private readonly uint _callback = RegisterWindowMessage("BrimDeck.CapsuleWorkArea");
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private Rect _screen;
    private Rect? _reserved;
    private int _height;
    private bool _registered, _positioning, _pending, _disposed;

    public CapsuleWorkArea(Action changed)
    {
        _changed = changed;
        _source = new HwndSource(new HwndSourceParameters("BrimDeck capsule work area")
        {
            WindowStyle = unchecked((int)0x90000000), // WS_POPUP | WS_VISIBLE
            ExtendedWindowStyle = 0x08000080 | 0x20, // NOACTIVATE | TOOLWINDOW | TRANSPARENT
            Width = 1, Height = 1, UsesPerPixelOpacity = true, HwndSourceHook = Hook
        }) { RootVisual = new DrawingVisual() };
    }

    public void Update(Rect screen, int height)
    {
        if (_disposed) return;
        if (_screen != screen) Unregister();
        _screen = screen; _height = height;
        if (height <= 0) { Unregister(); return; }
        if (!_registered)
        {
            var data = Data(); data.Callback = _callback;
            _registered = SHAppBarMessage(0, ref data) != UIntPtr.Zero;
        }
        if (_registered) Position();
    }

    // Do not let our own reduced work area move the capsule down on each WM_SETTINGCHANGE.
    public Rect LayoutWorkArea(Rect screen, Rect work)
    {
        if (screen != _screen || _reserved is not { Height: > 0 } reserved || work.Top < reserved.Bottom) return work;
        return new Rect(work.Left, work.Top - reserved.Height, work.Width, work.Height + reserved.Height);
    }

    private void Position()
    {
        if (_disposed || !_registered || _positioning) return;
        _positioning = true;
        try
        {
            var data = Data(); data.Edge = 1; // ABE_TOP
            data.Rect = new RECT { Left = (int)_screen.Left, Top = (int)_screen.Top,
                Right = (int)_screen.Right, Bottom = (int)_screen.Top + _height };
            SHAppBarMessage(2, ref data); // ABM_QUERYPOS: retain the taskbar and other appbars.
            data.Rect.Bottom = data.Rect.Top + Math.Min(_height, Math.Max(0, (int)_screen.Bottom - data.Rect.Top - 1));
            var next = data.Rect.ToRect();
            if (_reserved == next) return;
            _reserved = next; // Work-area broadcasts may arrive synchronously during ABM_SETPOS.
            SHAppBarMessage(3, ref data);
            _reserved = data.Rect.ToRect();
            SetWindowPos(_source.Handle, IntPtr.Zero, data.Rect.Left, data.Rect.Top,
                data.Rect.Right - data.Rect.Left, data.Rect.Bottom - data.Rect.Top, 0x0010 | 0x0004);
            _changed();
        }
        finally { _positioning = false; }
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if ((uint)message == _taskbarCreated)
        {
            _registered = false; _reserved = null;
            QueuePosition();
        }
        else if ((uint)message == _callback && wParam == (IntPtr)1 && !_positioning) QueuePosition(); // ABN_POSCHANGED
        else if (message == 0x0047 && _registered && !_positioning) // WM_WINDOWPOSCHANGED
        {
            var data = Data(); SHAppBarMessage(9, ref data);
        }
        return IntPtr.Zero;
    }

    private void QueuePosition()
    {
        if (_pending) return;
        _pending = true;
        _source.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _pending = false;
            if (!_disposed) { Update(_screen, _height); _changed(); }
        });
    }

    private APPBARDATA Data() => new() { Size = Marshal.SizeOf<APPBARDATA>(), Window = _source.Handle };
    private void Unregister()
    {
        if (!_registered) return;
        _registered = false; _reserved = null;
        var data = Data(); SHAppBarMessage(1, ref data);
        _changed();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Unregister(); _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int Size; public IntPtr Window; public uint Callback, Edge; public RECT Rect; public IntPtr Param;
    }
    [DllImport("shell32")] private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
