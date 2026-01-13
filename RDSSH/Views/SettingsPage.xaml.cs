using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDSSH.Contracts.Services;
using RDSSH.Helpers; // for GetLocalized()
using RDSSH.Services;
using RDSSH.ViewModels;
using RDSSH.Models;
using System.Globalization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using System.Linq;
using Meziantou.Framework.Win32;

namespace RDSSH.Views;

public sealed partial class SettingsPage : Page
{
    private const string LanguageSettingKey = "LanguageSetting";

    private readonly ILocalizationService _localizationService;
    private readonly IThemeSelectorService _themeSelectorService;

    private readonly CredentialService _credentialService;

    // Track the language that was selected when the page was loaded so we only trigger a restart
    // if the user actually changed the selection before pressing Save.
    private string _initialSelectedLanguage = string.Empty;

    private string _systemLanguageAtLoad = string.Empty; // Track system language at page load

    // Track current applied language tag (e.g. "en-US" or "de-DE") to allow reverting if user cancels
    private string _currentLanguageTag = string.Empty;

    // Suppress handling of selection-changed events while page initializes
    private bool _suppressLanguageSelectionChanged = false;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        _localizationService = App.GetService<ILocalizationService>();
        _themeSelectorService = App.GetService<IThemeSelectorService>();
        _credentialService = App.GetService<CredentialService>();
        this.DataContext = _credentialService;

        InitializeComponent();
        Loaded += SettingsPage_Loaded;

        // Apply localized strings initially
        ApplyLocalizedStrings();

