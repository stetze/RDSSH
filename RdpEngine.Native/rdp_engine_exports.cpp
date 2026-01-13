// rdp_engine_exports.cpp
// Native bridge DLL for FreeRDP (WinUI3/C# PInvoke)

#include "pch.h"

#include <windows.h>
#include <string>
#include <vector>
#include <cwchar>
#include <cstdarg>
#include <cstdio>
#include <ws2tcpip.h>
#include <winver.h>

#pragma comment(lib, "Ws2_32.lib")
#pragma comment(lib, "Version.lib")

#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/provider.h>

// FreeRDP 3
#include <freerdp3/freerdp/freerdp.h>
#include <freerdp3/freerdp/settings.h>
#include <freerdp3/freerdp/version.h>
#include <freerdp3/freerdp/error.h>
#include <freerdp3/freerdp/gdi/gdi.h>   // gdi_init / gdi_free, rdpGdi

// -------------------------
// Logging helpers
// -------------------------
static void LogA(const char* fmt, ...)
{
    char buffer[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, fmt, args);
    va_end(args);
    OutputDebugStringA(buffer);
    OutputDebugStringA("\n");
}

static void LogW(const wchar_t* fmt, ...)
{
    wchar_t buffer[1024];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(buffer, _countof(buffer), _TRUNCATE, fmt, args);
    va_end(args);
    OutputDebugStringW(buffer);
    OutputDebugStringW(L"\n");
}

static std::string ToUtf8(const wchar_t* ws)
{
    if (!ws) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0, ws, -1, nullptr, 0, nullptr, nullptr);
    if (len <= 0) return {};
    std::string out((size_t)len - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, ws, -1, out.data(), len, nullptr, nullptr);
    return out;
}

extern "C" __declspec(dllexport) int RdpEngine_Version()
{
    return FREERDP_VERSION_MAJOR * 10000
        + FREERDP_VERSION_MINOR * 100
        + FREERDP_VERSION_REVISION;
}

// -------------------------
// Session struct
// -------------------------
struct RdpSession
{
    freerdp* instance = nullptr;
    rdpContext* context = nullptr;
    bool connected = false;

    HWND targetHwnd = nullptr;

    WSADATA wsa{};
    bool wsaInit = false;

    // cached BITMAPINFO for StretchDIBits
    BITMAPINFO bmi{};
    bool bmiInit = false;
};

// Forward declarations
static BOOL Rdp_PreConnect(freerdp* instance);
static BOOL Rdp_PostConnect(freerdp* instance);
static BOOL Rdp_ContextNew(freerdp* instance, rdpContext* context);
static void Rdp_ContextFree(freerdp* instance, rdpContext* context);

static void EnsureBmi32(RdpSession* s, int w, int h)
{
    if (!s) return;

    // Negative height => top-down DIB (passend zu den meisten Framebuffers)
    s->bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    s->bmi.bmiHeader.biWidth = w;
    s->bmi.bmiHeader.biHeight = -h;
    s->bmi.bmiHeader.biPlanes = 1;
    s->bmi.bmiHeader.biBitCount = 32;
    s->bmi.bmiHeader.biCompression = BI_RGB;
    s->bmi.bmiHeader.biSizeImage = 0;
    s->bmiInit = true;
}

