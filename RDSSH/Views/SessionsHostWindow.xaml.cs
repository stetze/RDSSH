using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDSSH.Controls;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace RDSSH.Views
{
    public sealed partial class SessionsHostWindow : Window
    {
        public SessionsHostWindow()
        {
            InitializeComponent();
        }

        public NativeChildHwndHost AddRdpTab(string title)
        {
            var host = new NativeChildHwndHost
            {
                HostWindow = this,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            // Container erzwingt korrektes Measure/Arrange
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
                Debug.WriteLine($"[SessionsHostWindow] Child HWND created: 0x{hwnd:X}");

            return host;
        }

        // Foreground / Focus helper
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId(); // WICHTIG: kernel32, nicht user32
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

        private void SessionsTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is TabViewItem tvi)
            {
                // Content ist Grid -> Host ist Child[0]
                try
                {
                    if (tvi.Content is Grid g && g.Children.Count > 0 && g.Children[0] is IDisposable d1)
                        d1.Dispose();
                    else if (tvi.Content is IDisposable d2)
                        d2.Dispose();
                }
                catch { }

                sender.TabItems.Remove(tvi);
            }
        }
    }
}
