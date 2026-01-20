using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RDSSH.Controls.Rdp.Msrdp
{
    public sealed class MsRdpActiveXSession : IDisposable
    {
        private object? _ax;
        private bool _connected;

        private static bool _atlInitialized;

        public void Initialize(IntPtr atlAxWinHwnd)
        {
            if (atlAxWinHwnd == IntPtr.Zero)
                throw new ArgumentException("atlAxWinHwnd is null", nameof(atlAxWinHwnd));

            EnsureAtl();

            int hr = AtlAxGetControl(atlAxWinHwnd, out object control);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            _ax = control;
            Debug.WriteLine($"[MsRdpActiveXSession] ActiveX resolved. Type={_ax.GetType().FullName}");
        }

        public void Connect(
            string host,
            int port,
            string username,
            string? domain,
            string? password,           // aktuell prompt mode ok
            int desktopWidth,
            int desktopHeight,
            bool fullScreen = false,
            bool redirectClipboard = true,
            bool promptForCreds = true,
            bool enableCredSsp = true,
            bool ignoreCert = true)
        {
            if (_ax == null) throw new InvalidOperationException("Initialize() first.");
            if (_connected) return;

            host = (host ?? "").Trim();
            username = (username ?? "").Trim();
            domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();

            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("host empty", nameof(host));

            // Basis
            Set(_ax, "Server", host);
            Set(_ax, "UserName", username);
            if (!string.IsNullOrWhiteSpace(domain))
                TrySet(_ax, "Domain", domain);

            // Desktop size: muss VOR Connect gesetzt werden
            TrySet(_ax, "DesktopWidth", desktopWidth);
            TrySet(_ax, "DesktopHeight", desktopHeight);
            TrySet(_ax, "FullScreen", fullScreen);

            // PromptForCredentials (falls vorhanden)
            TrySet(_ax, "PromptForCredentials", promptForCreds);

            // ========== SETTINGS: Advanced / Secured ==========
            // AdvancedSettings (höchste verfügbare Version)
            object? adv = GetBestAdvancedSettings(_ax);

            if (adv != null)
            {
                TrySet(adv, "RDPPort", port);
                TrySet(adv, "RedirectClipboard", redirectClipboard);
                TrySet(adv, "EnableCredSspSupport", enableCredSsp);

                if (ignoreCert)
                    TrySet(adv, "AuthenticationLevel", 0);

                // Resize-Verhalten:
                // 1) SmartSizing: skaliert Inhalt sauber auf Viewport (wie 1Remote „Scale“)
                TrySet(adv, "SmartSizing", true);

                // 2) EnableDynamicResolution: echte dynamische Auflösung (wenn Server/Client es unterstützt)
                // Hinweis: Property existiert nicht in allen Versionen -> TrySet ist ok
                TrySet(adv, "EnableDynamicResolution", true);

                // Hotkeys/WinKey-Unterstützung (Teil 1)
                // AcceleratorPassthrough sitzt häufig in AdvancedSettings2+
                TrySet(adv, "AcceleratorPassthrough", 1);

                // Oft hilfreich: Fokus übernehmen
                TrySet(adv, "GrabFocusOnConnect", true);
            }

            // KeyboardHookMode sitzt in SecuredSettings2 (nicht zuverlässig in AdvancedSettings)
            object? sec = GetBestSecuredSettings(_ax);
            if (sec != null)
            {
                // 0=local, 1=remote nur full screen, 2=remote immer
                TrySet(sec, "KeyboardHookMode", 2);
            }

            // Passwort bewusst NICHT setzen -> Prompt
            Call(_ax, "Connect");

            _connected = true;
            Debug.WriteLine("[MsRdpActiveXSession] Connect() called.");
        }

        /// <summary>
        /// Resize-Update: bevorzugt UpdateSessionDisplaySettings (Dynamic Resolution),
        /// Fallback: DesktopWidth/DesktopHeight.
        /// WICHTIG: Bei COM NICHT per GetMethod suchen, sondern per InvokeMember/IDispatch aufrufen.
        /// </summary>
        public void UpdateDisplay(int width, int height)
        {
            if (_ax == null) return;
            if (!_connected) return;
            if (width < 1 || height < 1) return;

            try
            {
                // Versuch 1: echte dynamische Auflösung
                // UpdateSessionDisplaySettings(uint DesktopWidth, uint DesktopHeight,
                //   uint PhysicalWidth, uint PhysicalHeight, uint Orientation,
                //   uint DesktopScaleFactor, uint DeviceScaleFactor)
                Call(_ax, "UpdateSessionDisplaySettings",
                    (uint)width,
                    (uint)height,
                    (uint)width,
                    (uint)height,
                    (uint)0,
                    (uint)100,
                    (uint)100);

                Debug.WriteLine($"[MsRdpActiveXSession] UpdateSessionDisplaySettings({width}x{height})");
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MsRdpActiveXSession] UpdateSessionDisplaySettings failed: {ex.Message}");
            }

            // Fallback: versuchen, DesktopWidth/Height anzupassen
            TrySet(_ax, "DesktopWidth", width);
            TrySet(_ax, "DesktopHeight", height);
        }

        public void Disconnect()
        {
            if (_ax == null) return;
            try { Call(_ax, "Disconnect"); } catch { }
            _connected = false;
        }

        public void Dispose()
        {
            try { Disconnect(); } catch { }

            if (_ax != null && Marshal.IsComObject(_ax))
            {
                try { Marshal.FinalReleaseComObject(_ax); } catch { }
            }

            _ax = null;
        }

        private static object? GetBestAdvancedSettings(object ax)
        {
            object? adv = null;
            foreach (var name in new[]
            {
                "AdvancedSettings9","AdvancedSettings8","AdvancedSettings7","AdvancedSettings6",
                "AdvancedSettings5","AdvancedSettings4","AdvancedSettings3","AdvancedSettings2","AdvancedSettings"
            })
            {
                if (TryGet(ax, name, out adv) && adv != null) return adv;
            }
            return null;
        }

        private static object? GetBestSecuredSettings(object ax)
        {
            object? sec = null;
            foreach (var name in new[]
            {
                "SecuredSettings3","SecuredSettings2","SecuredSettings"
            })
            {
                if (TryGet(ax, name, out sec) && sec != null) return sec;
            }
            return null;
        }

        private static void Set(object target, string name, object? value)
        {
            target.GetType().InvokeMember(
                name,
                BindingFlags.SetProperty,
                binder: null,
                target: target,
                args: new object?[] { value });
        }

        private static bool TrySet(object target, string name, object? value)
        {
            try { Set(target, name, value); return true; }
            catch (TargetInvocationException tie) when (tie.InnerException is COMException ce && ce.HResult == unchecked((int)0x80020006)) { return false; }
            catch (COMException ce) when (ce.HResult == unchecked((int)0x80020006)) { return false; }
            catch { return false; }
        }

        private static bool TryGet(object target, string name, out object? value)
        {
            try
            {
                value = target.GetType().InvokeMember(
                    name,
                    BindingFlags.GetProperty,
                    binder: null,
                    target: target,
                    args: Array.Empty<object?>());
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static void Call(object target, string methodName, params object?[] args)
        {
            target.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod,
                binder: null,
                target: target,
                args: args);
        }

        private static void EnsureAtl()
        {
            if (_atlInitialized) return;
            _atlInitialized = AtlAxWinInit();
            if (!_atlInitialized)
                throw new InvalidOperationException("AtlAxWinInit failed.");
        }

        [DllImport("atl.dll", CharSet = CharSet.Unicode)]
        private static extern bool AtlAxWinInit();

        [DllImport("atl.dll", CharSet = CharSet.Unicode)]
        private static extern int AtlAxGetControl(
            IntPtr hWnd,
            [MarshalAs(UnmanagedType.IUnknown)] out object pp);
    }
}
