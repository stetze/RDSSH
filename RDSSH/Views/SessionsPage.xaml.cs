using CommunityToolkit.WinUI;
using Meziantou.Framework.Win32; // Importiere Meziantou.Framework.Win32
using Microsoft.Data.SqlClient;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using RDSSH.Contracts.Services;
using RDSSH.Controls;
using RDSSH.Helpers; // for GetLocalized()
using RDSSH.Models;
using RDSSH.Services;
using RDSSH.ViewModels;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using CTStrings = CommunityToolkit.WinUI.StringExtensions;

namespace RDSSH.Views;

public sealed partial class SessionsPage : Page
{
    public SessionsViewModel ViewModel { get; }
    private readonly ILocalizationService _localizationService;
    private FrameworkElement? _lastRightClickTarget;
    private readonly HostlistService _hostlistService; // resolve service

    // Keep these if needed later (not currently required)
    // private readonly HostlistViewModel _viewModel;
    // private ObservableCollection<HostlistModel> _allConnections;
    // private ObservableCollection<HostlistModel> _filteredConnections;
    private string _currentSortColumn;
    private bool _isAscending;

    // Class-level helper to provide localization with fallback
    private static string LocalizedOrDefault(string key, string deDefault, string enDefault)
    {
        var val = CTStrings.GetLocalized(key);
        if (val == key)
        {
            try
            {
                var overrideLang = ApplicationLanguages.PrimaryLanguageOverride;
                if (!string.IsNullOrEmpty(overrideLang))
                {
                    return overrideLang.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? deDefault : enDefault;
                }
            }
            catch
            {
                // ignore
            }

            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? deDefault : enDefault;
        }
        return val;
    }


    public SessionsPage()
    {
        ViewModel = App.GetService<SessionsViewModel>();
        _localizationService = App.GetService<ILocalizationService>();
        _hostlistService = App.GetService<HostlistService>(); // resolve service

        InitializeComponent();

        // Note: We don't bind directly here anymore, ApplyFilter will handle it
        // This prevents the binding from being overwritten

        // Wire localization change
        _localization_service_subscribe();

        // Set UI texts (use localization with fallback)
        try { tbSearch.PlaceholderText = CTStrings.GetLocalized("Sessions_FilterTextBox.PlaceholderText"); } catch { }


        // Ensure the list is populated automatically when the page is first shown
        Loaded += SessionsPage_Loaded;
    }

    private sealed class ActiveRdpSession
    {
        public nint Handle;
        public CancellationTokenSource Cts = new();
        public Task? Worker;
    }

    private readonly Dictionary<HostlistModel, ActiveRdpSession> _activeRdp = new();

    private void DevicesAddButton_Click(object sender, RoutedEventArgs e)
    {
        // Use the page's Frame when available; otherwise fall back to the application's navigation frame
        var frame = this.Frame ?? App.GetService<INavigationService>().Frame;
        if (frame != null)
        {
            frame.Navigate(typeof(AddConnectionPage));
        }
        else
        {
            Debug.WriteLine("SessionsPage: No Frame available for navigation to AddConnectionPage");
        }
    }

