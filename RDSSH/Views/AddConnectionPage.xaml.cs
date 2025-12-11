using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RDSSH.Models;
using RDSSH.Services;
using System;
using System.Linq;

namespace RDSSH.Views
{
    public sealed partial class AddConnectionPage : Page
    {
        public CredentialService CredentialService { get; }

        private readonly HostlistService _hostlistService;
        private HostlistModel _connectionToEdit;

        public AddConnectionPage()
        {
            this.InitializeComponent();
            CredentialService = App.GetService<CredentialService>();
            _hostlistService = App.GetService<HostlistService>();
            this.DataContext = this;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _connectionToEdit = e.Parameter as HostlistModel;

            if (_connectionToEdit != null)
            {
                // Fülle die Felder mit den Daten der zu bearbeitenden Verbindung
                ThemeComboBox.SelectedItem = ThemeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == _connectionToEdit.Protocol);
                PortTextBox.Text = _connectionToEdit.Port;
                DisplaynameTextBox.Text = _connectionToEdit.DisplayName;
                HostnameTextBox.Text = _connectionToEdit.Hostname;
                DomainTextBox.Text = _connectionToEdit.Domain;
                CredentialComboBox.SelectedItem = CredentialService.CredentialDataSet.FirstOrDefault(cred => cred.Username == _connectionToEdit.Username);
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CredentialComboBox.SelectedItem == null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Fehler",
                        Content = "Bitte wähle einen Benutzernamen aus.",
                        CloseButtonText = "OK"
                    };
                    await dialog.ShowAsync();
                    return;
                }

                var selectedCredential = (CredentialModel)CredentialComboBox.SelectedItem;
                var connection = new HostlistModel
                {
                    Protocol = ((ComboBoxItem)ThemeComboBox.SelectedItem).Content.ToString(),
                    Port = PortTextBox.Text,
                    DisplayName = DisplaynameTextBox.Text,
                    Hostname = HostnameTextBox.Text,
                    Domain = DomainTextBox.Text,
                    Username = selectedCredential.Username
                };

                if (_connectionToEdit != null)
                {
                    // Aktualisiere die bestehende Verbindung
                    var index = _hostlistService.Hostlist.IndexOf(_connectionToEdit);
                    if (index >= 0)
                    {
                        _hostlistService.Hostlist[index] = connection;
                    }
                }
                else
                {
                    // Füge eine neue Verbindung hinzu
                    _hostlistService.AddConnection(connection);
                }

                await _hostlistService.SaveConnectionsAsync();
                System.Diagnostics.Debug.WriteLine("Connection saved successfully.");
                this.Frame.Navigate(typeof(SessionsPage));
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "Fehler",
                    Content = $"Ein Fehler ist aufgetreten: {ex.Message}",
                    CloseButtonText = "OK"
                };
                await dialog.ShowAsync();
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
            }
        }



        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                switch (selectedItem.Content.ToString())
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
        {
            this.Frame.Navigate(typeof(SessionsPage));
        }
    }
}
