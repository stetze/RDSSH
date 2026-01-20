using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using WinRT.Interop;

namespace RDSSH.Controls
{
    public sealed class NativeChildHwndHost : Grid, IDisposable
    {
        private IntPtr _topLevelHwnd;
        private IntPtr _childHwnd;
        private bool _isLoaded;
        private bool _disposed;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private readonly TaskCompletionSource<IntPtr> _hwndTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly string _activeXProgId;

        public IntPtr ChildHwnd => _childHwnd;

        public event EventHandler<IntPtr>? ChildHwndCreated;
        public event EventHandler? BoundsUpdated;

        public Window? HostWindow { get; set; }

        public NativeChildHwndHost(string activeXProgId)
        {
            _activeXProgId = string.IsNullOrWhiteSpace(activeXProgId)
                ? throw new ArgumentException("activeXProgId required", nameof(activeXProgId))
                : activeXProgId;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;

            PointerPressed += OnPointerPressed;

            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_disposed) return;
            if (_childHwnd == IntPtr.Zero) return;

            try
            {
                // sicherstellen, dass unser Fenster Eingabe bekommt
                if (_topLevelHwnd != IntPtr.Zero)
                    SetForegroundWindow(_topLevelHwnd);

                // Fokus auf AtlAxWin
                SetFocus(_childHwnd);

                // Fokus auf inneres Child (RDP render window)
                var inner = FindWindowEx(_childHwnd, IntPtr.Zero, null, null);
                if (inner != IntPtr.Zero)
                    SetFocus(inner);

                e.Handled = true;
            }
            catch { }
        }

        public Task<IntPtr> WaitForChildHwndAsync() => _hwndTcs.Task;

        protected override Size ArrangeOverride(Size finalSize)
        {
            var s = base.ArrangeOverride(finalSize);
            UpdateBounds();
            return s;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBounds();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_disposed || _isLoaded)
                return;

            _isLoaded = true;

            var window = HostWindow ?? throw new InvalidOperationException("HostWindow not set on NativeChildHwndHost.");
            _topLevelHwnd = WindowNative.GetWindowHandle(window);

            if (_topLevelHwnd == IntPtr.Zero)
                throw new InvalidOperationException("Top-level HWND is null.");

            EnsureChildWindow();

            DispatcherQueue.TryEnqueue(UpdateBounds);
            DispatcherQueue.TryEnqueue(UpdateBounds);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Hide();
            _isLoaded = false;
        }

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;

        private const int WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x00000002;

        private void EnsureChildWindow()
        {
            if (_disposed) return;
            if (_childHwnd != IntPtr.Zero) return;
            if (_topLevelHwnd == IntPtr.Zero) throw new InvalidOperationException("Parent HWND not initialized.");

            EnsureAtl();

            _childHwnd = CreateWindowExW(
                WS_EX_LAYERED,
                "AtlAxWin",
                _activeXProgId,
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                0, 0, 100, 100,
                _topLevelHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_childHwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CreateWindowExW failed for AtlAxWin. GetLastError={err}");
            }

            SetLayeredWindowAttributes(_childHwnd, 0, 255, LWA_ALPHA);
            ShowWindow(_childHwnd, SW_SHOW);

            _hwndTcs.TrySetResult(_childHwnd);
            ChildHwndCreated?.Invoke(this, _childHwnd);

            Debug.WriteLine($"[NativeChildHwndHost] Child HWND created: 0x{_childHwnd:X} (ProgId='{_activeXProgId}', Class='{GetClassNameOf(_childHwnd)}')");
        }

        private void UpdateBounds()
        {
            if (_disposed) return;
            if (_childHwnd == IntPtr.Zero) return;
            if (XamlRoot is null) return;
            if (ActualWidth <= 1 || ActualHeight <= 1) return;

            try
            {
                uint dpi = GetDpiForWindow(_topLevelHwnd);
                double scale = dpi / 96.0;

                var gt = TransformToVisual(null);
                var topLeftDip = gt.TransformPoint(new Point(0, 0));

                int x = (int)Math.Round(topLeftDip.X * scale);
                int y = (int)Math.Round(topLeftDip.Y * scale);
                int w = (int)Math.Round(ActualWidth * scale);
                int h = (int)Math.Round(ActualHeight * scale);

                if (w < 1) w = 1;
                if (h < 1) h = 1;

                SetWindowPos(
                    _childHwnd,
                    HWND_TOP,
                    x, y, w, h,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);

                InvalidateRect(_childHwnd, IntPtr.Zero, true);

                BoundsUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeChildHwndHost] UpdateBounds exception: {ex}");
            }
        }

        public void Show()
        {
            if (_disposed) return;
            if (_childHwnd != IntPtr.Zero)
            {
                ShowWindow(_childHwnd, SW_SHOW);
                UpdateBounds();
            }
        }

        public void Hide()
        {
            if (_childHwnd != IntPtr.Zero) ShowWindow(_childHwnd, SW_HIDE);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
            SizeChanged -= OnSizeChanged;
            PointerPressed -= OnPointerPressed;

            if (_childHwnd != IntPtr.Zero)
            {
                try { ShowWindow(_childHwnd, SW_HIDE); } catch { }
                try { DestroyWindow(_childHwnd); } catch { }
                _childHwnd = IntPtr.Zero;
            }

            _topLevelHwnd = IntPtr.Zero;
            _isLoaded = false;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        private static readonly IntPtr HWND_TOP = new IntPtr(0);

        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy,
            uint uFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private static string GetClassNameOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            _ = GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool _atlInitialized;

        [DllImport("atl.dll", CharSet = CharSet.Unicode)]
        private static extern bool AtlAxWinInit();

        private static void EnsureAtl()
        {
            if (_atlInitialized) return;
            _atlInitialized = AtlAxWinInit();
            if (!_atlInitialized)
                throw new InvalidOperationException("AtlAxWinInit failed.");
        }
    }
}
