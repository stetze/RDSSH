using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDSSH.Controls;
using RDSSH.Controls.Rdp.Msrdp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinRT.Interop;

namespace RDSSH.Views
{
    public sealed partial class SessionsHostWindow : Window
    {
        public static SessionsHostWindow? Current { get; private set; }

        // ✅ Mehrere Fenster (Undock) -> Current reicht nicht. Deshalb Registry.
        private static readonly object _instancesLock = new();
        private static readonly List<WeakReference<SessionsHostWindow>> _instances = new();

        private readonly Dictionary<long, TabViewItem> _tabsByChildHwnd = new();
        private readonly Dictionary<long, MsRdpActiveXSession> _sessionsByChildHwnd = new();

        private readonly Dictionary<TabViewItem, Windows.Foundation.Point> _tabPointerStart = new();
        private bool _isClosing;

        private Windows.Foundation.Collections.IObservableVector<object>? _observableVector;
        private Windows.Foundation.Collections.VectorChangedEventHandler<object>? _vectorHandler;
        private System.Collections.Specialized.INotifyCollectionChanged? _inccRef;

        public SessionsHostWindow()
        {
            Current = this;

            lock (_instancesLock)
            {
                _instances.Add(new WeakReference<SessionsHostWindow>(this));
                CleanupDeadInstances_NoLock();
            }

            InitializeComponent();

            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
            this.ExtendsContentIntoTitleBar = true;

            try
            {
                var themeSvc = App.GetService<Contracts.Services.IThemeSelectorService>();
                RDSSH.Helpers.TitleBarHelper.UpdateTitleBar(this, themeSvc.Theme);
            }
            catch { }

            this.Closed += SessionsHostWindow_Closed;
            this.Activated += SessionsHostWindow_Activated;

            // ✅ wenn Tab ausgewählt wird: Host refreshen (das ersetzt “Tabwechsel zeigt erst Bild”)
            SessionsTabView.SelectionChanged += SessionsTabView_SelectionChanged;

            try
            {
                if (this.Content is FrameworkElement fe)
                {
                    fe.Loaded += (_, __) =>
                    {
                        try
                        {
                            if (fe.FindName("DragOverlay") is UIElement overlay)
                            {
                                App.AppTitlebar = overlay;
                                this.SetTitleBar(overlay);
                            }

                            SessionsTabView.SizeChanged += SessionsTabView_SizeChanged;
                            SessionsTabView.LayoutUpdated += SessionsTabView_LayoutUpdated;

                            try
                            {
                                if (SessionsTabView.TabItems is Windows.Foundation.Collections.IObservableVector<object> vec)
                                {
                                    _observableVector = vec;
                                    _vectorHandler = new Windows.Foundation.Collections.VectorChangedEventHandler<object>((s, ev) => UpdateDragOverlay());
                                    _observableVector.VectorChanged += _vectorHandler;
                                }
                                else if (SessionsTabView.TabItems is System.Collections.Specialized.INotifyCollectionChanged incc)
                                {
                                    _inccRef = incc;
                                    _inccRef.CollectionChanged += TabItems_CollectionChanged;
                                }
                            }
                            catch { }

                            UpdateDragOverlay();
                        }
                        catch { }
                    };
                }
            }
            catch { }
        }

        private static void CleanupDeadInstances_NoLock()
        {
            for (int i = _instances.Count - 1; i >= 0; i--)
            {
                if (!_instances[i].TryGetTarget(out _))
                    _instances.RemoveAt(i);
            }
        }

        // ✅ Global close: finde das richtige SessionsHostWindow
        public static bool TryCloseTabByChildHwndGlobal(IntPtr childHwnd)
        {
            if (childHwnd == IntPtr.Zero) return false;
            long key = childHwnd.ToInt64();

            lock (_instancesLock)
            {
                CleanupDeadInstances_NoLock();

                foreach (var wr in _instances)
                {
                    if (!wr.TryGetTarget(out var wnd)) continue;
                    if (wnd._tabsByChildHwnd.ContainsKey(key))
                    {
                        wnd.CloseTabByChildHwnd(childHwnd);
                        return true;
                    }
                }
            }
            return false;
        }

        // ✅ Wird vom Launcher aufgerufen, sobald Session existiert
        public void RegisterSessionForChildHwnd(IntPtr childHwnd, MsRdpActiveXSession session)
        {
            if (childHwnd == IntPtr.Zero || session == null) return;
            _sessionsByChildHwnd[childHwnd.ToInt64()] = session;
        }

