using ColorCode.Compilation.Languages;
using CommunityToolkit.WinUI.UI.Controls;
using Meziantou.Framework.Win32; // Importiere Meziantou.Framework.Win32
using Microsoft.Data.SqlClient;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using RDSSH.Contracts.Services;
using RDSSH.Helpers; // for GetLocalized()
using RDSSH.Models;
using RDSSH.Services;
using RDSSH.ViewModels;
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
        var val = key.GetLocalized();
        if (val == key)
        {
            // Prefer ApplicationLanguages.PrimaryLanguageOverride when set
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
        try { tbSearch.PlaceholderText = "Sessions_FilterTextBox.PlaceholderText".GetLocalized(); } catch { }

        // Ensure the list is populated automatically when the page is first shown
        Loaded += SessionsPage_Loaded;
    }

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
    {
        // Logik zum Bearbeiten der Verbindung und Navigieren zur AddConnectionPage
        Frame.Navigate(typeof(AddConnectionPage), connection);
    }

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
        var listView = sender as ListView;
        HostlistModel item = null;

        try
        {
            Debug.WriteLine($"DoubleTapped OriginalSource type: {e.OriginalSource?.GetType().FullName}");

            // Prefer SelectedItem (covers keyboard and mouse selection scenarios)
            item = listView?.SelectedItem as HostlistModel;
            Debug.WriteLine($"SelectedItem resolved: {(item != null ? item.DisplayName : "<null>")}");

            // Fallback: walk up the visual tree from OriginalSource to find the ListViewItem
            if (item == null)
            {
                var dep = e.OriginalSource as DependencyObject;
                while (dep != null && !(dep is ListViewItem))
                {
                    dep = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(dep);
                }

                if (dep is ListViewItem lvi)
                {
                    item = lvi.Content as HostlistModel;
                    Debug.WriteLine($"Resolved via ListViewItem: {(item != null ? item.DisplayName : "<null>")}");
                }
                else
                {
                    // Last resort: try DataContext of the OriginalSource if it's a FrameworkElement
                    if (e.OriginalSource is FrameworkElement fe)
                    {
                        item = fe.DataContext as HostlistModel;
                        Debug.WriteLine($"Resolved via OriginalSource.DataContext: {(item != null ? item.DisplayName : "<null>")}");
                    }
                }
            }

            if (item != null)
            {
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
            else
            {
                Debug.WriteLine("ConnectionsListView_DoubleTapped: could not resolve HostlistModel for clicked row.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ConnectionsListView_DoubleTapped error: {ex.Message}");
        }
    }

    private void StartSSHConnection(HostlistModel connection)
    {
        try
        {
            connection.IsConnected = true; // Setze den Status auf verbunden

            string serverAddress = connection.Hostname;
            string username = connection.Username;
            string port = connection.Port;

            // Baue den SSH-Befehl zusammen
            string sshCommand = $"ssh {username}@{serverAddress} -p {port}";

            var startInfo = new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = sshCommand,
                UseShellExecute = true
            };

            Process.Start(startInfo);

            connection.IsConnected = false; // Setze den Status auf nicht verbunden, wenn die Verbindung geschlossen ist
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in StartSSHConnection: {ex.Message}");
            throw;
        }
    }

    private async void StartRDPConnection(HostlistModel connection)
    {
        try
        {
            connection.IsConnected = true; // Setze den Status auf verbunden

            string serverAddress = connection.Hostname;
            string username = connection.Username;
            string domain = connection.Domain; // Füge die Domain aus der Hostlist hinzu

            // Lese die Anmeldedaten aus dem Credential Manager mit dem Präfix "RDSSH-Launcher\\"
            var credential = CredentialManager.ReadCredential("RDSSH\\" + username);

            // Erstelle einen SecureString für das Passwort
            SecureString securePassword = new SecureString();
            foreach (char c in credential.Password)
            {
                securePassword.AppendChar(c);
            }
            securePassword.MakeReadOnly();

            // Speichere die Anmeldedaten temporär als Generic Credential
            string tempCredentialName = $"TERMSRV/{serverAddress}.{domain}";
            CredentialManager.WriteCredential(tempCredentialName, $"{domain}\\{username}", credential.Password, domain, CredentialPersistence.Session, CredentialType.Generic);

            // Verwende sdl-freerdp direkt mit ProcessStartInfo
            string freerdpPath = Path.Combine(AppContext.BaseDirectory, "FreeRDP", "sdl3-freerdp.exe");

            if (File.Exists(freerdpPath))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = freerdpPath,
                    Arguments = "/args-from:stdin",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var process = new Process
                {
                    StartInfo = startInfo
                };

                process.OutputDataReceived += (sender, args) => Debug.WriteLine(args.Data);
                process.ErrorDataReceived += (sender, args) => Debug.WriteLine(args.Data);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Schreibe die Argumente in stdin
                using (var writer = process.StandardInput)
                {
                    if (writer.BaseStream.CanWrite)
                    {
                        writer.WriteLine($"/v:{serverAddress}.{domain}");
                        writer.WriteLine($"/u:{username}");
                        writer.WriteLine($"/d:{domain}");
                        writer.WriteLine("/cert:ignore");
                        writer.WriteLine("/dynamic-resolution");
                        writer.WriteLine("/clipboard");
                        writer.WriteLine($"/p:{new NetworkCredential(string.Empty, securePassword).Password}");
                    }
                }
                // Debugging-Ausgabe des Window Handles
                Debug.WriteLine($"Window Handle: {process.MainWindowHandle}");

                await process.WaitForExitAsync();

                // Lösche die temporären Anmeldedaten nach der Verwendung
                CredentialManager.DeleteCredential(tempCredentialName);

                connection.IsConnected = false; // Setze den Status auf nicht verbunden, wenn die Verbindung geschlossen ist
            }
            else
            {
                Debug.WriteLine($"Die Datei {freerdpPath} wurde nicht gefunden.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in StartRDPConnection: {ex.Message}");
            throw;
        }
    }



    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        // Basic handler: determine sort key from Button.Tag and log it.
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
    {
        _localizationService.LanguageChanged += LocalizationService_LanguageChanged;
    }

    private void LocalizationService_LanguageChanged(object? sender, System.EventArgs e)
    {
        Debug.WriteLine("SessionsPage: LanguageChanged received, re-localizing UI");

        // Re-apply localized strings when language changes
        void UpdateTexts()
        {
            // Update programmatically set values
            
            try { tbSearch.PlaceholderText = "Sessions_FilterTextBox.PlaceholderText".GetLocalized(); } catch { }
        }

        // Ensure update occurs on UI thread
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => UpdateTexts());
    }

    private ElementTheme GetCurrentAppTheme()
    {
        try
        {
            if (App.MainWindow?.Content is FrameworkElement root)
            {
                return root.ActualTheme;
            }
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
        // Apply current filter (ListView is already bound to HostlistService.Hostlist)
        ApplyFilter();

        // Unregister handler to avoid repeated loading
        Loaded -= SessionsPage_Loaded;
    }

    public class MyDataClass : INotifyPropertyChanged
    {
        private string _username = string.Empty;
        private string _poolName = string.Empty;
        private string _serverName = string.Empty;
        private int _sessionId;
        private string _clientName = string.Empty;

        public string Username { get => _username; set { _username = value; OnPropertyChanged(); } }
        public string PoolName { get => _poolName; set { _poolName = value; OnPropertyChanged(); } }
        public string ServerName { get => _serverName; set { _serverName = value; OnPropertyChanged(); } }
        public int SessionId { get => _sessionId; set { _sessionId = value; OnPropertyChanged(); } }

        // New: client name retrieved via WTS from the terminal server
        public string ClientName { get => _clientName; set { _clientName = value; OnPropertyChanged(); } }

        public MyDataClass(string userName, string poolName, string serverName, int sessionId)
        {
            Username = userName ?? string.Empty;
            PoolName = poolName ?? string.Empty;
            ServerName = serverName ?? string.Empty;
            SessionId = sessionId;
            ClientName = string.Empty;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<MyDataClass> MyData = new ObservableCollection<MyDataClass>();

    // Semaphore to serialize ContentDialog.ShowAsync calls to avoid the "Only a single ContentDialog can be open at any time" COMException
    private readonly SemaphoreSlim _dialogSemaphore = new SemaphoreSlim(1, 1);

    private async Task<ContentDialogResult> ShowContentDialogSerializedAsync(ContentDialog dialog)
    {
        await _dialogSemaphore.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        catch (COMException)
        {
            // If another dialog is shown concurrently, swallow or log as needed.
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogSemaphore.Release();
        }
    }

    // --- WTS P/Invoke declarations for client lookup ---
    private enum WTS_INFO_CLASS
    {
        WTSClientName = 10,
        WTSClientAddress = 14
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr WTSOpenServer(string pServerName);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSCloseServer(IntPtr hServer);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    // Decode pointer buffer robustly: try UTF-16 (Unicode), then ANSI (1252), then UTF-8
    private static string PtrToStringSmart(IntPtr pBuffer, int bytes)
    {
        if (pBuffer == IntPtr.Zero || bytes <= 0)
            return string.Empty;

        // Try Unicode (UTF-16LE)
        try
        {
            var s = Marshal.PtrToStringUni(pBuffer);
            if (!string.IsNullOrWhiteSpace(s))
            {
                // check for likely valid characters
                var total = s.Length;
                var good = s.Count(c => (c >= 0x20 && c <= 0x7E) || char.IsLetterOrDigit(c) || char.IsWhiteSpace(c));
                if (total > 0 && ((double)good / total) > 0.2)
                {
                    return s.Trim('\0', ' ');
                }
            }
        }
        catch { }

        // Fallback: copy raw bytes and try ANSI (code page 1252)
        try
        {
            var buffer = new byte[bytes];
            Marshal.Copy(pBuffer, buffer, 0, bytes);

            // Trim trailing zero bytes
            var actualLength = buffer.Length;
            while (actualLength > 0 && buffer[actualLength - 1] == 0) actualLength--;
            if (actualLength <= 0) return string.Empty;

            var ansi = Encoding.GetEncoding(1252).GetString(buffer, 0, actualLength);
            if (!string.IsNullOrWhiteSpace(ansi))
            {
                var total = ansi.Length;
                var good = ansi.Count(c => (c >= 0x20 && c <= 0x7E) || char.IsLetterOrDigit(c) || char.IsWhiteSpace(c));
                if (total > 0 && ((double)good / total) > 0.2)
                {
                    return ansi.Trim('\0', ' ');
                }
            }

            // Last try UTF8
            try
            {
                var utf8 = Encoding.UTF8.GetString(buffer, 0, actualLength);
                if (!string.IsNullOrWhiteSpace(utf8)) return utf8.Trim('\0', ' ');
            }
            catch { }
        }
        catch { }

        return string.Empty;
    }

    private static string GetClientNameForSession(string server, int sessionId)
    {
        if (string.IsNullOrWhiteSpace(server)) return string.Empty;
        IntPtr hServer = IntPtr.Zero;
        try
        {
            hServer = WTSOpenServer(server);
            if (hServer == IntPtr.Zero) return string.Empty;

            if (WTSQuerySessionInformation(hServer, sessionId, WTS_INFO_CLASS.WTSClientName, out var pBuffer, out var bytes) && pBuffer != IntPtr.Zero)
            {
                try
                {
                    var clientName = PtrToStringSmart(pBuffer, bytes);
                    return clientName;
                }
                finally
                {
                    WTSFreeMemory(pBuffer);
                }
            }
        }
        catch
        {
            // ignore errors and return empty
        }
        finally
        {
            if (hServer != IntPtr.Zero)
            {
                WTSCloseServer(hServer);
            }
        }

        return string.Empty;
    }

    private static async Task<string> GetClientNameForSessionAsync(string server, int sessionId, int timeoutMs = 2000)
    {
        var t = Task.Run(() => GetClientNameForSession(server, sessionId));
        if (await Task.WhenAny(t, Task.Delay(timeoutMs)) == t)
        {
            return t.Result;
        }
        return string.Empty;
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
        sortButton.Content = "A-Z";
        ApplySorting();
        Debug.WriteLine("Sorted A-Z");
    }

    private void SortZA_Click(object sender, RoutedEventArgs e)
    {
        currentSortMode = "za";
        sortButton.Content = "Z-A";
        ApplySorting();
        Debug.WriteLine("Sorted Z-A");
    }

    private void SortRecent_Click(object sender, RoutedEventArgs e)
    {
        currentSortMode = "recent";
        sortButton.Content = "Recent";
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
                listViewToggle.IsChecked = true;
                gridViewToggle.IsChecked = false;
                
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
                listViewToggle.IsChecked = false;
                gridViewToggle.IsChecked = true;
                
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
            Debug.WriteLine($"ItemClick resolved: {item.DisplayName} protocol={item.Protocol}");
            if (string.Equals(item.Protocol, "RDP", StringComparison.OrdinalIgnoreCase))
            {
                StartRDPConnection(item);
            }
            else if (string.Equals(item.Protocol, "SSH", StringComparison.OrdinalIgnoreCase))
            {
                StartSSHConnection(item);
            }
        }
    }
}
