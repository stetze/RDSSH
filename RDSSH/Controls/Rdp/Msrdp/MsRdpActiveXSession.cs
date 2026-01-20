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

        /// <summary>
        /// Minimaler Connect: setzt Server/User und ruft Connect().
        /// Passwort wird NICHT gesetzt -> Windows-Passwortdialog erscheint.
        /// </summary>
        public void Connect(
            string host,
            int port,
            string username,
            string? domain,
            string? password,   // wird absichtlich ignoriert für Prompt-Mode
            int desktopWidth,
            int desktopHeight,
            bool fullScreen = false,
            bool redirectClipboard = true,
            bool promptForCreds = true,   // für den Zustand "Prompt kommt" = true
            bool enableCredSsp = true,
            bool ignoreCert = true)
        {
            if (_ax == null) throw new InvalidOperationException("Initialize() first.");
            if (_connected) return;

            host = (host ?? "").Trim();
            username = (username ?? "").Trim();

            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("host empty", nameof(host));

            // MsTscAx Basis
            Set(_ax, "Server", host);
            Set(_ax, "UserName", username);

            // AdvancedSettings (MsTscAx hat meist AdvancedSettings / AdvancedSettings2+ je nach Version)
            object? adv = null;
            foreach (var name in new[]
            {
                "AdvancedSettings9","AdvancedSettings8","AdvancedSettings7","AdvancedSettings6",
                "AdvancedSettings5","AdvancedSettings4","AdvancedSettings3","AdvancedSettings2","AdvancedSettings"
            })
            {
                if (TryGet(_ax, name, out adv) && adv != null) break;
                adv = null;
            }

            if (adv != null)
            {
                // Port (Property heißt i.d.R. RDPPort)
                TrySet(adv, "RDPPort", port);

                // Clipboard
                TrySet(adv, "RedirectClipboard", redirectClipboard);

                // CredSSP
                TrySet(adv, "EnableCredSspSupport", enableCredSsp);

                // Zertifikatslevel (falls vorhanden; nicht überall)
                if (ignoreCert)
                    TrySet(adv, "AuthenticationLevel", 0);
            }

            // PromptForCredentials gibt es bei manchen Interfaces; wenn nicht, wird es ignoriert.
            TrySet(_ax, "PromptForCredentials", promptForCreds);

            // Wichtig: KEIN Passwort setzen -> Prompt erscheint
            Call(_ax, "Connect");

            _connected = true;
            Debug.WriteLine("[MsRdpActiveXSession] Connect() called (prompt mode).");
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
