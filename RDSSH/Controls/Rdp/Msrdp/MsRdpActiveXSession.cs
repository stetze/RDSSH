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
            string? password,
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

            bool hasPassword = !string.IsNullOrEmpty(password);

            // Basis
            Set(_ax, "Server", host);
            Set(_ax, "UserName", username);
            if (!string.IsNullOrWhiteSpace(domain))
                TrySet(_ax, "Domain", domain);

            // Desktop size: muss VOR Connect gesetzt werden
            TrySet(_ax, "DesktopWidth", desktopWidth);
            TrySet(_ax, "DesktopHeight", desktopHeight);
            TrySet(_ax, "FullScreen", fullScreen);

            // ========== SETTINGS: Advanced / Secured ==========
            object? adv = GetBestAdvancedSettings(_ax);

            if (adv != null)
            {
                TrySet(adv, "RDPPort", port);
                TrySet(adv, "RedirectClipboard", redirectClipboard);
                TrySet(adv, "EnableCredSspSupport", enableCredSsp);

                if (ignoreCert)
                    TrySet(adv, "AuthenticationLevel", 0);

                // Resize-Verhalten:
                TrySet(adv, "SmartSizing", true);
                TrySet(adv, "EnableDynamicResolution", true);

                // Hotkeys/WinKey-Unterstützung (Teil 1)
                TrySet(adv, "AcceleratorPassthrough", 1);

                // Oft hilfreich: Fokus übernehmen
                TrySet(adv, "GrabFocusOnConnect", true);
                // Wichtig für WIN-Taste / WIN+R usw.
                TrySet(adv, "EnableWindowsKey", 1);
            }

            object? sec = GetBestSecuredSettings(_ax);
            if (sec != null)
            {
                // KeyboardHookMode:
                // 0 = immer lokal (Hotkeys bleiben am Client)
                // 1 = Hotkeys immer an Remote weiterleiten (auch Windowed)
                // 2 = Hotkeys nur im Fullscreen an Remote weiterleiten
                TrySet(sec, "KeyboardHookMode", 1);
            }

            // Prompt-Logik:
            // - Wenn ein Passwort vorhanden ist: standardmäßig keinen Prompt (Auto-Login)
            // - Wenn kein Passwort vorhanden ist: promptForCreds entscheidet
            //
            // Hinweis: promptForCreds bleibt als "override" sinnvoll:
            //          Falls du trotz Passwort eine Interaktion willst, setze promptForCreds=true
            bool effectivePrompt =
                hasPassword ? promptForCreds /* override */ : promptForCreds;

            // In der Praxis: bei Passwort typischerweise promptForCreds=false übergeben.
            // Wir erzwingen Auto-Login, wenn Passwort da ist UND promptForCreds==false.
            TrySet(_ax, "PromptForCredentials", effectivePrompt);

            // Wenn Passwort vorhanden und KEIN Prompt gewünscht -> NonScriptable setzen
            if (hasPassword && !effectivePrompt)
            {
                // NUR NonScriptable (best practice)
                TrySetNonScriptablePassword(_ax, password!);
            }

            Call(_ax, "Connect");

            // Passwort im Control möglichst sofort wieder verwerfen
            if (hasPassword)
            {
                TryResetNonScriptablePassword(_ax);
            }

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

            TrySet(_ax, "DesktopWidth", width);
            TrySet(_ax, "DesktopHeight", height);
        }

        public void Disconnect()
        {
            if (_ax == null) return;

            // defensiv: Passwort immer resetten
            TryResetNonScriptablePassword(_ax);

            try { Call(_ax, "Disconnect"); } catch { }
            _connected = false;

            // nach Disconnect nochmals defensiv
            TryResetNonScriptablePassword(_ax);
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

        private static void TrySetNonScriptablePassword(object ax, string password)
        {
            try
            {
                if (ax is IMsRdpClientNonScriptable3 ns3) { ns3.put_ClearTextPassword(password); return; }
                if (ax is IMsRdpClientNonScriptable2 ns2) { ns2.put_ClearTextPassword(password); return; }
                if (ax is IMsRdpClientNonScriptable ns1) { ns1.put_ClearTextPassword(password); return; }
            }
            catch
            {
                // Kein Logging des Passworts. Optional nur technischen Fehler loggen.
            }
        }

        private static void TryResetNonScriptablePassword(object ax)
        {
            try
            {
                if (ax is IMsRdpClientNonScriptable3 ns3) { ns3.ResetPassword(); return; }
                if (ax is IMsRdpClientNonScriptable2 ns2) { ns2.ResetPassword(); return; }
                if (ax is IMsRdpClientNonScriptable ns1) { ns1.ResetPassword(); return; }
            }
            catch { }
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