        private void SessionsTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                // Beim Auswählen sofort bounds/refresh -> sofort Bild nach Redock
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (SessionsTabView.SelectedItem is TabViewItem tvi &&
                            tvi.Content is Grid grid &&
                            grid.Children.Count > 0 &&
                            grid.Children[0] is NativeChildHwndHost host)
                        {
                            host.ForceRefresh();
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void SessionsHostWindow_Activated(object? sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
        {
            try
            {
                if (_isClosing) return;
                if (this.Content is FrameworkElement fe && fe.FindName("DragOverlay") is UIElement overlay)
                {
                    App.AppTitlebar = overlay;
                    this.SetTitleBar(overlay);
                    UpdateDragOverlay();
                }
            }
            catch { }
        }

        private void SessionsHostWindow_Closed(object? sender, WindowEventArgs e)
        {
            _isClosing = true;

            try
            {
                SessionsTabView.SelectionChanged -= SessionsTabView_SelectionChanged;
                SessionsTabView.SizeChanged -= SessionsTabView_SizeChanged;
                SessionsTabView.LayoutUpdated -= SessionsTabView_LayoutUpdated;

                if (_observableVector != null && _vectorHandler != null)
                {
                    try { _observableVector.VectorChanged -= _vectorHandler; } catch { }
                }
                if (_inccRef != null)
                {
                    try { _inccRef.CollectionChanged -= TabItems_CollectionChanged; } catch { }
                }
            }
            catch { }

            lock (_instancesLock)
            {
                CleanupDeadInstances_NoLock();
            }
        }

        private void SessionsTabView_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateDragOverlay();
        private void SessionsTabView_LayoutUpdated(object? sender, object e) => UpdateDragOverlay();
        private void TabItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateDragOverlay();

        private void UpdateDragOverlay()
        {
            try
            {
                if (_isClosing) return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (_isClosing) return;

                        if (this.Content is not FrameworkElement fe) return;
                        if (fe.FindName("DragOverlay") is not Border overlay) return;
                        if (SessionsTabView == null) return;

                        double maxRight = 0;
                        foreach (var item in SessionsTabView.TabItems)
                        {
                            if (item is TabViewItem tvi)
                            {
                                try
                                {
                                    var transform = tvi.TransformToVisual(SessionsTabView);
                                    var pt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                                    double right = pt.X + tvi.ActualWidth;
                                    if (right > maxRight) maxRight = right;
                                }
                                catch { }
                            }
                        }

                        double left = maxRight > 0 ? Math.Ceiling(maxRight) + 8 : 44;

                        if (left < 0) left = 0;
                        if (left > SessionsTabView.ActualWidth - 24) left = Math.Max(0, SessionsTabView.ActualWidth - 24);

                        overlay.Margin = new Thickness(left, 0, 0, 0);
                        overlay.Width = Math.Max(24, SessionsTabView.ActualWidth - left);

                        try { App.AppTitlebar = overlay; this.SetTitleBar(overlay); } catch { }
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        // closing
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UpdateDragOverlay failed: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateDragOverlay enqueue failed: {ex}");
            }
        }

        public NativeChildHwndHost AddRdpTab(string title)
        {
            var host = new NativeChildHwndHost("MsTscAx.MsTscAx")
            {
                HostWindow = this
            };

            host.BoundsUpdated += (_, __) =>
            {
                try
                {
                    var child = host.ChildHwnd;
                    if (child == IntPtr.Zero) return;

                    if (!_sessionsByChildHwnd.TryGetValue(child.ToInt64(), out var session))
                        return;

                    // ✅ nur wenn verbunden (sonst knallt es gern in COM/Reflection)
                    if (!session.IsConnected) return;

                    var wnd = host.HostWindow;
                    if (wnd == null) return;

                    var hwnd = WindowNative.GetWindowHandle(wnd);
                    if (hwnd == IntPtr.Zero) return;

                    uint dpi = GetDpiForWindow(hwnd);
                    double scale = dpi / 96.0;

                    int w = (int)Math.Max(1, Math.Round(host.ActualWidth * scale));
                    int h = (int)Math.Max(1, Math.Round(host.ActualHeight * scale));

                    session.UpdateDisplay(w, h);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionsHostWindow] UpdateDisplay via BoundsUpdated failed: {ex}");
                }
            };

            var container = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            container.Children.Add(host);

            var tab = new TabViewItem
            {
                Header = title,
                Content = container
            };

            SessionsTabView.TabItems.Add(tab);
            SessionsTabView.SelectedItem = tab;

            tab.DoubleTapped += Tab_DoubleTapped;
            tab.PointerPressed += (s, e) =>
            {
                try
                {
                    var p = e.GetCurrentPoint(null).Position;
                    _tabPointerStart[(TabViewItem)s] = new Windows.Foundation.Point(p.X, p.Y);
                }
                catch { }
            };

            tab.PointerMoved += (s, e) =>
            {
                try
                {
                    if (!_tabPointerStart.TryGetValue((TabViewItem)s, out var start)) return;
                    var p = e.GetCurrentPoint(null).Position;
                    var dx = p.X - start.X;
                    var dy = p.Y - start.Y;
                    var distSq = dx * dx + dy * dy;
                    const double threshold = 12.0;
                    if (distSq > threshold * threshold)
                    {
                        if (s is TabViewItem t)
                        {
                            _tabPointerStart.Remove(t);
                            UndockTab(t);
                        }
                    }
                }
                catch { }
            };

            tab.PointerReleased += (s, e) => { try { _tabPointerStart.Remove((TabViewItem)s); } catch { } };

            host.ChildHwndCreated += (_, hwnd) =>
            {
                Debug.WriteLine($"[SessionsHostWindow] Child HWND created: 0x{hwnd:X}");
                var key = hwnd.ToInt64();
                tab.Tag = key;
                _tabsByChildHwnd[key] = tab;

                // ✅ Direkt nach Erstellung einmal refreshen
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { host.ForceRefresh(); } catch { }
                });
            };

