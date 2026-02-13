using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDSSH.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinRT.Interop;

namespace RDSSH.Views
{
    public sealed partial class SessionsHostWindow : Window
    {
        public static SessionsHostWindow? Current { get; private set; }

        private readonly Dictionary<long, TabViewItem> _tabsByChildHwnd = new();
        private readonly Dictionary<TabViewItem, Windows.Foundation.Point> _tabPointerStart = new();
        private TabViewItem? _pendingDragTab;
        private Windows.Foundation.Point _pendingDragStart;
        private bool _dragStarted;
        private bool _isClosing;

        // Observable collection hooks for TabItems
        private Windows.Foundation.Collections.IObservableVector<object>? _observableVector;
        private Windows.Foundation.Collections.VectorChangedEventHandler<object>? _vectorHandler;
        private System.Collections.Specialized.INotifyCollectionChanged? _inccRef;

        public SessionsHostWindow()
        {
            Current = this;
            InitializeComponent();
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
            // Extend content into title bar so we can hide the default caption
            this.ExtendsContentIntoTitleBar = true;
            // Apply title bar styling for sessions window
            try
            {
                var themeSvc = App.GetService<Contracts.Services.IThemeSelectorService>();
                RDSSH.Helpers.TitleBarHelper.UpdateTitleBar(this, themeSvc.Theme);
            }
            catch { }
            this.Closed += SessionsHostWindow_Closed;
            this.Activated += SessionsHostWindow_Activated;

            // Ensure the title bar element is registered once the visual tree is loaded
            try
            {
                if (this.Content is FrameworkElement fe)
                {
                    fe.Loaded += (_, __) =>
                    {
                        try
                        {
                            // register overlay as title bar
                            if (fe.FindName("DragOverlay") is UIElement overlay)
                            {
                                App.AppTitlebar = overlay;
                                this.SetTitleBar(overlay);
                            }

                            // hook events to update overlay position (use named handlers so we can unsubscribe)
                            SessionsTabView.SizeChanged += SessionsTabView_SizeChanged;
                            SessionsTabView.LayoutUpdated += SessionsTabView_LayoutUpdated;
                            try
                            {
                                // try attach to WinRT IObservableVector<T>
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
                // detach handlers
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
        }

        private void SessionsTabView_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateDragOverlay();
        private void SessionsTabView_LayoutUpdated(object? sender, object e) => UpdateDragOverlay();
        private void TabItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateDragOverlay();

        private void UpdateDragOverlay()
        {
            try
            {
                // EARLY RETURN: avoid enqueueing work for windows that are already closing
                if (_isClosing) return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        // Re-check closing state inside the callback to avoid race after enqueue
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
                                    // transform header position relative to SessionsTabView
                                    var transform = tvi.TransformToVisual(SessionsTabView);
                                    var pt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                                    double right = pt.X + tvi.ActualWidth;
                                    if (right > maxRight) maxRight = right;
                                }
                                catch { }
                            }
                        }

                        // fallback if no tabs found
                        double left = maxRight > 0 ? Math.Ceiling(maxRight) + 8 : 44;

                        // ensure within bounds
                        if (left < 0) left = 0;
                        if (left > SessionsTabView.ActualWidth - 24) left = Math.Max(0, SessionsTabView.ActualWidth - 24);

                        overlay.Margin = new Thickness(left, 0, 0, 0);
                        overlay.Width = Math.Max(24, SessionsTabView.ActualWidth - left);
                        // register as titlebar in case it changed
                        try { App.AppTitlebar = overlay; this.SetTitleBar(overlay); } catch { }
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        // WinUI Desktop Window object already closed / invalid operation id. Safe to ignore.
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
            // WICHTIG: F�r den Zustand "Passwortabfrage kommt" verwenden wir das klassische Control:
            // MsTscAx.MsTscAx
            var host = new NativeChildHwndHost("MsTscAx.MsTscAx")
            {
                HostWindow = this
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

            // allow double-tap on tab header to toggle dock/undock
            tab.DoubleTapped += Tab_DoubleTapped;
            // pointer handlers to implement drag-to-undock
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
                    const double threshold = 12.0; // pixels
                    if (distSq > threshold * threshold)
                    {
                        // start undock
                        if (s is TabViewItem t)
                        {
                            // remove stored start to avoid repeated undock
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
            };

            return host;
        }

        private void Tab_DoubleTapped(object? sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            try
            {
                if (sender is not TabViewItem tab) return;

                var main = App.SessionsWindow; // do not create new main window here
                if (main != null && ReferenceEquals(this, main))
                {
                    // undock into new window
                    UndockTab(tab);
                }
                else
                {
                    // dock back to main window (create if necessary)
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
            // mark drag started to prevent immediate retargeting when new window activates
            _dragStarted = true;
            _pendingDragTab = tab;
            _pendingDragStart = new Windows.Foundation.Point(0,0);
            try { DispatcherQueue.TryEnqueue(async () => { await Task.Delay(50); _dragStarted = false; }); } catch { }

            try
            {
                if (tab == null) return;
                if (tab.Content is not Grid grid) return;
                if (grid.Children.Count == 0) return;
                if (grid.Children[0] is not NativeChildHwndHost host) return;

                // remove from current window
                SessionsTabView.TabItems.Remove(tab);
                _tabsByChildHwnd.Remove(host.ChildHwnd.ToInt64());

                // create new window and move host
                var wnd = new SessionsHostWindow();
                // ensure new window uses current app theme
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

                // detach host from old parent and attach to new container
                grid.Children.Remove(host);
                var newContainer = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                newContainer.Children.Add(host);

                host.HostWindow = wnd;
                host.ReparentTo(WinRT.Interop.WindowNative.GetWindowHandle(wnd));

                var newTab = new TabViewItem { Header = tab.Header, Content = newContainer };
                newTab.DoubleTapped += wnd.Tab_DoubleTapped;
                wnd.SessionsTabView.TabItems.Add(newTab);
                wnd.SessionsTabView.SelectedItem = newTab;
                wnd.RegisterExistingTab(newTab, host);

                // if the original window is now empty, close it to avoid orphan empty windows
                try
                {
                    // only auto-close if this is not the app's main sessions window
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

                // remove from source window
                if (ReferenceEquals(this, target)) return;
                SessionsTabView.TabItems.Remove(tab);
                _tabsByChildHwnd.Remove(host.ChildHwnd.ToInt64());

                // move to target
                grid.Children.Remove(host);
                var newContainer = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                newContainer.Children.Add(host);

                host.HostWindow = target;
                host.ReparentTo(WinRT.Interop.WindowNative.GetWindowHandle(target));

                var newTab = new TabViewItem { Header = tab.Header, Content = newContainer };
                newTab.DoubleTapped += target.Tab_DoubleTapped;
                target.SessionsTabView.TabItems.Add(newTab);
                target.SessionsTabView.SelectedItem = newTab;
                target.RegisterExistingTab(newTab, host);
                target.BringToFront();
                // ensure target uses theme
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
                    // if source window is now empty and it's not the app's main sessions window, close it
                    if (!ReferenceEquals(this, App.SessionsWindow) && this.SessionsTabView.TabItems.Count == 0 && !this._isClosing)
                    {
                        this._isClosing = true;
                        try { this.Close(); } catch { }
                    }
                }
                catch { }
            }
        }

        // Register an existing tab that already contains a hosted child hwnd
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
                }
            });
        }

        private void CloseHostWindowIfEmpty()


        {


            try


            {


                if (_isClosing)


                    return;


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
                _tabsByChildHwnd.Remove(host.ChildHwnd.ToInt64());
                host.Dispose();
            }
            else if (tab.Tag is long hwnd)
            {
                _tabsByChildHwnd.Remove(hwnd);
            }

            // Wenn keine Tabs mehr vorhanden sind, Host-Window sauber schließen.
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