// Best-effort: aus GDI primary buffer nach targetHwnd blitten
static void PaintToWindow(RdpSession* s)
{
    if (!s || !s->instance || !s->context || !s->connected) return;
    if (!s->targetHwnd) return;

    // FreeRDP GDI: gdi_init legt instance->context->gdi an
    // In FreeRDP ist das üblicherweise rdpGdi* in context->gdi
    rdpGdi* gdi = nullptr;

    // Viele Builds haben context->gdi als void* / rdpGdi*
    // Wir casten defensiv:
    gdi = (rdpGdi*)s->context->gdi;
    if (!gdi) return;

    // primary_buffer enthält den kompletten Desktop
    const int srcW = (int)gdi->width;
    const int srcH = (int)gdi->height;
    if (srcW <= 0 || srcH <= 0) return;

    BYTE* src = (BYTE*)gdi->primary_buffer;
    if (!src) return;

    // Zielgröße = ClientRect vom targetHwnd
    RECT rc{};
    if (!GetClientRect(s->targetHwnd, &rc)) return;

    const int dstW = rc.right - rc.left;
    const int dstH = rc.bottom - rc.top;
    if (dstW <= 0 || dstH <= 0) return;

    EnsureBmi32(s, srcW, srcH);

    HDC hdc = GetDC(s->targetHwnd);
    if (!hdc) return;

    // StretchDIBits: 13 Parameter (HDc + 12 ints/pointers)
    // Quelle: BGRA32 / BI_RGB, daher biBitCount=32, biCompression=BI_RGB
    int res = StretchDIBits(
        hdc,
        0, 0, dstW, dstH,          // dest
        0, 0, srcW, srcH,          // src
        src,
        &s->bmi,
        DIB_RGB_COLORS,
        SRCCOPY
    );

    ReleaseDC(s->targetHwnd, hdc);

    if (res == GDI_ERROR)
    {
        LogA("PaintToWindow: StretchDIBits -> GDI_ERROR (GetLastError=%lu)", (unsigned long)GetLastError());
    }
}

extern "C" __declspec(dllexport) void RdpSession_RequestRepaint(void* session)
{
    auto s = static_cast<RdpSession*>(session);
    if (!s) return;
    PaintToWindow(s);
}


// -------------------------
// FreeRDP callbacks
// -------------------------
static BOOL Rdp_ContextNew(freerdp* instance, rdpContext* context)
{
    (void)instance;
    (void)context;
    LogA("Rdp_ContextNew");
    return TRUE;
}

static void Rdp_ContextFree(freerdp* instance, rdpContext* context)
{
    (void)context;
    LogA("Rdp_ContextFree: gdi_free");
    if (instance)
        gdi_free(instance);
}

static BOOL Rdp_PreConnect(freerdp* instance)
{
    (void)instance;
    LogA("Rdp_PreConnect: OK");
    return TRUE;
}

static BOOL Rdp_PostConnect(freerdp* instance)
{
    if (!instance || !instance->context)
        return FALSE;

    LogA("Rdp_PostConnect: initializing GDI");

    // BGRA32 verhindert meist Farbkanal-Verkippungen
    if (!gdi_init(instance, PIXEL_FORMAT_BGRA32))
    {
        LogA("Rdp_PostConnect: gdi_init FAILED");
        return FALSE;
    }

    LogA("Rdp_PostConnect: OK (GDI initialized)");
    return TRUE;
}

// -------------------------
// Create / Destroy
// -------------------------
extern "C" __declspec(dllexport) void* RdpSession_Create()
{
    auto s = new RdpSession();

    if (WSAStartup(MAKEWORD(2, 2), &s->wsa) != 0)
    {
        delete s;
        return nullptr;
    }
    s->wsaInit = true;

    s->instance = freerdp_new();
    if (!s->instance)
    {
        WSACleanup();
        delete s;
        return nullptr;
    }

    s->instance->ContextSize = sizeof(rdpContext);
    s->instance->ContextNew = Rdp_ContextNew;
    s->instance->ContextFree = Rdp_ContextFree;
    s->instance->PreConnect = Rdp_PreConnect;
    s->instance->PostConnect = Rdp_PostConnect;

    if (!freerdp_context_new(s->instance))
    {
        freerdp_free(s->instance);
        s->instance = nullptr;
        WSACleanup();
        delete s;
        return nullptr;
    }

    s->context = s->instance->context;
    if (!s->context || !s->context->settings)
    {
        freerdp_context_free(s->instance);
        freerdp_free(s->instance);
        s->instance = nullptr;
        s->context = nullptr;
        WSACleanup();
        delete s;
        return nullptr;
    }

    LogA("RdpSession_Create: OK");
    return s;
}

