using System;
using System.ComponentModel;

namespace RDSSH.Models
{
    public class HostlistModel : IEquatable<HostlistModel>, INotifyPropertyChanged
    {
        private bool _isConnected;

        // >>> NEU: stabile ID pro Verbindung
        public Guid ConnectionId { get; set; } = Guid.NewGuid();

        // Referenz auf gespeicherten Credential-Account
        public Guid? CredentialId { get; set; }
        public string Protocol { get; set; }
        public string Port { get; set; }
        private string _displayName;
        private string _hostname;

        public string DisplayName
        {
            get => _displayName;
            set
            {
                _displayName = value;
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(Title));
            }
        }

        public string Hostname
        {
            get => _hostname;
            set
            {
                _hostname = value;
                OnPropertyChanged(nameof(Hostname));
                OnPropertyChanged(nameof(Title));
            }
        }

        // Helper: show DisplayName if present, otherwise fallback to Hostname
        public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Hostname ?? string.Empty : DisplayName;

        public string Domain { get; set; }
        public string Username { get; set; }

        // --- RDP Advanced Settings (persisted) ---
        public bool RdpIgnoreCert { get; set; }
        public bool RdpTlsLegacy { get; set; }
        public bool RdpDynamicResolution { get; set; } = true;
        public bool RdpClipboard { get; set; } = true;
        public bool RdpAdminMode { get; set; }
        public string RdpLoadBalanceInfo { get; set; }
        public string RdpExtraArgs { get; set; }

        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                _isConnected = value;
                OnPropertyChanged(nameof(IsConnected));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public override string ToString() => $"{DisplayName} ({Protocol})";

        public bool Equals(HostlistModel other)
        {
            if (other == null) return false;

            return ConnectionId == other.ConnectionId &&
                   string.Equals(Protocol, other.Protocol, StringComparison.Ordinal) &&
                   string.Equals(Port, other.Port, StringComparison.Ordinal) &&
                   string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
                   string.Equals(Hostname, other.Hostname, StringComparison.Ordinal) &&
                   string.Equals(Domain, other.Domain, StringComparison.Ordinal) &&
                   string.Equals(Username, other.Username, StringComparison.Ordinal) &&
                   RdpIgnoreCert == other.RdpIgnoreCert &&
                   RdpTlsLegacy == other.RdpTlsLegacy &&
                   RdpDynamicResolution == other.RdpDynamicResolution &&
                   RdpClipboard == other.RdpClipboard &&
                   RdpAdminMode == other.RdpAdminMode &&
                   string.Equals(RdpLoadBalanceInfo, other.RdpLoadBalanceInfo, StringComparison.Ordinal) &&
                   string.Equals(RdpExtraArgs, other.RdpExtraArgs, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as HostlistModel);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(ConnectionId);
            h.Add(Protocol);
            h.Add(Port);
            h.Add(DisplayName);
            h.Add(Hostname);
            h.Add(Domain);
            h.Add(Username);

            h.Add(RdpIgnoreCert);
            h.Add(RdpTlsLegacy);
            h.Add(RdpDynamicResolution);
            h.Add(RdpClipboard);
            h.Add(RdpAdminMode);
            h.Add(RdpLoadBalanceInfo);
            h.Add(RdpExtraArgs);

            return h.ToHashCode();
        }
    }
}
