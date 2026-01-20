using Meziantou.Framework.Win32;
using RDSSH.Helpers;
using System.Diagnostics;
using System.Text;
using Windows.UI.ViewManagement;

namespace RDSSH;

public sealed partial class MainWindow : WindowEx
{
    private Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue;

    private UISettings settings;

    public MainWindow()
    {
        InitializeComponent();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        Content = null;
        Title = "AppDisplayName".GetLocalized();

        // Theme change code picked from https://github.com/microsoft/WinUI-Gallery/pull/1239
        dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        settings = new UISettings();
        settings.ColorValuesChanged += Settings_ColorValuesChanged; // cannot use FrameworkElement.ActualThemeChanged event
                                                                   
        //try
        //{
        //    var v = FreeRdpNative.RdpEngine_Version();
        //    System.Diagnostics.Debug.WriteLine($"FreeRDP version code: {v}");
        //}
        //catch (Exception ex)
        //{
        //    System.Diagnostics.Debug.WriteLine($"Native call failed: {ex}");
        //}
        //#if DEBUG
        //        try
        //        {
        //            nint h = 0;
        //            try
        //            {
        //                h = RdpSessionNative.Create();
        //                if (h == 0)
        //                {
        //                    Debug.WriteLine("RdpSessionNative.Create failed");
        //                    return; // <-- WICHTIG: DAS entfernen, siehe unten
        //                }

        //                string hostName = "rzkl-aet01".Trim();
        //                int port = 3389;
        //                string username = "ladm_daniel";
        //                string domain = "";

        //                var gai = RdpSessionNative.NetTestResolve(hostName, port);
        //                Debug.WriteLine($"GetAddrInfoW rc={gai}");

        //                var sbDiag = new StringBuilder(512);
        //                RdpSessionNative.CryptoDiagnose(sbDiag, sbDiag.Capacity);
        //                Debug.WriteLine(sbDiag.ToString());

        //                var cred = CredentialManager.ReadCredential("RDSSH\\" + username);
        //                if (cred == null)
        //                {
        //                    Debug.WriteLine("Credential not found in Credential Manager.");
        //                    return; // <-- auch das entfernen
        //                }

        //                string password = cred.Password;
        //                string args = "/cert:ignore /tls-seclevel:0";

        //                int rc = RdpSessionNative.Connect(
        //                    h,
        //                    hostName,
        //                    port,
        //                    username,
        //                    domain,
        //                    password,
        //                    1280,
        //                    720,
        //                    1,
        //                    args
        //                );

        //                if (rc != 0)
        //                {
        //                    uint lastErr = RdpSessionNative.GetLastError(h);
        //                    var sb = new StringBuilder(256);
        //                    RdpSessionNative.GetLastErrorString(h, sb, sb.Capacity);
        //                    Debug.WriteLine($"RdpSessionNative.Connect rc={rc} lastErr=0x{lastErr:X8} {sb}");
        //                }
        //                else
        //                {
        //                    Debug.WriteLine("RdpSessionNative.Connect rc=0 (success), disconnecting test session...");
        //                    RdpSessionNative.Disconnect(h);
        //                }
        //            }
        //            finally
        //            {
        //                if (h != 0)
        //                {
        //                    try { RdpSessionNative.Destroy(h); } catch { }
        //                }
        //            }
        //        }
        //        catch (DllNotFoundException ex)
        //        {
        //            Debug.WriteLine($"Native DLL missing -> skipping debug RDP test. {ex.Message}");
        //        }
        //        catch (Exception ex)
        //        {
        //            Debug.WriteLine($"Rdp connect test failed: {ex}");
        //        }
        //#endif


    }

    // this handles updating the caption button colors correctly when indows system theme is changed
    // while the app is open
    private void Settings_ColorValuesChanged(UISettings sender, object args)
    {
        // This calls comes off-thread, hence we will need to dispatch it to current app's thread
        dispatcherQueue.TryEnqueue(() =>
        {
            TitleBarHelper.ApplySystemThemeToCaptionButtons();
        });
    }
}
