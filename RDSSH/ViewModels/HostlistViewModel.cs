using System.Collections.ObjectModel;
using System.Threading.Tasks;
using RDSSH.Models;
using RDSSH.Services;
namespace RDSSH.ViewModels
{
    public class HostlistViewModel
    {
        private readonly HostlistService _hostlistService;

        public ObservableCollection<HostlistModel> Hostlist => _hostlistService.Hostlist;

        public HostlistViewModel()
        {
            _hostlistService = App.GetService<HostlistService>();
        }

        public async Task SaveConnectionAsync(HostlistModel connection)
        {
            _hostlistService.AddConnection(connection);
            await _hostlistService.SaveConnectionsAsync();
        }

        public async Task DeleteConnectionAsync(HostlistModel connection)
        {
            _hostlistService.RemoveConnection(connection);
            await _hostlistService.SaveConnectionsAsync();
        }


        public async Task LoadConnectionsAsync()
        {
            await _hostlistService.LoadConnectionsAsync();
        }
    }
}
