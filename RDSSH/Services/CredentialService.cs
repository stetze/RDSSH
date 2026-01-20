using System;
using System.Collections.ObjectModel;
using System.Linq;
using Meziantou.Framework.Win32;
using RDSSH.Models;

namespace RDSSH.Services
{
    public class CredentialService
    {
        public ObservableCollection<CredentialModel> CredentialDataSet { get; private set; }
        public CredentialModel CurrentEditingUser { get; set; }

        private const string Prefix = "RDSSH\\ACC\\";

        public CredentialService()
        {
            CredentialDataSet = new ObservableCollection<CredentialModel>();
            LoadCredentials();
        }

        private static string BuildAccountKey(Guid accountId, string username, string? domain)
        {
            username = (username ?? "").Trim();
            domain = (domain ?? "").Trim();
            var label = string.IsNullOrWhiteSpace(domain) ? username : $"{username}@{domain}";
            return $"{Prefix}{accountId:D}|{label}";
        }

        private static bool TryParseAccountId(string applicationName, out Guid id)
        {
            id = default;

            if (!applicationName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            // Format: RDSSH\ACC\<Guid>|...
            var rest = applicationName.Substring(Prefix.Length);
            var guidPart = rest.Split('|').FirstOrDefault();
            return Guid.TryParse(guidPart, out id);
        }

        private void LoadCredentials()
        {
            CredentialDataSet.Clear();

            var credentials = CredentialManager.EnumerateCredentials();

            foreach (var credential in credentials)
            {
                if (credential.ApplicationName == null)
                    continue;

                // Neues Schema
                if (TryParseAccountId(credential.ApplicationName, out var id))
                {
                    CredentialDataSet.Add(new CredentialModel
                    {
                        ID = id,
                        Username = credential.UserName,
                        Domain = credential.Comment
                    });
                    continue;
                }

                // Altes Schema (RDSSH\username) – optional als Legacy anzeigen
                if (credential.ApplicationName.StartsWith("RDSSH\\", StringComparison.OrdinalIgnoreCase) &&
                    !credential.ApplicationName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Legacy-Einträge hatten bei dir keine Password Secrets (""), aber Domain in Comment
                    CredentialDataSet.Add(new CredentialModel
                    {
                        ID = Guid.NewGuid(), // Legacy hat keine stabile ID -> neu erzeugen
                        Username = credential.UserName,
                        Domain = credential.Comment
                    });
                }
            }
        }

        /// <summary>
        /// Persistiert Account in Credential Manager (inkl. Passwort!)
        /// </summary>
        public void AddCredential(CredentialModel credential)
        {
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            if (credential.ID == Guid.Empty) credential.ID = Guid.NewGuid();

            var key = BuildAccountKey(credential.ID, credential.Username, credential.Domain);

            // WICHTIG: secret ist das Passwort (nicht leer!)
            CredentialManager.WriteCredential(
                applicationName: key,
                userName: credential.Username,
                secret: credential.Password ?? "",
                comment: credential.Domain ?? "",
                persistence: CredentialPersistence.LocalMachine
            );

            // Upsert in Dataset
            var existing = CredentialDataSet.FirstOrDefault(x => x.ID == credential.ID);
            if (existing == null)
            {
                CredentialDataSet.Add(new CredentialModel
                {
                    ID = credential.ID,
                    Username = credential.Username,
                    Domain = credential.Domain
                });
            }
            else
            {
                existing.Username = credential.Username;
                existing.Domain = credential.Domain;
            }
        }

        public void UpdateCredential(CredentialModel credential, string newUsername, string newDomain, string? newPassword)
        {
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            if (credential.ID == Guid.Empty) throw new InvalidOperationException("Credential.ID is empty");

            credential.Username = (newUsername ?? "").Trim();
            credential.Domain = (newDomain ?? "").Trim();

            var key = BuildAccountKey(credential.ID, credential.Username, credential.Domain);

            // Wenn newPassword null/empty ist, kannst du das vorhandene Passwort behalten:
            var secret = newPassword;
            if (string.IsNullOrEmpty(secret))
            {
                // vorhandenes Passwort lesen
                var existing = CredentialManager.ReadCredential(key);
                secret = existing?.Password ?? "";
            }

            CredentialManager.WriteCredential(
                applicationName: key,
                userName: credential.Username,
                secret: secret ?? "",
                comment: credential.Domain ?? "",
                persistence: CredentialPersistence.LocalMachine
            );
        }

        public void DeleteCredential(CredentialModel credential)
        {
            if (credential == null) return;

            // Wir müssen den gespeicherten ApplicationName-Key finden
            // (weil der Key auch Label enthält).
            var creds = CredentialManager.EnumerateCredentials()
                .Where(c => TryParseAccountId(c.ApplicationName ?? "", out var id) && id == credential.ID)
                .ToList();

            foreach (var c in creds)
            {
                CredentialManager.DeleteCredential(c.ApplicationName);
            }

            CredentialDataSet.Remove(credential);
        }

        /// <summary>
        /// Liefert Passwort + User/Domain aus dem Tresor für einen ausgewählten CredentialModel.
        /// </summary>
        public Credential? ReadVaultCredential(Guid credentialId)
        {
            var creds = CredentialManager.EnumerateCredentials()
                .FirstOrDefault(c => TryParseAccountId(c.ApplicationName ?? "", out var id) && id == credentialId);

            if (creds == null) return null;

            return CredentialManager.ReadCredential(creds.ApplicationName);
        }
    }
}
