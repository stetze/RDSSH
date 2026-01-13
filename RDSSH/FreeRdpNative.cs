using System.Runtime.InteropServices;

namespace RDSSH
{
    internal static class FreeRdpNative
    {
        [DllImport("RdpEngine.Native.dll",
            EntryPoint = "RdpEngine_Version",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int RdpEngine_Version();
    }
}
