using System;
using System.ComponentModel;

namespace RDSSH.Models
{
    public class HostlistModel : IEquatable<HostlistModel>, INotifyPropertyChanged
    {
        private bool _isConnected;

        public string Protocol { get; set; }
        public string Port { get; set; }
        public string DisplayName { get; set; }
        public string Hostname { get; set; }
        public string Domain { get; set; }
        public string Username { get; set; }

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
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public override string ToString()
        {
            return $"{DisplayName} ({Protocol})";
        }

        public bool Equals(HostlistModel other)
        {
            if (other == null) return false;
            return this.Protocol == other.Protocol &&
                   this.Port == other.Port &&
                   this.DisplayName == other.DisplayName &&
                   this.Hostname == other.Hostname &&
                   this.Domain == other.Domain &&
                   this.Username == other.Username;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as HostlistModel);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Protocol, Port, DisplayName, Hostname, Domain, Username);
        }
    }
}
