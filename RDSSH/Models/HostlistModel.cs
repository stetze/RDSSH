using System;
using System.ComponentModel;

namespace RDSSH.Models
{
    public class HostlistModel : IEquatable<HostlistModel>, INotifyPropertyChanged
    {
        private bool _isConnected;

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
        public bool RdpIgnoreCert { get; set; }              // /cert:ignore (NOT default)
        public bool RdpTlsLegacy { get; set; }               // /tls-seclevel:0 (NOT default)
        public bool RdpDynamicResolution { get; set; } = true; // /dynamic-resolution (default ON)
        public bool RdpClipboard { get; set; } = true;       // /clipboard (default ON)
        public bool RdpAdminMode { get; set; }               // /admin (NOT default)
        public string RdpLoadBalanceInfo { get; set; }       // /load-balance-info:tsv://...
        public string RdpExtraArgs { get; set; }             // optional: raw additional args (advanced textbox)

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

            return string.Equals(Protocol, other.Protocol, StringComparison.Ordinal) &&
                   string.Equals(Port, other.Port, StringComparison.Ordinal) &&
                   string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
                   string.Equals(Hostname, other.Hostname, StringComparison.Ordinal) &&
                   string.Equals(Domain, other.Domain, StringComparison.Ordinal) &&
                   string.Equals(Username, other.Username, StringComparison.Ordinal) &&

                   // New fields
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
            // HashCode.Combine hat Limitierungen – daher “stufenweise”:
            var h = new HashCode();
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
