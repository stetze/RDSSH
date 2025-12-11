using System;
using System.Collections.ObjectModel;
using Meziantou.Framework.Win32;
using RDSSH.Models;

namespace RDSSH.Services
{
    public class CredentialService
    {
        public ObservableCollection<CredentialModel> CredentialDataSet { get; private set; }
        public CredentialModel CurrentEditingUser { get; set; }

        public CredentialService()
        {
            CredentialDataSet = new ObservableCollection<CredentialModel>();
            LoadCredentials();
        }

        private void LoadCredentials()
        {
            var credentials = CredentialManager.EnumerateCredentials();
            foreach (var credential in credentials)
            {
                if (credential.ApplicationName.StartsWith("RDSSH\\"))
                {
                    CredentialDataSet.Add(new CredentialModel
                    {
                        ID = Guid.NewGuid(),
                        Username = credential.UserName,
                        Domain = credential.Comment
                    });
                }
            }
        }

        public void AddCredential(CredentialModel credential)
        {
            CredentialManager.WriteCredential("RDSSH\\" + credential.Username, credential.Username, "", credential.Domain, CredentialPersistence.LocalMachine);
            CredentialDataSet.Add(credential);
        }

        public void UpdateCredential(CredentialModel credential, string newUsername, string newDomain)
        {
            var oldApplicationName = "RDSSH\\" + credential.Username;
            var newApplicationName = "RDSSH\\" + newUsername;

            CredentialManager.WriteCredential(newApplicationName, newUsername, "", newDomain, CredentialPersistence.LocalMachine);

            if (oldApplicationName != newApplicationName)
            {
                CredentialManager.DeleteCredential(oldApplicationName);
            }

            credential.Username = newUsername;
            credential.Domain = newDomain;
        }

        public void DeleteCredential(CredentialModel credential)
        {
            var applicationName = "RDSSH\\" + credential.Username;
            CredentialManager.DeleteCredential(applicationName);
            CredentialDataSet.Remove(credential);
        }
    }
}
