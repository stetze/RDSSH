using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RDSSH.Models;
using RDSSH.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RDSSH.Views
{
    public sealed partial class AddConnectionPage : Page
    {
        public CredentialService CredentialService { get; }

        private readonly HostlistService _hostlistService;
        private HostlistModel _connectionToEdit;

        public AddConnectionPage()
        {
            InitializeComponent();
            CredentialService = App.GetService<CredentialService>();
            _hostlistService = App.GetService<HostlistService>();
            DataContext = this;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _connectionToEdit = e.Parameter as HostlistModel;

            if (_connectionToEdit != null)
            {
                ThemeComboBox.SelectedItem = ThemeComboBox.Items
                    .Cast<ComboBoxItem>()
                    .FirstOrDefault(item => item.Content?.ToString() == _connectionToEdit.Protocol);

                PortTextBox.Text = _connectionToEdit.Port;
                DisplaynameTextBox.Text = _connectionToEdit.DisplayName;
                HostnameTextBox.Text = _connectionToEdit.Hostname;
                DomainTextBox.Text = _connectionToEdit.Domain;

                // >>> FIX: Credential-Auswahl primär über CredentialId (sonst kollidieren gleiche Usernames)
                if (_connectionToEdit.CredentialId != null && _connectionToEdit.CredentialId != Guid.Empty)
                {
                    CredentialComboBox.SelectedItem = CredentialService.CredentialDataSet
                        .FirstOrDefault(cred => cred.ID == _connectionToEdit.CredentialId.Value);
                }
                else
                {
                    // Legacy-Fallback: alte Connections hatten ggf. nur Username
                    CredentialComboBox.SelectedItem = CredentialService.CredentialDataSet
                        .FirstOrDefault(cred => cred.Username == _connectionToEdit.Username);
                }

                // --- Load advanced values ---
                IgnoreCertCheckBox.IsChecked = _connectionToEdit.RdpIgnoreCert;
                TlsLegacyCheckBox.IsChecked = _connectionToEdit.RdpTlsLegacy;
                DynamicResolutionCheckBox.IsChecked = _connectionToEdit.RdpDynamicResolution;
                ClipboardCheckBox.IsChecked = _connectionToEdit.RdpClipboard;
                AdminModeCheckBox.IsChecked = _connectionToEdit.RdpAdminMode;
                LoadBalanceInfoTextBox.Text = _connectionToEdit.RdpLoadBalanceInfo ?? "";
                ExtraArgsTextBox.Text = _connectionToEdit.RdpExtraArgs ?? "";
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CredentialComboBox.SelectedItem == null)
                {
                    await ShowErrorAsync("Fehler", "Bitte wähle einen Benutzernamen aus.");
                    return;
                }

                // Optional: Sicherheitswarnung, wenn IgnoreCert aktiv ist
                if (IgnoreCertCheckBox.IsChecked == true)
                {
                    var proceed = await ShowWarningAsync(
                        title: "Sicherheitswarnung",
                        content: "„Zertifikat ignorieren“ deaktiviert die Server-Identitätsprüfung. Das kann Man-in-the-Middle-Angriffe ermöglichen.\n\nMöchtest du fortfahren?",
                        proceedText: "Fortfahren",
                        cancelText: "Abbrechen"
                    );

                    if (!proceed)
                        return;
                }

                var selectedCredential = (CredentialModel)CredentialComboBox.SelectedItem;
                var protocol = ((ComboBoxItem)ThemeComboBox.SelectedItem)?.Content?.ToString() ?? "RDP";

                // WICHTIG: target zuerst deklarieren
                HostlistModel target = _connectionToEdit ?? new HostlistModel();

                target.Protocol = protocol;
                target.Port = (PortTextBox.Text ?? "").Trim();
                target.DisplayName = (DisplaynameTextBox.Text ?? "").Trim();
                target.Hostname = (HostnameTextBox.Text ?? "").Trim();
                target.Domain = (DomainTextBox.Text ?? "").Trim();

                // >>> FIX: Eindeutige Zuordnung (GUID), damit gleiche Usernames nicht kollidieren
                target.CredentialId = selectedCredential.ID;

                // optional weiter befüllen (Anzeige / Backward compatibility)
                target.Username = selectedCredential.Username;

                // --- Persist advanced settings ---
                target.RdpIgnoreCert = IgnoreCertCheckBox.IsChecked == true;
                target.RdpTlsLegacy = TlsLegacyCheckBox.IsChecked == true;
                target.RdpDynamicResolution = DynamicResolutionCheckBox.IsChecked == true;
                target.RdpClipboard = ClipboardCheckBox.IsChecked == true;
                target.RdpAdminMode = AdminModeCheckBox.IsChecked == true;
                target.RdpLoadBalanceInfo = (LoadBalanceInfoTextBox.Text ?? "").Trim();
                target.RdpExtraArgs = (ExtraArgsTextBox.Text ?? "").Trim();

                if (_connectionToEdit == null)
                {
                    _hostlistService.AddConnection(target);
                }

                await _hostlistService.SaveConnectionsAsync();
                System.Diagnostics.Debug.WriteLine("Connection saved successfully.");
                Frame.Navigate(typeof(SessionsPage));
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Fehler", $"Ein Fehler ist aufgetreten: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Error: {ex}");
            }
        }

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                switch (selectedItem.Content?.ToString())
                {
                    case "RDP":
                        PortTextBox.Text = "3389";
                        break;
                    case "SSH":
                        PortTextBox.Text = "22";
                        break;
                    default:
                        PortTextBox.Text = string.Empty;
                        break;
                }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
            => Frame.Navigate(typeof(SessionsPage));

        private async Task ShowErrorAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async Task<bool> ShowWarningAsync(string title, string content, string proceedText, string cancelText)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = proceedText,
                CloseButtonText = cancelText,
                XamlRoot = this.XamlRoot
            };

            var res = await dialog.ShowAsync();
            return res == ContentDialogResult.Primary;
        }
    }
}