extern "C" __declspec(dllexport) void RdpSession_Destroy(void* session)
{
    auto s = static_cast<RdpSession*>(session);
    if (!s) return;

    if (s->instance)
    {
        if (s->connected)
        {
            freerdp_disconnect(s->instance);
            s->connected = false;
        }

        if (s->instance->context)
        {
            freerdp_context_free(s->instance);
            s->context = nullptr;
        }

        freerdp_free(s->instance);
        s->instance = nullptr;
    }

    if (s->wsaInit)
    {
        WSACleanup();
        s->wsaInit = false;
    }

    delete s;
    LogA("RdpSession_Destroy: done");
}

extern "C" __declspec(dllexport) void RdpSession_Disconnect(void* session)
{
    auto s = static_cast<RdpSession*>(session);
    if (!s || !s->instance) return;

    if (s->connected)
    {
        freerdp_disconnect(s->instance);
        s->connected = false;
        LogA("RdpSession_Disconnect: disconnected");
    }
}

// -------------------------
// Attach HWND (call BEFORE Connect)
// -------------------------
extern "C" __declspec(dllexport) void RdpSession_AttachToHwnd(void* session, void* hwnd)
{
    auto s = static_cast<RdpSession*>(session);
    if (!s) return;

    s->targetHwnd = (HWND)hwnd;
    LogA("RdpSession_AttachToHwnd: hwnd=0x%p", hwnd);
}

// -------------------------
// Last error helpers (Context-based)
// -------------------------
extern "C" __declspec(dllexport) unsigned int RdpSession_GetLastError(void* session)
{
    auto s = static_cast<RdpSession*>(session);
    if (!s || !s->context) return 0;
    return freerdp_get_last_error(s->context);
}

extern "C" __declspec(dllexport) int RdpSession_GetLastErrorString(void* session, wchar_t* buffer, int cch)
{
    if (!buffer || cch <= 0) return 0;
    buffer[0] = L'\0';

    auto s = static_cast<RdpSession*>(session);
    if (!s || !s->context) return 0;

    const UINT32 code = freerdp_get_last_error(s->context);
    const char* msg = freerdp_get_last_error_string(code);
    if (!msg) msg = "UNKNOWN";

    int n = MultiByteToWideChar(CP_UTF8, 0, msg, -1, buffer, cch);
    if (n <= 0)
        n = MultiByteToWideChar(CP_ACP, 0, msg, -1, buffer, cch);

    buffer[cch - 1] = L'\0';
    return (n > 0) ? (n - 1) : 0;
}

