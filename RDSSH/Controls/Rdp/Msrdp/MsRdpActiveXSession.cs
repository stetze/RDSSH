using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace RDSSH.Controls.Rdp.Msrdp
{
    public sealed class MsRdpActiveXSession : IDisposable
    {
        private readonly SynchronizationContext? _ownerContext = SynchronizationContext.Current;
        private static readonly Guid IMsTscAxEvents_Iid = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
        private const int DISPID_OnDisconnected = 4;

        private Action<int>? _onDisconnectedHandler;
        private bool _eventsHooked;
        private object? _ax;
        private bool _connected;
        public bool IsConnected => _connected;
        private static bool _atlInitialized;

        public event EventHandler<int>? Disconnected;

        public void Initialize(IntPtr atlAxWinHwnd)
        {
            if (atlAxWinHwnd == IntPtr.Zero)
                throw new ArgumentException("atlAxWinHwnd is null", nameof(atlAxWinHwnd));

            EnsureAtl();
            int hr = AtlAxGetControl(atlAxWinHwnd, out object control);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            _ax = control;
            Debug.WriteLine($"[MsRdpActiveXSession] ActiveX resolved. Type={_ax.GetType().FullName}");
            HookComEvents();
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
            bool ignoreCert = true,
            string? loadBalanceInfo = null)
        {
            if (_ax == null) throw new InvalidOperationException("Initialize() first.");
            if (_connected) return;

            host = (host ?? string.Empty).Trim();
            username = (username ?? string.Empty).Trim();
            domain = string.IsNullOrWhiteSpace(domain) ? null : domain!.Trim();
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("host empty", nameof(host));

            bool hasPassword = !string.IsNullOrEmpty(password);

            try
            {
                TrySet(_ax, "Server", host);
                Set(_ax, "UserName", username);
                if (!string.IsNullOrWhiteSpace(domain))
                    TrySet(_ax, "Domain", domain);

                TrySet(_ax, "DesktopWidth", desktopWidth);
                TrySet(_ax, "DesktopHeight", desktopHeight);
                TrySet(_ax, "FullScreen", fullScreen);

                object? adv = GetBestAdvancedSettings(_ax);
                if (adv != null)
                {
                    TrySet(adv, "RDPPort", port);
                    TrySet(adv, "RedirectClipboard", redirectClipboard);
                    TrySet(adv, "EnableCredSspSupport", enableCredSsp);

                    if (!string.IsNullOrWhiteSpace(loadBalanceInfo))
                    {
                        var packed = BuildBrokerLoadBalanceInfoBstr(loadBalanceInfo);
                        if (!string.IsNullOrEmpty(packed))
                        {
                            TrySet(adv, "LoadBalanceInfo", packed);
                            Debug.WriteLine("[MsRdpActiveXSession] LoadBalanceInfo (tsv packed) applied.");
                        }
                    }

                    if (ignoreCert) TrySet(adv, "AuthenticationLevel", 0);
                    TrySet(adv, "SmartSizing", true);
                    TrySet(adv, "EnableDynamicResolution", true);
                    TrySet(adv, "AcceleratorPassthrough", 1);
                    TrySet(adv, "GrabFocusOnConnect", true);
                    TrySet(adv, "EnableWindowsKey", 1);
                }

                object? sec = GetBestSecuredSettings(_ax);
                if (sec != null)
                {
                    TrySet(sec, "KeyboardHookMode", 1);
                }

                bool effectivePrompt = promptForCreds;
                TrySet(_ax, "PromptForCredentials", effectivePrompt);

                if (hasPassword && !effectivePrompt)
                {
                    TrySetNonScriptablePassword(_ax, password!);
                }

                Call(_ax, "Connect");

                if (hasPassword) { TryResetNonScriptablePassword(_ax); }

                _connected = true;
                Debug.WriteLine("[MsRdpActiveXSession] Connect() called.");
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"[MsRdpActiveXSession] COMException in Connect: 0x{ex.HResult:X8} {ex}");
                try { Call(_ax, "Disconnect"); } catch { }
                _connected = false;
            }
            catch (TargetInvocationException tie)
            {
                Debug.WriteLine($"[MsRdpActiveXSession] TargetInvocationException in Connect: {tie.InnerException?.Message ?? tie.Message}");
                try { Call(_ax, "Disconnect"); } catch { }
                _connected = false;
            }
        }

        public void UpdateDisplay(int width, int height)
        {
            if (_ownerContext is not null && SynchronizationContext.Current != _ownerContext)
            {
                RunOnOwnerThread(() => UpdateDisplay(width, height), wait: false);
                return;
            }
            if (_ax == null) return;
            if (!_connected) return;
            if (width < 1 || height < 1) return;

            try
            {
                Call(_ax, "UpdateSessionDisplaySettings",
                    (uint)width, (uint)height, (uint)width, (uint)height,
                    (uint)0, (uint)100, (uint)100);

                Debug.WriteLine($"[MsRdpActiveXSession] UpdateSessionDisplaySettings({width}x{height})");
                return;
            }
            catch (TargetInvocationException tie)
            {
                Debug.WriteLine($"[MsRdpActiveXSession] UpdateSessionDisplaySettings failed: {tie.InnerException?.Message ?? tie.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MsRdpActiveXSession] UpdateSessionDisplaySettings failed: {ex.Message}");
            }

            try
            {
                TrySet(_ax, "DesktopWidth", width);
                TrySet(_ax, "DesktopHeight", height);
            }
            catch { }
        }

        public void Disconnect()
        {
            if (_ownerContext is not null && SynchronizationContext.Current != _ownerContext)
            {
                RunOnOwnerThread(Disconnect, wait: true);
                return;
            }
            if (_ax == null) return;

            TryResetNonScriptablePassword(_ax);
            try { Call(_ax, "Disconnect"); } catch { }
            _connected = false;
            TryResetNonScriptablePassword(_ax);
        }

        public void Dispose()
        {
            if (_ownerContext is not null && SynchronizationContext.Current != _ownerContext)
            {
                RunOnOwnerThread(Dispose, wait: true);
                return;
            }

            UnhookComEvents();
            try { Disconnect(); } catch { }

            if (_ax != null && Marshal.IsComObject(_ax))
            {
                try { Marshal.FinalReleaseComObject(_ax); } catch { }
            }
            _ax = null;
        }

        private static string? BuildBrokerLoadBalanceInfoBstr(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var s = raw.Trim();

            const string rdpPrefix = "loadbalanceinfo:s:";
            if (s.StartsWith(rdpPrefix, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(rdpPrefix.Length).Trim();

            if (s.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase) &&
                s.IndexOf("MS Terminal Services Plugin", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var after = s.Substring("cookie:".Length).Trim();
                s = "tsv://" + after;
                Debug.WriteLine("[MsRdpActiveXSession] LoadBalanceInfo: converted cookie->tsv: " + s);
            }

            if (!s.StartsWith("tsv://", StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine("[MsRdpActiveXSession] LoadBalanceInfo: non-TSV value passed through as-is.");
                return s;
            }

            if ((s.Length % 2) == 1) s += " ";
            s += "\r\n";

            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            if ((bytes.Length % 2) == 1)
            {
                var tmp = new byte[bytes.Length + 1];
                System.Buffer.BlockCopy(bytes, 0, tmp, 0, bytes.Length);
                tmp[tmp.Length - 1] = (byte)' ';
                bytes = tmp;
            }

            var packed = System.Text.Encoding.Unicode.GetString(bytes);
            Debug.WriteLine("[MsRdpActiveXSession] LoadBalanceInfo (tsv packed) length=" + packed.Length);
            return packed;
        }

        private void HookComEvents()
        {
            if (_eventsHooked || _ax is null) return;

            try
            {
                _onDisconnectedHandler = reason =>
                {
                    // Wichtig: KEINE Exceptions nach außen. Sonst COM -> TargetInvocationException und dein Cleanup läuft nicht.
                    try
                    {
                        RunOnOwnerThread(() =>
                        {
                            _connected = false;
                            try { Debug.WriteLine($"[MsRdpActiveXSession] OnDisconnected({reason})"); } catch { }

                            try
                            {
                                Disconnected?.Invoke(this, reason);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[MsRdpActiveXSession] Disconnected subscriber threw: {ex}");
                            }
                        }, wait: false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MsRdpActiveXSession] OnDisconnected internal error: {ex}");
                    }
                };

                ComEventsHelper.Combine(_ax, IMsTscAxEvents_Iid, DISPID_OnDisconnected, _onDisconnectedHandler);
                _eventsHooked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MsRdpActiveXSession] HookComEvents failed: {ex.Message}");
            }
        }

        private void UnhookComEvents()
        {
            if (!_eventsHooked || _ax is null || _onDisconnectedHandler is null) return;

            try { ComEventsHelper.Remove(_ax, IMsTscAxEvents_Iid, DISPID_OnDisconnected, _onDisconnectedHandler); }
            catch { }
            finally
            {
                _eventsHooked = false;
                _onDisconnectedHandler = null;
            }
        }

        private void RunOnOwnerThread(Action action, bool wait)
        {
            if (_ownerContext is null) { action(); return; }
            if (SynchronizationContext.Current == _ownerContext) { action(); return; }

            if (!wait)
            {
                _ownerContext.Post(_ => action(), null);
                return;
            }

            Exception? captured = null;
            using var mre = new ManualResetEventSlim(false);
            _ownerContext.Post(_ =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
                finally { mre.Set(); }
            }, null);
            mre.Wait();

            if (captured is not null) throw captured;
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
            foreach (var name in new[] { "SecuredSettings3", "SecuredSettings2", "SecuredSettings" })
            {
                if (TryGet(ax, name, out sec) && sec != null) return sec;
            }
            return null;
        }

        private static void TrySetNonScriptablePassword(object ax, string password)
        {
            try
            {
                if (ax is IMsRdpClientNonScriptable3 ns3) { ns3.put_ClearTextPassword(password); return; }
                if (ax is IMsRdpClientNonScriptable2 ns2) { ns2.put_ClearTextPassword(password); return; }
                if (ax is IMsRdpClientNonScriptable ns1) { ns1.put_ClearTextPassword(password); return; }
            }
            catch { }
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