            return host;
        }

        private void Tab_DoubleTapped(object? sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            try
            {
                if (sender is not TabViewItem tab) return;

                var main = App.SessionsWindow;
                if (main != null && ReferenceEquals(this, main))
                {
                    UndockTab(tab);
                }
                else
                {
                    var target = App.GetOrCreateSessionsWindow();
                    if (!ReferenceEquals(this, target))
                    {
                        DockTabTo(target, tab);
                    }
                }
            }
            catch { }
        }

        private void UndockTab(TabViewItem tab)
        {
            try
            {
                if (tab == null) return;
                if (tab.Content is not Grid grid) return;
                if (grid.Children.Count == 0) return;
                if (grid.Children[0] is not NativeChildHwndHost host) return;

                SessionsTabView.TabItems.Remove(tab);
                _tabsByChildHwnd.Remove(host.ChildHwnd.ToInt64());

                var wnd = new SessionsHostWindow();
                try
                {
                    var themeSvc = App.GetService<Contracts.Services.IThemeSelectorService>();
                    if (wnd.Content is FrameworkElement sfe)
                        sfe.RequestedTheme = themeSvc.Theme;
                    RDSSH.Helpers.TitleBarHelper.UpdateTitleBar(wnd, themeSvc.Theme);
                }
                catch { }

                wnd.Activate();
                wnd.BringToFront();

                grid.Children.Remove(host);
                var newContainer = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                newContainer.Children.Add(host);

                host.HostWindow = wnd;
                host.ReparentTo(WindowNative.GetWindowHandle(wnd));

                var newTab = new TabViewItem { Header = tab.Header, Content = newContainer };
                newTab.DoubleTapped += wnd.Tab_DoubleTapped;
                wnd.SessionsTabView.TabItems.Add(newTab);
                wnd.SessionsTabView.SelectedItem = newTab;
                wnd.RegisterExistingTab(newTab, host);

                // ✅ Undock refresh
                wnd.DispatcherQueue.TryEnqueue(() =>
                {
                    try { host.ForceRefresh(); } catch { }
                });

                try
                {
                    if (!ReferenceEquals(this, App.SessionsWindow) && this.SessionsTabView.TabItems.Count == 0 && !this._isClosing)
                    {
                        this._isClosing = true;
                        try { this.Close(); } catch { }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UndockTab failed: {ex}");
            }
        }

        private void DockTabTo(SessionsHostWindow target, TabViewItem tab)
        {
            try
            {
                if (tab == null) return;
                if (tab.Content is not Grid grid) return;
                if (grid.Children.Count == 0) return;
                if (grid.Children[0] is not NativeChildHwndHost host) return;

                if (ReferenceEquals(this, target)) return;
                SessionsTabView.TabItems.Remove(tab);
                _tabsByChildHwnd.Remove(host.ChildHwnd.ToInt64());

                grid.Children.Remove(host);
                var newContainer = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                newContainer.Children.Add(host);

                host.HostWindow = target;
                host.ReparentTo(WindowNative.GetWindowHandle(target));

                var newTab = new TabViewItem { Header = tab.Header, Content = newContainer };
                newTab.DoubleTapped += target.Tab_DoubleTapped;
                target.SessionsTabView.TabItems.Add(newTab);
                target.SessionsTabView.SelectedItem = newTab;
                target.RegisterExistingTab(newTab, host);
                target.BringToFront();

                // ✅ Dock refresh
                target.DispatcherQueue.TryEnqueue(() =>
                {
                    try { host.ForceRefresh(); } catch { }
                });

                try
                {
                    var themeSvc = App.GetService<Contracts.Services.IThemeSelectorService>();
                    if (target.Content is FrameworkElement sfe)
                        sfe.RequestedTheme = themeSvc.Theme;
                    RDSSH.Helpers.TitleBarHelper.UpdateTitleBar(target, themeSvc.Theme);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DockTabTo failed: {ex}");
            }
            finally
            {
                try
                {
                    if (!ReferenceEquals(this, App.SessionsWindow) && this.SessionsTabView.TabItems.Count == 0 && !this._isClosing)
                    {
                        this._isClosing = true;
                        try { this.Close(); } catch { }
                    }
                }
                catch { }
            }
        }

        public void RegisterExistingTab(TabViewItem tab, NativeChildHwndHost host)
        {
            try
            {
                if (tab == null || host == null) return;
                var key = host.ChildHwnd.ToInt64();
                tab.Tag = key;
                _tabsByChildHwnd[key] = tab;
            }
            catch { }
        }

        // Foreground / Focus helper
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        public void BringToFront()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                ShowWindow(hwnd, SW_RESTORE);
                ShowWindow(hwnd, SW_SHOW);

                var fg = GetForegroundWindow();
                uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint curThread = GetCurrentThreadId();

                AttachThreadInput(curThread, fgThread, true);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                AttachThreadInput(curThread, fgThread, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BringToFront failed: {ex}");
                try { SetForegroundWindow(WindowNative.GetWindowHandle(this)); } catch { }
            }
        }

        public void CloseTabByChildHwnd(IntPtr childHwnd)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var key = childHwnd.ToInt64();
                if (_tabsByChildHwnd.TryGetValue(key, out var tab))
                    CloseTabInternal(tab);
            });
        }

        public void FocusTabByChildHwnd(IntPtr childHwnd)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var key = childHwnd.ToInt64();
                if (_tabsByChildHwnd.TryGetValue(key, out var tab))
                {
                    SessionsTabView.SelectedItem = tab;
                    // sofort refreshen
                    try
                    {
                        if (tab.Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is NativeChildHwndHost host)
                            host.ForceRefresh();
                    }
                    catch { }
                }
            });
        }

        private void CloseHostWindowIfEmpty()
        {
            try
            {
                if (_isClosing) return;
                if (SessionsTabView != null && SessionsTabView.TabItems.Count == 0)
                {
                    _isClosing = true;
                    this.Close();
                }
            }
            catch { }
        }

        private void CloseTabInternal(TabViewItem tab)
        {
            SessionsTabView.TabItems.Remove(tab);

            if (tab.Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is NativeChildHwndHost host)
            {
                var key = host.ChildHwnd.ToInt64();
                _tabsByChildHwnd.Remove(key);

                if (_sessionsByChildHwnd.TryGetValue(key, out var session))
                {
                    try { session.Dispose(); } catch { }
                    _sessionsByChildHwnd.Remove(key);
                }

                try { host.Dispose(); } catch { }
            }
            else if (tab.Tag is long hwnd)
            {
                _tabsByChildHwnd.Remove(hwnd);

                if (_sessionsByChildHwnd.TryGetValue(hwnd, out var session))
                {
                    try { session.Dispose(); } catch { }
                    _sessionsByChildHwnd.Remove(hwnd);
                }
            }

            CloseHostWindowIfEmpty();
        }

        private void SessionsTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is not TabViewItem tvi)
                return;

            CloseTabInternal(tvi);
        }
    }
}