// -------------------------
// Connect
// -------------------------
extern "C" __declspec(dllexport) int RdpSession_Connect(
    void* session,
    const wchar_t* host, int port,
    const wchar_t* username, const wchar_t* domain,
    const wchar_t* password,
    int width, int height,
    int dynamicResolution,
    const wchar_t* freerdpArgs)
{
    auto s = static_cast<RdpSession*>(session);
    if (!s || !s->instance || !s->context || !s->context->settings)
    {
        LogA("RdpSession_Connect: invalid session/instance/context/settings");
        return -2;
    }
    if (!host || !*host)
    {
        LogA("RdpSession_Connect: host is null/empty");
        return -3;
    }

    if (port <= 0) port = 3389;
    if (width <= 0) width = 1280;
    if (height <= 0) height = 720;

    auto settings = s->context->settings;

    LogW(L"RdpSession_Connect: host='%s' port=%d user='%s' domain='%s' dynRes=%d size=%dx%d args='%s'",
        host,
        port,
        (username ? username : L""),
        (domain ? domain : L""),
        dynamicResolution,
        width,
        height,
        (freerdpArgs ? freerdpArgs : L""));

    bool tlsSecLevel0 = false;
    bool ignoreCert = false;

    if (freerdpArgs && *freerdpArgs)
    {
        std::wstring args(freerdpArgs);
        if (args.find(L"/tls-seclevel:0") != std::wstring::npos) tlsSecLevel0 = true;
        if (args.find(L"/cert:ignore") != std::wstring::npos) ignoreCert = true;
    }

    if (tlsSecLevel0)
        freerdp_settings_set_uint32(settings, FreeRDP_TlsSecLevel, 0);

    if (ignoreCert)
        freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate, TRUE);

    // Server / creds
    {
        freerdp_settings_set_string(settings, FreeRDP_ServerHostname, ToUtf8(host).c_str());
        freerdp_settings_set_uint32(settings, FreeRDP_ServerPort, (UINT32)port);

        if (username && *username)
            freerdp_settings_set_string(settings, FreeRDP_Username, ToUtf8(username).c_str());

        if (domain && *domain)
            freerdp_settings_set_string(settings, FreeRDP_Domain, ToUtf8(domain).c_str());

        if (password && *password)
            freerdp_settings_set_string(settings, FreeRDP_Password, ToUtf8(password).c_str());
    }

    // Video
    freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, 32);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, (UINT32)width);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, (UINT32)height);

    if (dynamicResolution)
        freerdp_settings_set_bool(settings, FreeRDP_DynamicResolutionUpdate, TRUE);

    // Embedding flags (optional; wir rendern trotzdem selbst)
    if (s->targetHwnd != nullptr)
    {
        freerdp_settings_set_uint64(settings, FreeRDP_ParentWindowId, (UINT64)(uintptr_t)s->targetHwnd);
        freerdp_settings_set_bool(settings, FreeRDP_EmbeddedWindow, TRUE);
        freerdp_settings_set_bool(settings, FreeRDP_Decorations, FALSE);
        LogA("RdpSession_Connect: EmbeddedWindow enabled, ParentWindowId=0x%p", s->targetHwnd);
    }
    else
    {
        LogA("RdpSession_Connect: WARNING: no targetHwnd attached");
    }

    LogA("RdpSession_Connect: calling freerdp_connect()");
    if (!freerdp_connect(s->instance))
    {
        s->connected = false;
        const UINT32 err = freerdp_get_last_error(s->context);
        const char* errStr = freerdp_get_last_error_string(err);
        LogA("RdpSession_Connect: freerdp_connect FAILED last_error=0x%08X (%s)", err, errStr ? errStr : "UNKNOWN");
        return -10;
    }

    s->connected = true;
    LogA("RdpSession_Connect: freerdp_connect OK");
    return 0;
}

// -------------------------
// Pump
// -------------------------
extern "C" __declspec(dllexport) int __cdecl RdpSession_Pump(void* session, int timeoutMs)
{
    auto s = static_cast<RdpSession*>(session);
    if (!s || !s->context || !s->instance || !s->connected)
        return -1;

    HANDLE handles[64];
    const DWORD capacity = (DWORD)_countof(handles);

    const DWORD count = freerdp_get_event_handles(s->context, handles, capacity);
    if (count == 0)
        return 0;

    const DWORD to = (timeoutMs < 0) ? INFINITE : (DWORD)timeoutMs;
    const DWORD wrc = WaitForMultipleObjects(count, handles, FALSE, to);

    if (wrc == WAIT_TIMEOUT)
        return 0;

    if (wrc == WAIT_FAILED)
    {
        DWORD gle = GetLastError();
        LogA("Pump: WaitForMultipleObjects FAILED, GetLastError=%lu", (unsigned long)gle);
        return -3;
    }

    if (!freerdp_check_event_handles(s->context))
    {
        const UINT32 ferr = freerdp_get_last_error(s->context);
        const char* ferrStr = freerdp_get_last_error_string(ferr);
        LogA("Pump: freerdp_check_event_handles FAILED, freerdp_last_error=0x%08X (%s)",
            ferr, (ferrStr ? ferrStr : "UNKNOWN"));
        return -93;
    }

    // Nach jedem “event batch” versuchen wir zu zeichnen.
    PaintToWindow(s);

    return 0;
}

// -------------------------
// Diagnostics: DNS resolve
// -------------------------
extern "C" __declspec(dllexport) int RdpNet_TestResolve(const wchar_t* host, int port)
{
    if (!host || !*host) return -1;

    ADDRINFOW hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    wchar_t portStr[16];
    _itow_s(port > 0 ? port : 3389, portStr, 10);

    ADDRINFOW* res = nullptr;
    int rc = GetAddrInfoW(host, portStr, &hints, &res);
    if (res) FreeAddrInfoW(res);

    return rc;
}

