using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using WinRT.Interop;

namespace RDSSH.Controls
{
    public sealed class NativeChildHwndHost : Grid, IDisposable
    {
        private IntPtr _parentHwnd;
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

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBounds();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_disposed || _isLoaded)
                return;

            _isLoaded = true;

            var window = HostWindow ?? throw new InvalidOperationException("HostWindow not set on NativeChildHwndHost.");
            _parentHwnd = WindowNative.GetWindowHandle(window);

            EnsureChildWindow();

            // nach Loaded nochmal bounds setzen (Layout ist oft noch nicht final)
            DispatcherQueue.TryEnqueue(UpdateBounds);
            DispatcherQueue.TryEnqueue(UpdateBounds);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // NICHT zerstören (TabView/Virtualisierung triggert Unloaded)
            // nur verstecken
            Hide();
            _isLoaded = false;
        }

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;

        private void EnsureChildWindow()
        {
            if (_disposed) return;
            if (_childHwnd != IntPtr.Zero) return;
            if (_parentHwnd == IntPtr.Zero) throw new InvalidOperationException("Parent HWND not initialized.");

            EnsureAtl();

            // AtlAxWin: windowName = ProgID
            _childHwnd = CreateWindowExW(
                0,
                "AtlAxWin",
                _activeXProgId,
                WS_CHILD | WS_VISIBLE,
                0, 0, 100, 100,
                _parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_childHwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CreateWindowExW failed for AtlAxWin. GetLastError={err}");
            }

            ShowWindow(_childHwnd, SW_SHOW);

            _hwndTcs.TrySetResult(_childHwnd);
            ChildHwndCreated?.Invoke(this, _childHwnd);

            Debug.WriteLine($"[NativeChildHwndHost] Child HWND created: 0x{_childHwnd:X}");
        }

        private void UpdateBounds()
        {
            if (_disposed) return;
            if (_childHwnd == IntPtr.Zero) return;
            if (XamlRoot is null) return;
            if (ActualWidth <= 1 || ActualHeight <= 1) return;

            try
            {
                var scale = XamlRoot.RasterizationScale;

                var gt = TransformToVisual(null);
                var topLeftDip = gt.TransformPoint(new Point(0, 0));

                int x = (int)Math.Round(topLeftDip.X * scale);
                int y = (int)Math.Round(topLeftDip.Y * scale);
                int w = (int)Math.Round(ActualWidth * scale);
                int h = (int)Math.Round(ActualHeight * scale);

                MoveWindow(_childHwnd, x, y, w, h, true);
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
            if (_childHwnd != IntPtr.Zero) ShowWindow(_childHwnd, SW_SHOW);
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

            if (_childHwnd != IntPtr.Zero)
            {
                try { ShowWindow(_childHwnd, SW_HIDE); } catch { }
                try { DestroyWindow(_childHwnd); } catch { }
                _childHwnd = IntPtr.Zero;
            }

            _parentHwnd = IntPtr.Zero;
            _isLoaded = false;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int cx, int cy, bool repaint);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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
