using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RDSSH
{
    internal static class RdpSessionNative
    {
        private const string Dll = "RdpEngine.Native.dll";

        [DllImport(Dll, EntryPoint = "RdpSession_Create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Create();

        [DllImport(Dll, EntryPoint = "RdpSession_Destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(nint session);

        [DllImport(Dll, EntryPoint = "RdpSession_Disconnect", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Disconnect(nint session);

        [DllImport(Dll, EntryPoint = "RdpSession_Pump", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Pump(nint session, int timeoutMs);

        [DllImport("RdpEngine.Native.dll", EntryPoint = "RdpSession_RequestRepaint", CallingConvention = CallingConvention.Cdecl)]
        public static extern void RequestRepaint(nint session);

        [DllImport(Dll, EntryPoint = "RdpSession_GetLastError", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint GetLastError(nint session);

        [DllImport(Dll, EntryPoint = "RdpSession_GetLastErrorString", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetLastErrorString(nint session, StringBuilder buffer, int cch);

        [DllImport(Dll, EntryPoint = "RdpNet_TestResolve", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NetTestResolve(string host, int port);

        [DllImport(Dll, EntryPoint = "RdpCrypto_Diagnose", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CryptoDiagnose(StringBuilder buffer, int cch);

        // HWND an die Session binden (vor Connect!)
        [DllImport(Dll, EntryPoint = "RdpSession_AttachToHwnd", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void AttachToHwnd(nint session, nint hwnd);

        [DllImport(Dll,
            EntryPoint = "RdpSession_Connect",
            CharSet = CharSet.Unicode,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Connect(
            nint session,
            string host, int port,
            string username, string domain,
            string password,
            int width, int height,
            int dynamicResolution,
            string freerdpArgs);

        [DllImport(Dll,
            EntryPoint = "RdpEngine_LogRuntimeVersions",
            CharSet = CharSet.Unicode,
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int LogRuntimeVersionsNative(StringBuilder buffer, int cch);

        internal static void LogRuntimeVersions()
        {
            try
            {
                Debug.WriteLine("LogRuntimeVersions(): entering");

                var sb = new StringBuilder(2048);
                int n = LogRuntimeVersionsNative(sb, sb.Capacity);

                Debug.WriteLine($"LogRuntimeVersions(): native returned n={n}");
                Debug.WriteLine("LogRuntimeVersions(): " + sb);

                Debug.WriteLine("LogRuntimeVersions(): leaving");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("LogRuntimeVersions(): EXCEPTION -> " + ex);
            }
        }
    }
}