// -------------------------
// Diagnostics: OpenSSL provider check
// -------------------------
extern "C" __declspec(dllexport) int RdpCrypto_Diagnose(wchar_t* buffer, int cch)
{
    if (!buffer || cch <= 0) return 0;
    buffer[0] = L'\0';

    OPENSSL_init_ssl(0, nullptr);
    OSSL_PROVIDER* legacy = OSSL_PROVIDER_load(nullptr, "legacy");

    unsigned long e = ERR_get_error();
    char errbuf[256]{};
    if (e != 0)
        ERR_error_string_n(e, errbuf, sizeof(errbuf));
    else
        strcpy_s(errbuf, "OK");

    std::string msg = "OpenSSL diag: legacy=";
    msg += (legacy ? "OK" : "FAIL");
    msg += " err=";
    msg += errbuf;

    if (legacy) OSSL_PROVIDER_unload(legacy);

    MultiByteToWideChar(CP_UTF8, 0, msg.c_str(), -1, buffer, cch);
    buffer[cch - 1] = L'\0';
    return (int)wcslen(buffer);
}

// -------------------------
// Version helper
// -------------------------
static std::wstring GetFileVersionOfModule(HMODULE hMod)
{
    if (!hMod) return L"<null>";

    wchar_t path[MAX_PATH]{};
    if (!GetModuleFileNameW(hMod, path, MAX_PATH))
        return L"<GetModuleFileNameW failed>";

    DWORD dummy = 0;
    DWORD size = GetFileVersionInfoSizeW(path, &dummy);
    if (!size) return std::wstring(path) + L" (no version info)";

    std::vector<BYTE> buf(size);
    if (!GetFileVersionInfoW(path, 0, size, buf.data()))
        return std::wstring(path) + L" (GetFileVersionInfo failed)";

    VS_FIXEDFILEINFO* ffi = nullptr;
    UINT len = 0;
    if (!VerQueryValueW(buf.data(), L"\\", (LPVOID*)&ffi, &len) || !ffi)
        return std::wstring(path) + L" (VerQueryValue failed)";

    wchar_t ver[64]{};
    swprintf_s(ver, L"%u.%u.%u.%u",
        HIWORD(ffi->dwFileVersionMS),
        LOWORD(ffi->dwFileVersionMS),
        HIWORD(ffi->dwFileVersionLS),
        LOWORD(ffi->dwFileVersionLS));

    return std::wstring(path) + L" v" + ver;
}

extern "C" __declspec(dllexport) int RdpEngine_LogRuntimeVersions(wchar_t* buffer, int cch)
{
    if (!buffer || cch <= 0) return 0;
    buffer[0] = L'\0';

    HMODULE hSelf = GetModuleHandleW(L"RdpEngine.Native.dll");
    HMODULE hFreerdp = GetModuleHandleW(L"freerdp3.dll");
    HMODULE hWinpr = GetModuleHandleW(L"winpr3.dll");
    HMODULE hSsl = GetModuleHandleW(L"libssl-3-x64.dll");
    HMODULE hCrypto = GetModuleHandleW(L"libcrypto-3-x64.dll");

    std::wstring msg;
    msg += L"RdpEngine.Native: " + GetFileVersionOfModule(hSelf) + L"\r\n";
    msg += L"freerdp3.dll: " + GetFileVersionOfModule(hFreerdp) + L"\r\n";
    msg += L"winpr3.dll: " + GetFileVersionOfModule(hWinpr) + L"\r\n";
    msg += L"libssl: " + GetFileVersionOfModule(hSsl) + L"\r\n";
    msg += L"libcrypto: " + GetFileVersionOfModule(hCrypto) + L"\r\n";

    wcsncpy_s(buffer, cch, msg.c_str(), _TRUNCATE);
    buffer[cch - 1] = L'\0';
    return (int)wcslen(buffer);
}
