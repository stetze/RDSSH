using System;
using System.Diagnostics;
using System.Text;
using Meziantou.Framework.Win32;

namespace RDSSH.Services;

public sealed class RdpEngineService
{
    public int Connect(
        string host,
        int? port,
        string username,
        string? domain,
        string credentialKey,     // z.B. "RDSSH\\ladm_daniel"
        int width,
        int height,
        bool dynamicResolution,
        string? freerdpArgs,
        out string error)
    {
        error = "";

        var h = RdpSessionNative.Create();
        if (h == 0)
        {
            error = "RdpSessionNative.Create failed";
            return -1;
        }

        try
        {
            host = (host ?? "").Trim();
            username = (username ?? "").Trim();
            domain = (domain ?? "").Trim();

            int p = (port.GetValueOrDefault(3389) <= 0) ? 3389 : port.Value;

            // Credential Manager lesen (kein Klartext im UI)
            var cred = CredentialManager.ReadCredential(credentialKey);
            if (cred == null)
            {
                error = $"Credential not found: {credentialKey}";
                return -2;
            }

            // Optional: vorher DNS Test (kann man später entfernen)
            var gai = RdpSessionNative.NetTestResolve(host, p);
            Debug.WriteLine($"GetAddrInfoW rc={gai}");

            int rc = RdpSessionNative.Connect(
                h,
                host,
                p,
                username,
                domain ?? "",
                cred.Password,
                width,
                height,
                dynamicResolution ? 1 : 0,
                freerdpArgs ?? ""
            );

            uint lastErr = RdpSessionNative.GetLastError(h);
            var sb = new StringBuilder(256);
            RdpSessionNative.GetLastErrorString(h, sb, sb.Capacity);

            Debug.WriteLine($"RdpSessionNative.Connect rc={rc} lastErr=0x{lastErr:X8} {sb}");

            if (rc != 0)
            {
                error = $"rc={rc} lastErr=0x{lastErr:X8} {sb}";
            }

            return rc;
        }
        finally
        {
            // In Phase 1 machen wir Connect/Disconnect testweise.
            // Später behalten wir die Session offen und hängen Rendering dran.
            RdpSessionNative.Disconnect(h);
            RdpSessionNative.Destroy(h);
        }
    }
}
