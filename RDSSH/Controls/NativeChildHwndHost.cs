using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using WinRT.Interop;

namespace RDSSH.Controls;

public sealed class NativeChildHwndHost : Grid, IDisposable
{
    private IntPtr _parentHwnd;
    private IntPtr _childHwnd;
    private bool _isLoaded;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private AppWindow? _appWindow;

    private readonly TaskCompletionSource<IntPtr> _hwndTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IntPtr ChildHwnd => _childHwnd;

    public event EventHandler<IntPtr>? ChildHwndCreated;

    public Window? HostWindow { get; set; }

    public NativeChildHwndHost()
    {
        Loaded += OnLoaded;

        SizeChanged += (_, __) => UpdateBounds();

        GotFocus += (_, __) => FocusChild();
        PointerPressed += (_, __) => FocusChild();

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public Task<IntPtr> WaitForChildHwndAsync() => _hwndTcs.Task;

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = base.ArrangeOverride(finalSize);
        UpdateBounds();
        return s;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NativeChildHwndHost] OnLoaded");

        if (_isLoaded) return;
        _isLoaded = true;

        var window = HostWindow ?? throw new InvalidOperationException("HostWindow not set on NativeChildHwndHost.");
        _parentHwnd = WindowNative.GetWindowHandle(window);

        // AppWindow move/size hook (damit Overlay beim Verschieben mitgeht)
        try
        {
            if (_appWindow != null)
            {
                try { _appWindow.Changed -= AppWindow_Changed; } catch { }
                _appWindow = null;
            }

            var windowId = Win32Interop.GetWindowIdFromWindow(_parentHwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Changed += AppWindow_Changed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NativeChildHwndHost] AppWindow hook failed: {ex.Message}");
            _appWindow = null;
        }

        EnsureChildWindow();

        UpdateBounds();
        DispatcherQueue.TryEnqueue(() => UpdateBounds());
        DispatcherQueue.TryEnqueue(() => UpdateBounds());
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            DispatcherQueue.TryEnqueue(() => UpdateBounds());
        }
    }

    // Overlay-Workaround: WS_POPUP owned by HostWindow
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int SS_BLACKRECT = 0x00000004;

    private void EnsureChildWindow()
    {
        if (_childHwnd != IntPtr.Zero) return;
        if (_parentHwnd == IntPtr.Zero) throw new InvalidOperationException("Parent HWND not initialized.");

        _childHwnd = CreateWindowExW(
            0,
            "STATIC",
            "",
            WS_POPUP | WS_VISIBLE | SS_BLACKRECT,
            0, 0, 100, 100,
            _parentHwnd, // WS_POPUP: Owner
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_childHwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowExW failed for native hwnd.");

        ShowWindow(_childHwnd, SW_SHOW);

        // Await + Event
        _hwndTcs.TrySetResult(_childHwnd);
        ChildHwndCreated?.Invoke(this, _childHwnd);
    }

    private void UpdateBounds()
    {
        if (_childHwnd == IntPtr.Zero)
        {
            Debug.WriteLine("[NativeChildHwndHost] UpdateBounds skipped: child hwnd == 0");
            return;
        }

        if (XamlRoot is null)
        {
            Debug.WriteLine("[NativeChildHwndHost] UpdateBounds skipped: XamlRoot is null");
            return;
        }

        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            Debug.WriteLine($"[NativeChildHwndHost] UpdateBounds skipped size w={ActualWidth} h={ActualHeight}");
            return;
        }

        var scale = XamlRoot.RasterizationScale;

        // Position innerhalb des XAML-Roots (DIP)
        GeneralTransform gt = TransformToVisual(null);
        Point topLeftDip = gt.TransformPoint(new Point(0, 0));

        // DIP -> Pixel (Client Koordinaten)
        int xClient = (int)Math.Round(topLeftDip.X * scale);
        int yClient = (int)Math.Round(topLeftDip.Y * scale);
        int w = (int)Math.Round(ActualWidth * scale);
        int h = (int)Math.Round(ActualHeight * scale);

        // Client (0,0) -> Screen Ursprung
        POINT origin = new() { X = 0, Y = 0 };
        ClientToScreen(_parentHwnd, ref origin);

        int xScreen = origin.X + xClient;
        int yScreen = origin.Y + yClient;

        SetWindowPos(_childHwnd, HWND_TOP, xScreen, yScreen, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);

        Debug.WriteLine($"[NativeChildHwndHost] bounds screen x={xScreen} y={yScreen} w={w} h={h}");
    }

    public void Show()
    {
        if (_childHwnd != IntPtr.Zero) ShowWindow(_childHwnd, SW_SHOW);
    }

    public void Hide()
    {
        if (_childHwnd != IntPtr.Zero) ShowWindow(_childHwnd, SW_HIDE);
    }

    public void FocusChild()
    {
        if (_childHwnd != IntPtr.Zero) SetFocus(_childHwnd);
    }

    public void Dispose()
    {
        Debug.WriteLine("[NativeChildHwndHost] Dispose");

        if (_childHwnd != IntPtr.Zero)
        {
            try { ShowWindow(_childHwnd, SW_HIDE); } catch { }
            try { DestroyWindow(_childHwnd); } catch { }
            _childHwnd = IntPtr.Zero;
        }

        _parentHwnd = IntPtr.Zero;
        _isLoaded = false;

        if (_appWindow != null)
        {
            try { _appWindow.Changed -= AppWindow_Changed; } catch { }
            _appWindow = null;
        }
    }

    private static readonly IntPtr HWND_TOP = new(0);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);
}
