using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using RDSSH.Models;
using System;
using RDSSH.Services;
using System.Linq;

namespace RDSSH.Services
{
    public class HostlistService
    {
        public ObservableCollection<HostlistModel> Hostlist { get; } = new ObservableCollection<HostlistModel>();

        public void AddConnection(HostlistModel connection)
        {
            Hostlist.Add(connection);
        }

        public async Task SaveConnectionsAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var file = await localFolder.CreateFileAsync("connections.json", CreationCollisionOption.OpenIfExists);

                // Serialize the current in-memory Hostlist directly to avoid null-deserialization issues
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNameCaseInsensitive = true
                };
                string json = JsonSerializer.Serialize(Hostlist, options);

                await FileIO.WriteTextAsync(file, json);
                System.Diagnostics.Debug.WriteLine($"Connections saved successfully to {file.Path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving connections: {ex.Message}");
            }
        }

        public void RemoveConnection(HostlistModel connection)
        {
            if (Hostlist.Contains(connection))
            {
                Hostlist.Remove(connection);
            }
        }


        public async Task LoadConnectionsAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var file = await localFolder.CreateFileAsync("connections.json", CreationCollisionOption.OpenIfExists);
                string json = await FileIO.ReadTextAsync(file);

                var connections = string.IsNullOrEmpty(json)
                    ? new ObservableCollection<HostlistModel>()
                    : JsonSerializer.Deserialize<ObservableCollection<HostlistModel>>(json);

                // Ensure we have a non-null collection
                if (connections == null)
                {
                    connections = new ObservableCollection<HostlistModel>();
                }

                Hostlist.Clear();

                // >>> NEU: Legacy-Migration für CredentialId
                var credSvc = App.GetService<CredentialService>();

                foreach (var connection in connections)
                {
                    if ((connection.CredentialId == null || connection.CredentialId == Guid.Empty)
                        && !string.IsNullOrWhiteSpace(connection.Username))
                    {
                        var match = credSvc.CredentialDataSet.FirstOrDefault(c =>
                            string.Equals(c.Username, connection.Username, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.Domain ?? "", connection.Domain ?? "", StringComparison.OrdinalIgnoreCase));

                        if (match != null)
                        {
                            connection.CredentialId = match.ID;
                        }
                    }

                    Hostlist.Add(connection);
                }

                System.Diagnostics.Debug.WriteLine($"Connections loaded successfully from {file.Path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading connections: {ex.Message}");
            }
        }


    }
}
