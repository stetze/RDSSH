using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDSSH.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace RDSSH.Views
{
    public sealed partial class SessionsHostWindow : Window
    {
        public static SessionsHostWindow? Current { get; private set; }

        private readonly Dictionary<long, TabViewItem> _tabsByChildHwnd = new();
        private bool _isClosing;

        public SessionsHostWindow()
        {
            Current = this;
            InitializeComponent();
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
            this.Closed += (_, __) => _isClosing = true;
        }

        public NativeChildHwndHost AddRdpTab(string title)
        {
            // WICHTIG: F¸r den Zustand "Passwortabfrage kommt" verwenden wir das klassische Control:
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

            host.ChildHwndCreated += (_, hwnd) =>
            {
                Debug.WriteLine($"[SessionsHostWindow] Child HWND created: 0x{hwnd:X}");
                var key = hwnd.ToInt64();
                tab.Tag = key;
                _tabsByChildHwnd[key] = tab;
            };

            return host;
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

            // Wenn keine Tabs mehr vorhanden sind, Host-Window sauber schlieﬂen.
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
