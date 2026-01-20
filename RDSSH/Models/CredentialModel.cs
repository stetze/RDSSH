using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RDSSH.Models
{
    public class CredentialModel
    {
        public Guid ID { get; set; } = Guid.NewGuid();
        public string Username { get; set; }
        public string Domain { get; set; }
        public string Password { get; set; } // nur zur Eingabe, nicht im Hostlist speichern
    }
}
