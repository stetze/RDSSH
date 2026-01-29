using Microsoft.UI.Dispatching;
using RDSSH.Controls.Rdp.Msrdp;
using RDSSH.Models;
using RDSSH.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace RDSSH.Services
{
    public sealed class ConnectionLauncherService
    {
        private sealed class ActiveRdpSession
        {
            public MsRdpActiveXSession Ax = default!;
            public IntPtr ChildHwnd;
            public HostlistModel Connection = default!;
        }

        private readonly HostlistService _hostlistService;
        private readonly CredentialService _credentialService;

        private readonly Dictionary<Guid, ActiveRdpSession> _activeRdpById = new();

        public ConnectionLauncherService(HostlistService hostlistService, CredentialService credentialService)
        {
            _hostlistService = hostlistService;
            _credentialService = credentialService;
        }

        public async Task StartRdpAsync(HostlistModel connection)
        {
            if (connection == null) return;

            if (connection.ConnectionId != Guid.Empty &&
                _activeRdpById.TryGetValue(connection.ConnectionId, out var existing))
            {
                Debug.WriteLine("[ConnectionLauncherService] RDP already active -> focus tab");
                FocusExistingRdpTab(existing);
                return;
            }

            await StartRdpInternalAsync(connection);
        }

        public async Task StartRdpAsync(Guid connectionId)
        {
            if (connectionId == Guid.Empty) return;

            if (_hostlistService.Hostlist.Count == 0)
            {
                try { await _hostlistService.LoadConnectionsAsync(); } catch { }
            }

            var connection = _hostlistService.Hostlist.FirstOrDefault(c => c.ConnectionId == connectionId);
            if (connection == null)
            {
                Debug.WriteLine($"[ConnectionLauncherService] ConnectionId not found: {connectionId}");
                return;
            }

            await StartRdpAsync(connection);
        }

        private void FocusExistingRdpTab(ActiveRdpSession session)
        {
            try
            {
                var sessionsWindow = App.GetOrCreateSessionsWindow();
                sessionsWindow.BringToFront();
                sessionsWindow.FocusTabByChildHwnd(session.ChildHwnd);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConnectionLauncherService] FocusExistingRdpTab failed: {ex}");
            }
        }

        private async Task StartRdpInternalAsync(HostlistModel connection)
        {
            try
            {
                connection.IsConnected = true;

                var host = (connection.Hostname ?? string.Empty).Trim(); // Broker-FQDN
                var userFromConnection = (connection.Username ?? string.Empty).Trim();
                var domainFromConnection = (connection.Domain ?? string.Empty).Trim();

                int port = 3389;
                if (!string.IsNullOrWhiteSpace(connection.Port) &&
                    int.TryParse(connection.Port, out var p) && p > 0)
                {
                    port = p;
                }

                if (string.IsNullOrWhiteSpace(host))
                {
                    Debug.WriteLine("[ConnectionLauncherService] RDP host missing.");
                    connection.IsConnected = false;
                    return;
                }

                if (connection.CredentialId == null || connection.CredentialId == Guid.Empty)
                {
                    Debug.WriteLine("[ConnectionLauncherService] Connection has no CredentialId.");
                    connection.IsConnected = false;
                    return;
                }

                var vaultCred = _credentialService.ReadVaultCredential(connection.CredentialId.Value);
                if (vaultCred == null)
                {
                    Debug.WriteLine($"[ConnectionLauncherService] Vault credential not found: {connection.CredentialId}");
                    connection.IsConnected = false;
                    return;
                }

                var vaultUserRaw = !string.IsNullOrWhiteSpace(vaultCred.UserName) ? vaultCred.UserName : userFromConnection;
                var vaultDomainRaw = !string.IsNullOrWhiteSpace(vaultCred.Comment) ? vaultCred.Comment : domainFromConnection;
                SplitUserAndDomain(vaultUserRaw, vaultDomainRaw, out var connectUser, out var connectDomain);

                string pwd = vaultCred.Password;
                bool hasPassword = !string.IsNullOrEmpty(pwd);

                var sessionsWindow = App.GetOrCreateSessionsWindow();
                sessionsWindow.BringToFront();

                var tabTitle =
                    !string.IsNullOrWhiteSpace(connection.DisplayName) ? connection.DisplayName :
                    !string.IsNullOrWhiteSpace(connection.Hostname) ? connection.Hostname :
                    "RDP";

                var hostControl = sessionsWindow.AddRdpTab(tabTitle);
                var childHwnd = await hostControl.WaitForChildHwndAsync();

                var scale = hostControl.XamlRoot?.RasterizationScale ?? 1.0;
                int width = (int)Math.Max(1, Math.Round(hostControl.ActualWidth * scale));
                int height = (int)Math.Max(1, Math.Round(hostControl.ActualHeight * scale));
                if (width <= 1 || height <= 1) { width = 1280; height = 720; }

                var dispatcher = sessionsWindow.DispatcherQueue;

                // --- ActiveX auf UI-Thread erzeugen (STA) ---
                MsRdpActiveXSession ax = null!;
                await EnqueueAsync(dispatcher, () =>
                {
                    ax = new MsRdpActiveXSession();
                    ax.Initialize(childHwnd);

                    ax.Disconnected += (_, __) =>
                    {
                        _ = dispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                if (connection.ConnectionId != Guid.Empty &&
                                    _activeRdpById.TryGetValue(connection.ConnectionId, out var active) &&
                                    active.ChildHwnd == childHwnd)
                                {
                                    _activeRdpById.Remove(connection.ConnectionId);
                                }

                                SessionsHostWindow.Current?.CloseTabByChildHwnd(childHwnd);
                                try { ax?.Dispose(); } catch { }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ConnectionLauncherService] Auto-close tab failed: {ex}");
                            }
                            finally
                            {
                                connection.IsConnected = false;
                            }
                        });
                    };
                });

                var session = new ActiveRdpSession
                {
                    Ax = ax,
                    ChildHwnd = childHwnd,
                    Connection = connection
                };

                if (connection.ConnectionId != Guid.Empty)
                    _activeRdpById[connection.ConnectionId] = session;

                // Resize nur solange verbunden
                hostControl.BoundsUpdated += (_, __) =>
                {
                    try
                    {
                        var scale2 = hostControl.XamlRoot?.RasterizationScale ?? 1.0;
                        var w = (int)Math.Max(1, Math.Round(hostControl.ActualWidth * scale2));
                        var h = (int)Math.Max(1, Math.Round(hostControl.ActualHeight * scale2));
                        if (session.Ax != null && session.Ax.IsConnected)
                            session.Ax.UpdateDisplay(w, h);
                    }
                    catch { }
                };

                // Connect (UI-Thread)
                await EnqueueAsync(dispatcher, () =>
                {
                    session.Ax.Connect(
                        host: host,
                        port: port,
                        username: connectUser,
                        domain: connectDomain,
                        password: hasPassword ? pwd : null,
                        desktopWidth: width,
                        desktopHeight: height,
                        promptForCreds: !hasPassword,
                        redirectClipboard: connection.RdpClipboard,
                        enableCredSsp: true,
                        ignoreCert: connection.RdpIgnoreCert,
                        loadBalanceInfo: connection.RdpLoadBalanceInfo
                    );
                });

                // Passwort leeren
                pwd = string.Empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConnectionLauncherService] Error in StartRdpInternalAsync: {ex}");
                connection.IsConnected = false;

                if (connection.ConnectionId != Guid.Empty &&
                    _activeRdpById.TryGetValue(connection.ConnectionId, out var s))
                {
                    try { s.Ax?.Dispose(); } catch { }
                    _activeRdpById.Remove(connection.ConnectionId);
                }

                throw;
            }
        }

        private static void SplitUserAndDomain(string inputUser, string? inputDomain, out string user, out string? domain)
        {
            user = (inputUser ?? string.Empty).Trim();
            domain = string.IsNullOrWhiteSpace(inputDomain) ? null : inputDomain.Trim();

            var bs = user.IndexOf('\\');
            if (bs > 0 && bs < user.Length - 1)
            {
                var d = user.Substring(0, bs);
                var u = user.Substring(bs + 1);
                if (string.IsNullOrWhiteSpace(domain)) domain = d;
                user = u;
                return;
            }

            var at = user.IndexOf('@');
            if (at > 0 && at < user.Length - 1)
            {
                var u = user.Substring(0, at);
                var d = user.Substring(at + 1);
                if (string.IsNullOrWhiteSpace(domain)) domain = d;
                user = u;
            }
        }

        private static Task EnqueueAsync(DispatcherQueue queue, Action action)
        {
            var tcs = new TaskCompletionSource();
            queue.TryEnqueue(() =>
            {
                try { action(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
    }
}