        // Subscribe to language change notifications so UI updates when language is applied elsewhere
        _localizationService.LanguageChanged += LocalizationService_LanguageChanged;
    }

    // -------------------- Benutzerkonten Einstellungen --------------------

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameTextBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Password;
        var domain = DomainTextBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        if (_credentialService.CurrentEditingUser != null)
        {
            var existing = _credentialService.CurrentEditingUser;
            var oldApplicationName = "RDSSH\\" + existing.Username;
            var newApplicationName = "RDSSH\\" + username;

            // Write new credential under unified prefix
            CredentialManager.WriteCredential(newApplicationName, username, password, domain, CredentialPersistence.LocalMachine);

            // Remove old if name changed
            if (!string.Equals(oldApplicationName, newApplicationName, StringComparison.OrdinalIgnoreCase))
            {
                try { CredentialManager.DeleteCredential(oldApplicationName); } catch { }
                // also try legacy key
                try { CredentialManager.DeleteCredential("RDSSH-Launcher\\" + existing.Username); } catch { }
            }

            existing.Username = username;
            existing.Domain = domain;

            var index = _credentialService.CredentialDataSet.IndexOf(existing);
            if (index >= 0)
            {
                _credentialService.CredentialDataSet[index] = existing;
            }

            _credentialService.CurrentEditingUser = null;
        }
        else
        {
            // Write new credential under unified prefix
            CredentialManager.WriteCredential("RDSSH\\" + username, username, password, domain, CredentialPersistence.LocalMachine);

            _credentialService.CredentialDataSet.Add(new CredentialModel
            {
                ID = Guid.NewGuid(),
                Username = username,
                Domain = domain
            });
        }

        ClearUserDetails();
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is Guid id)
        {
            var user = _credentialService.CredentialDataSet.FirstOrDefault(u => u.ID == id);
            if (user == null) return;

            UsernameTextBox.Text = user.Username;
            DomainTextBox.Text = user.Domain;

            // Try unified prefix first, fallback to legacy
            var credential = CredentialManager.ReadCredential("RDSSH\\" + user.Username) ?? CredentialManager.ReadCredential("RDSSH-Launcher\\" + user.Username);
            PasswordBox.Password = credential?.Password ?? string.Empty;

            _credentialService.CurrentEditingUser = user;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is Guid id)
        {
            var user = _credentialService.CredentialDataSet.FirstOrDefault(u => u.ID == id);
            if (user == null) return;

            try { CredentialManager.DeleteCredential("RDSSH\\" + user.Username); } catch { }
            try { CredentialManager.DeleteCredential("RDSSH-Launcher\\" + user.Username); } catch { }

            _credentialService.CredentialDataSet.Remove(user);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => ClearUserDetails();

    private void ClearUserDetails()
    {
        UsernameTextBox.Text = string.Empty;
        PasswordBox.Password = string.Empty;
        DomainTextBox.Text = string.Empty;
        _credentialService.CurrentEditingUser = null;
    }

    private void LocalizationService_LanguageChanged(object? sender, System.EventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => ApplyLocalizedStrings());
    }

    private void ApplyLocalizedStrings()
    {
        // Localize top language combobox items
        try
        {
            if (SettingsLanguageComboBox != null)
            {
                foreach (var obj in SettingsLanguageComboBox.Items)
                {
                    if (obj is ComboBoxItem item)
                    {
                        var tag = item.Tag as string ?? string.Empty;
                        switch (tag)
                        {
                            case "":
                            {
                                var sysText = "Settings_Language_SystemDefault.Content".GetLocalized();
                                item.Content = sysText == "Settings_Language_SystemDefault.Content" ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "Systemstandard" : "System default") : sysText;
                                break;
                            }
                            case "en-US":
                            {
                                var enText = "Settings_Language_English.Content".GetLocalized();
                                item.Content = enText == "Settings_Language_English.Content" ? "English (United States)" : enText;
                                break;
                            }
                            case "de-DE":
                            {
                                var deText = "Settings_Language_German.Content".GetLocalized();
                                item.Content = deText == "Settings_Language_German.Content" ? "Deutsch (Deutschland)" : deText;
                                break;
                            }
                            default:
                                item.Content = tag;
                                break;
                        }
                    }
                }
            }
        }
        catch { }

        // Set About header to app name from resources
        try
        {
            SettingsAboutHeaderText.Text = "AppDisplayName".GetLocalized();
        }
        catch { }

        // Localize repo command text if present
        try
        {
            if (Settings_RepoCommand != null)
            {
                // keep existing text or localized resource
                Settings_RepoCommand.Text = Settings_RepoCommand.Text ?? "git clone https://github.com/stetze/RDSSH.git";
            }
        }
        catch { }
    }
    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _suppressLanguageSelectionChanged = true;

        try
        {
            // Determine system fallback tag used for initial selection
            _systemLanguageAtLoad = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de-DE" : "en-US";

            // Initialize language selection in the top combobox
            string selectedLang = string.Empty;

            if (SettingsLanguageComboBox != null)
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(LanguageSettingKey, out var langObj) && langObj is string langStr && !string.IsNullOrWhiteSpace(langStr))
                {
                    var found = false;
                    foreach (var obj in SettingsLanguageComboBox.Items)
                    {
                        if (obj is ComboBoxItem item)
                        {
                            var tag = item.Tag as string ?? string.Empty;
                            if (string.Equals(tag, langStr, StringComparison.OrdinalIgnoreCase))
                            {
                                SettingsLanguageComboBox.SelectedItem = item;
                                found = true;
                                break;
                            }
                        }
                    }

                    if (!found)
                    {
                        // fall back to system language if available
                        foreach (var obj in SettingsLanguageComboBox.Items)
                        {
                            if (obj is ComboBoxItem item)
                            {
                                var tag = item.Tag as string ?? string.Empty;
                                if (string.Equals(tag, _systemLanguageAtLoad, StringComparison.OrdinalIgnoreCase))
                                {
                                    SettingsLanguageComboBox.SelectedItem = item;
                                    found = true;
                                    break;
                                }
                            }
                        }

                        if (!found && SettingsLanguageComboBox.Items.Count > 0)
                        {
                            SettingsLanguageComboBox.SelectedIndex = 0;
                        }
                    }

                    selectedLang = langStr;
                }
                else
                {
                    var found = false;
                    foreach (var obj in SettingsLanguageComboBox.Items)
                    {
                        if (obj is ComboBoxItem item)
                        {
                            var tag = item.Tag as string ?? string.Empty;
                            if (string.Equals(tag, _systemLanguageAtLoad, StringComparison.OrdinalIgnoreCase))
                            {
                                SettingsLanguageComboBox.SelectedItem = item;
                                found = true;
                                break;
                            }
                        }
                    }

                    if (!found && SettingsLanguageComboBox.Items.Count > 0)
                    {
                        SettingsLanguageComboBox.SelectedIndex = 0;
                    }

                    selectedLang = string.Empty;
                }
            }

            // Initialize theme ComboBox selection to reflect current ViewModel.ElementTheme
            try
            {
                if (SettingsThemeComboBox != null)
                {
                    var themeName = ViewModel?.ElementTheme.ToString() ?? ElementTheme.Default.ToString();
                    foreach (var obj in SettingsThemeComboBox.Items)
                    {
                        if (obj is ComboBoxItem item)
                        {
                            var tag = item.Tag as string ?? string.Empty;
                            if (string.Equals(tag, themeName, StringComparison.OrdinalIgnoreCase))
                            {
                                SettingsThemeComboBox.SelectedItem = item;
                                break;
                            }
                        }
                    }

                    // If nothing selected, pick first
                    if (SettingsThemeComboBox.SelectedItem == null && SettingsThemeComboBox.Items.Count > 0)
                    {
                        SettingsThemeComboBox.SelectedIndex = 0;
                    }
                }
            }
            catch { }

            // Save the initial selection so we can detect real user changes on Save
            _initialSelectedLanguage = selectedLang;
            _currentLanguageTag = selectedLang;
        }
        finally
        {
            // Re-enable handlers
            _suppressLanguageSelectionChanged = false;
        }
    }

    // Handler: immediate apply when top card language ComboBox changes
    private async void SettingsLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageSelectionChanged) return;

        if (!(sender is ComboBox cb && cb.SelectedItem is ComboBoxItem selected && selected.Tag is string tag)) return;

        // If nothing changed, ignore
        if (string.Equals(tag, _currentLanguageTag, StringComparison.OrdinalIgnoreCase)) return;

        // Single dialog: ask to apply language and offer restart now or later
        var dialog = new ContentDialog
        {
            Title = "Settings_LanguageChange_Title".GetLocalized(),
            Content = "Settings_LanguageChange_Content".GetLocalized(),
            PrimaryButtonText = "Settings_LanguageChange_Restart".GetLocalized(), // Restart now
            CloseButtonText = "Settings_LanguageChange_Later".GetLocalized(),    // Apply later / later
            XamlRoot = this.XamlRoot,
            RequestedTheme = _themeSelectorService?.Theme ?? ElementTheme.Default
        };

        var result = await dialog.ShowAsync();

        // If user cancelled the dialog (closed without choosing Restart or Later) -> revert
        if (result != ContentDialogResult.Primary && result != ContentDialogResult.None)
        {
            // Unexpected value; revert to previous
            try
            {
                _suppressLanguageSelectionChanged = true;
                foreach (var obj in cb.Items)
                {
                    if (obj is ComboBoxItem item)
                    {
                        var t = item.Tag as string ?? string.Empty;
                        if (string.Equals(t, _currentLanguageTag, StringComparison.OrdinalIgnoreCase))
                        {
                            cb.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _suppressLanguageSelectionChanged = false;
            }

            return;
        }

        // User confirmed (either Restart (Primary) or Later (CloseButton) ) -> apply language
        try
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                ApplicationData.Current.LocalSettings.Values.Remove(LanguageSettingKey);
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values[LanguageSettingKey] = tag;
            }

            _localizationService.ApplyLanguage(tag);
            _currentLanguageTag = tag;

            // If user selected Restart now
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exe,
                            UseShellExecute = true
                        });
                    }
                }
                catch { }

                Environment.Exit(0);
            }
        }
        catch
        {
            // ignore
        }
    }

    // New handler: when user selects an item in the card's ComboBox, execute the ViewModel SwitchThemeCommand
    private void SettingsThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem selected && selected.Tag is string tag)
        {
            if (Enum.TryParse(typeof(ElementTheme), tag, out var parsed))
            {
                var theme = (ElementTheme)parsed;
                // Use command if available
                if (ViewModel?.SwitchThemeCommand != null && ViewModel.SwitchThemeCommand.CanExecute(theme))
                {
                    ViewModel.SwitchThemeCommand.Execute(theme);
                }
                else
                {
                    // Fallback: call theme service directly
                    _ = _themeSelectorService?.SetThemeAsync(theme);
                }
            }
        }
    }

    private async void OnCardClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            // Prefer the localized TextBlock content if available
            var text = Settings_RepoCommand?.Text ?? "git clone https://github.com/stetze/RDS-Shadow.git";
            var dataPackage = new DataPackage();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();

            // Optionally provide lightweight feedback by briefly changing the header or similar (omitted)
        }
        catch
        {
            // ignore clipboard failures
        }
    }
}