    // Added missing event handlers referenced from XAML to fix build errors
    private void ConnectionsListView_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        try
        {
            var gridView = sender as GridView ?? ConnectionsListView;

            // Find the GridViewItem that was right-clicked
            DependencyObject dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is GridViewItem))
            {
                dep = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(dep);
            }

            GridViewItem? container = dep as GridViewItem;
            HostlistModel? item = null;

            if (container != null)
            {
                item = container.Content as HostlistModel;
            }
            else if (e.OriginalSource is FrameworkElement fe)
            {
                // fallback: DataContext of original source
                item = fe.DataContext as HostlistModel;
            }

            Debug.WriteLine($"RightTapped: resolved item={(item != null ? item.DisplayName : "<null>")}");

            if (item != null)
            {
                var menuFlyout = new MenuFlyout();

                var editItem = new MenuFlyoutItem { Text = "Bearbeiten" };
                editItem.Click += (s, args) => EditConnection(item);
                menuFlyout.Items.Add(editItem);

                var deleteItem = new MenuFlyoutItem { Text = "Löschen" };
                deleteItem.Click += (s, args) => DeleteConnection(item);
                menuFlyout.Items.Add(deleteItem);

                // Show the flyout at the container (preferred) or at the GridView
                try
                {
                    if (container != null)
                    {
                        menuFlyout.ShowAt(container, e.GetPosition(container));
                    }
                    else
                    {
                        menuFlyout.ShowAt(gridView, e.GetPosition(gridView));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error showing MenuFlyout: {ex.Message}");
                    // fallback: show at window content if it's a FrameworkElement
                    try
                    {
                        var hostFe = App.MainWindow.Content as FrameworkElement;
                        if (hostFe != null) menuFlyout.ShowAt(hostFe);
                    }
                    catch { }
                }

                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ConnectionsListView_RightTapped error: {ex.Message}");
        }
    }

    private void EditConnection(HostlistModel connection)
        => Frame.Navigate(typeof(AddConnectionPage), connection);

    private async void DeleteConnection(HostlistModel connection)
    {
        if (connection != null)
        {
            try
            {
                _hostlistService.RemoveConnection(connection);
                await _hostlistService.SaveConnectionsAsync();
                Debug.WriteLine("Connection deleted successfully.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting connection: {ex.Message}");
            }
        }
    }

    private void ConnectionsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        HostlistModel? item = null;

        try
        {
            Debug.WriteLine($"DoubleTapped OriginalSource type: {e.OriginalSource?.GetType().FullName}");

            // 1) Prefer SelectedItem (works for both ListView and GridView)
            if (sender is ListView lv)
            {
                item = lv.SelectedItem as HostlistModel;
            }
            else if (sender is GridView gv)
            {
                item = gv.SelectedItem as HostlistModel;
            }

            Debug.WriteLine($"SelectedItem resolved: {(item != null ? item.DisplayName : "<null>")}");

            // 2) Fallback: walk up the visual tree to find the item container
            if (item == null)
            {
                var dep = e.OriginalSource as DependencyObject;

                // First try ListViewItem
                var probe = dep;
                while (probe != null && probe is not ListViewItem)
                {
                    probe = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(probe);
                }
                if (probe is ListViewItem lvi)
                {
                    item = lvi.Content as HostlistModel;
                    Debug.WriteLine($"Resolved via ListViewItem: {(item != null ? item.DisplayName : "<null>")}");
                }

                // Then try GridViewItem
                if (item == null)
                {
                    probe = dep;
                    while (probe != null && probe is not GridViewItem)
                    {
                        probe = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(probe);
                    }
                    if (probe is GridViewItem gvi)
                    {
                        item = gvi.Content as HostlistModel;
                        Debug.WriteLine($"Resolved via GridViewItem: {(item != null ? item.DisplayName : "<null>")}");
                    }
                }

                // 3) Last resort: DataContext of OriginalSource
                if (item == null && e.OriginalSource is FrameworkElement fe)
                {
                    item = fe.DataContext as HostlistModel;
                    Debug.WriteLine($"Resolved via OriginalSource.DataContext: {(item != null ? item.DisplayName : "<null>")}");
                }
            }

            // 4) Guard: abort if still null
            if (item == null)
            {
                Debug.WriteLine("DoubleTapped: item null -> abort");
                return;
            }

            // Execute single-click behavior first (selection/details/etc.)
            OnItemSingleClick(item);

            // Then start the connection only on double-click
            Debug.WriteLine($"Starting connection for: {item.DisplayName} protocol={item.Protocol}");

            if (string.Equals(item.Protocol, "RDP", StringComparison.OrdinalIgnoreCase))
            {
                StartRDPConnection(item);
            }
            else if (string.Equals(item.Protocol, "SSH", StringComparison.OrdinalIgnoreCase))
            {
                StartSSHConnection(item);
            }
            else
            {
                Debug.WriteLine($"Unknown protocol: {item.Protocol}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ConnectionsListView_DoubleTapped error: {ex}");
        }
    }


    // Extracted single-click actions here. This will NOT start connections.
    private void OnItemSingleClick(HostlistModel item)
    {
        if (item == null) return;
        Debug.WriteLine($"Item single-clicked: {item.DisplayName} protocol={item.Protocol}");
    }

    private void StartSSHConnection(HostlistModel connection)
    {
        try
        {
            connection.IsConnected = true;

            string serverAddress = connection.Hostname;
            string username = connection.Username;
            string port = connection.Port;

            string sshCommand = $"ssh {username}@{serverAddress} -p {port}";

            var startInfo = new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = sshCommand,
                UseShellExecute = true
            };

            Process.Start(startInfo);

            connection.IsConnected = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in StartSSHConnection: {ex.Message}");
            throw;
        }
    }

    private static string BuildFreeRdpArgs(HostlistModel c)
    {
        var args = new StringBuilder();

        if (c.RdpDynamicResolution) args.Append("/dynamic-resolution ");
        if (c.RdpClipboard) args.Append("/clipboard ");

        // NICHT default:
        if (c.RdpIgnoreCert) args.Append("/cert:ignore ");
        if (c.RdpTlsLegacy) args.Append("/tls-seclevel:0 ");
        if (c.RdpAdminMode) args.Append("/admin ");

        if (!string.IsNullOrWhiteSpace(c.RdpLoadBalanceInfo))
            args.Append("/load-balance-info:").Append(c.RdpLoadBalanceInfo.Trim()).Append(' ');

        if (!string.IsNullOrWhiteSpace(c.RdpExtraArgs))
            args.Append(c.RdpExtraArgs.Trim()).Append(' ');

        return args.ToString().Trim();
    }

    private async void StartRDPConnection(HostlistModel connection)
    {
        RdpSessionNative.LogRuntimeVersions();

        try
        {
            // Toggle: Wenn schon aktiv -> disconnect
            if (_activeRdp.TryGetValue(connection, out var existing))
            {
                Debug.WriteLine("StartRDPConnection: already connected -> disconnect");
                await StopRdpSessionAsync(connection, existing);
                return;
            }

            connection.IsConnected = true;

            var host = (connection.Hostname ?? "").Trim();
            var username = (connection.Username ?? "").Trim();
            var domain = (connection.Domain ?? "").Trim();

            int port = 3389;
            if (!string.IsNullOrWhiteSpace(connection.Port) &&
                int.TryParse(connection.Port, out var p) &&
                p > 0)
            {
                port = p;
            }

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username))
            {
                Debug.WriteLine("StartRDPConnection: Host oder Username fehlt.");
                connection.IsConnected = false;
                return;
            }

            // Credential Manager
            var credKey = "RDSSH\\" + username;
            var cred = CredentialManager.ReadCredential(credKey);
            if (cred == null)
            {
                Debug.WriteLine($"StartRDPConnection: Credential nicht gefunden: {credKey}");
                connection.IsConnected = false;
                return;
            }

            var freerdpArgs = BuildFreeRdpArgs(connection);
            var dynamicResolution = connection.RdpDynamicResolution ? 1 : 0;

            int width = 1280;
            int height = 720;

            // --- SessionsHostWindow holen + in Vordergrund bringen + Tab erstellen ---
            var sessionsWindow = App.GetOrCreateSessionsWindow();
            sessionsWindow.BringToFront();

            var hostControl = sessionsWindow.AddRdpTab($"RDP: {connection.DisplayName}");

            // WICHTIG: erst warten bis Child-HWND existiert
            var childHwnd = await hostControl.WaitForChildHwndAsync();
            Debug.WriteLine($"StartRDPConnection: got childHwnd=0x{childHwnd:X}");

            // Session erstellen
            var handle = RdpSessionNative.Create();
            if (handle == 0)
            {
                Debug.WriteLine("StartRDPConnection: RdpSessionNative.Create failed");
                connection.IsConnected = false;
                return;
            }

            // Session registrieren (damit Stop/Toggle funktioniert)
            var session = new ActiveRdpSession { Handle = handle };
            _activeRdp[connection] = session;

            // HWND an Native binden (VOR Connect)
            RdpSessionNative.AttachToHwnd(handle, childHwnd);

            // Connect Ergebnis synchronisieren
            var tcsConnect = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            session.Worker = Task.Run(() =>
            {
                int rc = -999;

                try
                {
                    Debug.WriteLine($"RDP worker: connecting host='{host}' port={port} user='{username}' domain='{domain}' args='{freerdpArgs}'");

                    rc = RdpSessionNative.Connect(
                        handle,
                        host,
                        port,
                        username,
                        domain,
                        cred.Password,
                        width,
                        height,
                        dynamicResolution,
                        freerdpArgs
                    );

                    tcsConnect.TrySetResult(rc);

                    if (rc != 0)
                        return;

                    var sw = Stopwatch.StartNew();
                    while (!session.Cts.IsCancellationRequested)
                    {
                        int prc = RdpSessionNative.Pump(handle, 100);

                        if (sw.ElapsedMilliseconds > 2000)
                        {
                            Debug.WriteLine($"RDP worker: pump alive prc={prc}");
                            sw.Restart();
                        }

                        if (prc < 0)
                        {
                            Debug.WriteLine($"RDP worker: pump ended prc={prc} lastErr=0x{RdpSessionNative.GetLastError(handle):X8}");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    tcsConnect.TrySetResult(rc);
                    Debug.WriteLine($"RDP worker exception: {ex}");
                }
                finally
                {
                    try { RdpSessionNative.Disconnect(handle); } catch { }
                    try { RdpSessionNative.Destroy(handle); } catch { }
                }
            });

            int connectRc = await tcsConnect.Task;

            if (connectRc != 0)
            {
                uint lastErr = 0;
                var sb = new StringBuilder(256);

                try
                {
                    lastErr = RdpSessionNative.GetLastError(handle);
                    RdpSessionNative.GetLastErrorString(handle, sb, sb.Capacity);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GetLastError/GetLastErrorString failed: {ex}");
                }

                Debug.WriteLine($"RdpSessionNative.Connect rc={connectRc} lastErr=0x{lastErr:X8} {sb}");

                _activeRdp.Remove(connection);
                connection.IsConnected = false;
                return;
            }

            Debug.WriteLine("RdpSessionNative.Connect rc=0 (Success).");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in StartRDPConnection: {ex}");
            connection.IsConnected = false;
            throw;
        }
    }



    private async Task StopRdpSessionAsync(HostlistModel connection, ActiveRdpSession session)
    {
        try
        {
            session.Cts.Cancel();

            // Optional: kurz warten, damit Worker in finally sauber Disconnect/Destroy macht
            if (session.Worker != null)
            {
                try { await Task.WhenAny(session.Worker, Task.Delay(1000)); } catch { }
            }
        }
        finally
        {
            _activeRdp.Remove(connection);
            connection.IsConnected = false;
        }
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Microsoft.UI.Xaml.Controls.Button b && b.Tag is string tag)
            {
                Debug.WriteLine($"Sort requested on: {tag}");
                // TODO: implement actual sorting logic
            }
        }
        catch { }
    }

    private void _localization_service_subscribe()
        => _localizationService.LanguageChanged += LocalizationService_LanguageChanged;

    private void LocalizationService_LanguageChanged(object? sender, System.EventArgs e)
    {
        Debug.WriteLine("SessionsPage: LanguageChanged received, re-localizing UI");

        void UpdateTexts()
        {
            try { tbSearch.PlaceholderText = CTStrings.GetLocalized("Sessions_FilterTextBox.PlaceholderText"); } catch { }

        }

        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => UpdateTexts());
    }

    private ElementTheme GetCurrentAppTheme()
    {
        try
        {
            if (App.MainWindow?.Content is FrameworkElement root)
                return root.ActualTheme;
        }
        catch { }

        // fallback to theme service
        try
        {
            var svc = App.GetService<IThemeSelectorService>();
            return svc?.Theme ?? ElementTheme.Default;
        }
        catch { }

        return ElementTheme.Default;
    }

    private async void SessionsPage_Loaded(object? sender, RoutedEventArgs e)
    {
        ApplyFilter();

        // Unregister handler to avoid repeated loading
        Loaded -= SessionsPage_Loaded;
    }

    private void tbSearch_TextChanged(Microsoft.UI.Xaml.Controls.AutoSuggestBox sender, Microsoft.UI.Xaml.Controls.AutoSuggestBoxTextChangedEventArgs e)
    {
        try { ApplyFilter(); } catch { }
    }

    private void tbSearch_QuerySubmitted(Microsoft.UI.Xaml.Controls.AutoSuggestBox sender, Microsoft.UI.Xaml.Controls.AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        try { ApplyFilter(); } catch { }
    }

    private string currentFilter = string.Empty;
    private string currentTypeFilter = string.Empty;
    private string currentGroupFilter = string.Empty;
    private string currentSortMode = "az"; // az, za, recent

    private void FilterAll_Click(object sender, RoutedEventArgs e)
    {
        // Reset all filters
        currentFilter = string.Empty;
        currentTypeFilter = string.Empty;
        currentGroupFilter = string.Empty;

        if (tbSearch != null) tbSearch.Text = string.Empty;
        if (filterTypeComboBox != null) filterTypeComboBox.SelectedIndex = 0;
        if (filterGroupComboBox != null) filterGroupComboBox.SelectedIndex = 0;

        ApplyFilter();
        Debug.WriteLine("All filters reset");
    }

    private void FilterType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (filterTypeComboBox == null) return;

        if (filterTypeComboBox.SelectedIndex == 0)
        {
            currentTypeFilter = string.Empty;
        }
        else if (filterTypeComboBox.SelectedItem is ComboBoxItem item)
        {
            currentTypeFilter = item.Content?.ToString() ?? string.Empty;
        }

        ApplyFilter();
        Debug.WriteLine($"Type filter: {currentTypeFilter}");
    }

    private void FilterGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (filterGroupComboBox == null) return;

        if (filterGroupComboBox.SelectedIndex == 0)
        {
            currentGroupFilter = string.Empty;
        }
        else if (filterGroupComboBox.SelectedItem is ComboBoxItem item)
        {
            currentGroupFilter = item.Content?.ToString() ?? string.Empty;
        }

        ApplyFilter();
        Debug.WriteLine($"Group filter: {currentGroupFilter}");
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        // Flyout wird automatisch geöffnet
    }

    private void SortAZ_Click(object sender, RoutedEventArgs e)
    {
        currentSortMode = "az";
        if (sortButton != null) sortButton.Content = "A-Z";
        ApplySorting();
        Debug.WriteLine("Sorted A-Z");
    }

    private void SortZA_Click(object sender, RoutedEventArgs e)
    {
        currentSortMode = "za";
        if (sortButton != null) sortButton.Content = "Z-A";
        ApplySorting();
        Debug.WriteLine("Sorted Z-A");
    }

    private void SortRecent_Click(object sender, RoutedEventArgs e)
    {
        currentSortMode = "recent";
        if (sortButton != null) sortButton.Content = "Recent";
        ApplySorting();
        Debug.WriteLine("Sorted by Recent");
    }

    private void ApplySorting()
    {
        var hostlist = _hostlistService?.Hostlist;
        if (hostlist == null || hostlist.Count == 0)
        {
            Debug.WriteLine("ApplySorting: No items to sort");
            return;
        }

        // Create a sorted list without modifying the original
        var sortedList = currentSortMode switch
        {
            "za" => hostlist.OrderByDescending(x => x.DisplayName).ToList(),
            "recent" => hostlist.ToList(), // Keep current order as "recent" - can be implemented later with a LastUsed property
            _ => hostlist.OrderBy(x => x.DisplayName).ToList() // "az" default
        };

        // Clear and repopulate the collection
        _hostlistService.Hostlist.Clear();
        foreach (var item in sortedList)
        {
            _hostlistService.Hostlist.Add(item);
        }

        Debug.WriteLine($"ApplySorting: Sorted {sortedList.Count} items by {currentSortMode}");

        // Reapply filters
        ApplyFilter();
    }

    private void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggleButton && toggleButton.Tag is string tag)
        {
            if (tag == "list")
            {
                // Switch to List View
                if (listViewToggle != null) listViewToggle.IsChecked = true;
                if (gridViewToggle != null) gridViewToggle.IsChecked = false;

                // Hide GridView, Show ListView
                if (GridViewScrollViewer != null) GridViewScrollViewer.Visibility = Visibility.Collapsed;
                if (ConnectionsListViewList != null) ConnectionsListViewList.Visibility = Visibility.Visible;

                // Sync data
                SyncItemSources();

                Debug.WriteLine("Switched to List View");
            }
            else if (tag == "grid")
            {
                // Switch to Grid View
                if (listViewToggle != null) listViewToggle.IsChecked = false;
                if (gridViewToggle != null) gridViewToggle.IsChecked = true;

                // Show GridView, Hide ListView
                if (GridViewScrollViewer != null) GridViewScrollViewer.Visibility = Visibility.Visible;
                if (ConnectionsListViewList != null) ConnectionsListViewList.Visibility = Visibility.Collapsed;

                // Sync data
                SyncItemSources();

                Debug.WriteLine("Switched to Grid View");
            }
        }
    }

    private void SyncItemSources()
    {
        // Get current ItemsSource from the visible control
        var currentSource = GridViewScrollViewer?.Visibility == Visibility.Visible
            ? ConnectionsListView?.ItemsSource
            : ConnectionsListViewList?.ItemsSource;

        // Apply to both controls
        if (currentSource != null)
        {
            if (ConnectionsListView != null) ConnectionsListView.ItemsSource = currentSource;
            if (ConnectionsListViewList != null) ConnectionsListViewList.ItemsSource = currentSource;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyFilter(); // Apply the current filter after refreshing the list
    }

    private void ApplyFilter()
    {
        // Early exit if controls are not initialized yet
        if ((ConnectionsListView == null && ConnectionsListViewList == null) || _hostlistService?.Hostlist == null)
        {
            Debug.WriteLine("ApplyFilter: Controls not initialized yet");
            return;
        }

        currentFilter = tbSearch?.Text ?? string.Empty;

        var hostlist = _hostlistService.Hostlist;

        // Start with all items
        IEnumerable<HostlistModel> filteredData = hostlist;

        // Apply search filter
        if (!string.IsNullOrEmpty(currentFilter))
        {
            filteredData = filteredData.Where(item =>
                (item.DisplayName?.IndexOf(currentFilter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (item.Hostname?.IndexOf(currentFilter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (item.Username?.IndexOf(currentFilter, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        // Apply type filter (RDP/SSH)
        if (!string.IsNullOrEmpty(currentTypeFilter) && currentTypeFilter != "Type")
        {
            filteredData = filteredData.Where(item =>
                item.Protocol?.Equals(currentTypeFilter, StringComparison.OrdinalIgnoreCase) == true);
        }

        // Apply group filter - Note: Group property doesn't exist yet in HostlistModel
        // Can be implemented later when Group property is added to the model
        // if (!string.IsNullOrEmpty(currentGroupFilter) && currentGroupFilter != "Group")
        // {
        //     filteredData = filteredData.Where(item =>
        //         !string.IsNullOrEmpty(item.Group) &&
        //         item.Group.Equals(currentGroupFilter, StringComparison.OrdinalIgnoreCase));
        // }

        // Update the ItemsSource for both views
        try
        {
            var dataSource = new ObservableCollection<HostlistModel>(filteredData);

            if (ConnectionsListView != null)
                ConnectionsListView.ItemsSource = dataSource;

            if (ConnectionsListViewList != null)
                ConnectionsListViewList.ItemsSource = dataSource;

            Debug.WriteLine($"ApplyFilter: Updated ItemsSource with {filteredData.Count()} items");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error updating ItemsSources: {ex.Message}");
        }
    }

    private void ConnectionsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HostlistModel item)
        {
            // Do not start a connection on single click. Execute only the single-click behavior.
            OnItemSingleClick(item);
        }
    }
    private static Task EnqueueAsync(Microsoft.UI.Dispatching.DispatcherQueue queue, Action action)
    {
        var tcs = new TaskCompletionSource();

        queue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

}
