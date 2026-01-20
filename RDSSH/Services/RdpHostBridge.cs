using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RDSSH.Services
{
    internal static class RdpHostBridge
    {
        // Muss identisch sein zum WPF-Projekt
        private const string PipeName = "RDSSH.RdpHost.Pipe";

        // Optional: etwas länger, weil WPF Host beim ersten Start ggf. noch JIT/COM initialisiert
        private static readonly TimeSpan HostStartupTimeout = TimeSpan.FromSeconds(8);

        public static async Task<bool> OpenRdpAsync(
            string title,
            string server,
            string username,
            string? domain,
            string password,
            CancellationToken ct)
        {
            // Credential Manager Key: du nutzt in deinem WinUI-Code "RDSSH\\<username>"
            // Das Passwort-Argument wird NICHT mehr über IPC geschickt.
            var credKey = "RDSSH\\" + username;

            // 1) Host sicherstellen
            EnsureHostProcessRunning();

            // 2) Pipe connect (mit Retry)
            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);

            var deadline = DateTime.UtcNow.Add(HostStartupTimeout);
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await client.ConnectAsync(500, ct).ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                    if (DateTime.UtcNow >= deadline) return false;
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline) return false;
                }
            }

            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(client, Encoding.UTF8, bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

            // 3) Typed payload
            var request = new OpenRdpRequest
            {
                Command = "OpenRdp",
                Title = title,
                Server = server,
                Username = username,
                Domain = domain,
                CredentialKey = credKey,
            };

            var json = JsonSerializer.Serialize(request);
            await writer.WriteLineAsync(json).ConfigureAwait(false);

            var response = await reader.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response)) return false;

            try
            {
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean())
                    return true;

                if (doc.RootElement.TryGetProperty("error", out var errProp))
                    Debug.WriteLine("RdpHostBridge: Host error: " + errProp.GetString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("RdpHostBridge: Bad response: " + response);
                Debug.WriteLine(ex.ToString());
            }

            return false;
        }

        private static void EnsureHostProcessRunning()
        {
            // Wenn bereits läuft -> nichts tun
            foreach (var p in Process.GetProcessesByName("RDSSH.RdpHost"))
            {
                try
                {
                    if (!p.HasExited) return;
                }
                catch { }
            }

            var baseDir = AppContext.BaseDirectory;

            // 1) Primär: im gleichen Output/AppX Ordner wie RDSSH.exe
            var exeNextToApp = Path.Combine(baseDir, "RDSSH.RdpHost.exe");
            if (File.Exists(exeNextToApp))
            {
                StartProcess(exeNextToApp, args: null);
                return;
            }

            // 2) Fallback: Suche im Solution bin (Debug/Build)
            //    baseDir bei WinUI (AppX) ist z.B. ...\RDSSH\bin\...\AppX\
            //    Wir suchen best-effort im Repo.
            string? exePath = FindHostExeInRepoFallback(baseDir);
            if (exePath != null)
            {
                StartProcess(exePath, args: null);
                return;
            }

            // 3) Letzter Fallback (nur Debug sinnvoll): dotnet RDSSH.RdpHost.dll
            //    (Achtung: in echter MSIX/Produktiv meist nicht verfügbar/erwünscht)
            var dllNextToApp = Path.Combine(baseDir, "RDSSH.RdpHost.dll");
            if (File.Exists(dllNextToApp))
            {
                StartProcess("dotnet", args: $"\"{dllNextToApp}\"");
                return;
            }

            throw new FileNotFoundException(
                "RDSSH.RdpHost.exe not found. Ensure RDSSH.RdpHost.exe + RDSSH.RdpHost.dll are copied into the WinUI AppX output folder (and packaged).");
        }

        private static void StartProcess(string fileName, string? args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args ?? "",
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
            };

            Process.Start(psi);
        }

        private static string? FindHostExeInRepoFallback(string baseDir)
        {
            // Best-effort: gehe ein paar Ebenen hoch und suche nach RDSSH.RdpHost\bin\...\RDSSH.RdpHost.exe
            // Das ist bewusst tolerant, weil dein Layout / Output variieren kann.
            try
            {
                var probeRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
                if (!Directory.Exists(probeRoot))
                    probeRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));

                if (!Directory.Exists(probeRoot))
                    return null;

                foreach (var f in Directory.EnumerateFiles(probeRoot, "RDSSH.RdpHost.exe", SearchOption.AllDirectories))
                {
                    // Optional: du kannst hier noch filtern (z.B. Configuration/Platform)
                    return f;
                }
            }
            catch { }

            return null;
        }

        // IPC Contract (muss mit Host übereinstimmen)
        private sealed class OpenRdpRequest
        {
            public string Command { get; set; } = "OpenRdp";
            public string Title { get; set; } = "RDP";
            public string Server { get; set; } = "";
            public string Username { get; set; } = "";
            public string? Domain { get; set; }
            public string CredentialKey { get; set; } = "";
        }
    }
}
