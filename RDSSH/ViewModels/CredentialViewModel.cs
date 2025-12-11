using RDSSH.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RDSSH.ViewModels
{
    public class CredentialViewModel
    {
        public ObservableCollection<CredentialModel> CredentialDataSet { get; set; }
        public CredentialModel CurrentEditingUser { get; set; }

        public CredentialViewModel()
        {
            CredentialDataSet = new ObservableCollection<CredentialModel>();
        }
    }
}
