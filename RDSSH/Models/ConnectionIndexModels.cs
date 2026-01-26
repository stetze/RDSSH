using System;
using System.Collections.Generic;

namespace RDSSH.Models
{
    /// <summary>
    /// Public/External search index for integrations (e.g., PowerToys Run plugin).
    /// No secrets.
    /// </summary>
    public sealed class ConnectionIndexFile
    {
        public int Version { get; set; } = 1;
        public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
        public List<ConnectionIndexEntry> Connections { get; set; } = new();
    }

    public sealed class ConnectionIndexEntry
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string Hostname { get; set; } = "";
        public string? Protocol { get; set; }
        public string? Port { get; set; }
    }
}
