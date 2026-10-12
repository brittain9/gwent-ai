/*
 * Private name routing, a stand-in Galaxy service, acceptance of this
 * install's certificate and loopback-only listeners for the client on
 * Windows, in the game process only: the counterpart of the /etc/hosts that
 * scripts/run-local.sh bind-mounts on Linux and of platform/macos/session_net.c.
 * Nothing outside the process changes: no hosts file, no Windows service,
 * no DNS setting, no certificate store, no firewall rule.
 *
 * The GwentSessionNet plugin (platform/windows/SessionNet) loads this library
 * when the session sets GWENT_SESSION_NET_DLL and calls session_net_install.
 * It rewrites import-table entries of the modules loaded from the client
 * folder (UnityPlayer.dll, the Mono runtimes, MelonLoader's included,
 * Galaxy64.dll, ...): those loaded at install, and each one the loader maps
 * later (ntdll's LdrRegisterDllNotification), such as the Wwise engine
 * AkSoundEngine.dll, which the client loads after the plugin's last call:
 *
 *   getaddrinfo, GetAddrInfoW, gethostbyname
 *       A name listed in the hosts file (hosts-file syntax; "*.example.com"
 *       matches every subdomain; a trailing dot is ignored) resolves to its
 *       listed address; other names go to the system resolver unchanged.
 *   GetProcAddress
 *       A module that looks one of these functions up at run time gets the
 *       hooked version too (InitSecurityInterfaceA/W included, see below).
 *   bind
 *       A socket bound to every interface (0.0.0.0 or ::) is bound to
 *       loopback instead, so the game's own listeners (the Wwise profiler's
 *       discovery and command ports, Galaxy's) are not reachable from the
 *       network and Windows Firewall has nothing to ask about. Logged with
 *       the calling module and the port.
 *   OpenSCManagerA/W, OpenServiceA/W, QueryServiceStatus(Ex), StartServiceA/W,
 *   ControlService, CloseServiceHandle (Galaxy64.dll only)
 *       The Galaxy SDK signs in only while a Windows service named
 *       GalaxyCommunication reports SERVICE_RUNNING (see
 *       server/comet-main/dummy-service/communication_wine.c). Its queries for
 *       that service are answered "running" here; the session's commservice.py
 *       answers the SDK on 127.0.0.1:9977 as on Linux.
 *
 * This install's certificate (GWENT_SESSION_CERT_SHA256, the SHA-256 of
 * .runtime/certs/fake.crt). Galaxy64.dll carries libcurl 7.57 on Schannel,
 * which asks Schannel to validate the server's chain against the Windows
 * trust store (SCH_CRED_AUTO_CRED_VALIDATION) and hangs up on the local
 * server's self-signed certificate. When a client module fetches
 * InitSecurityInterfaceA/W, it gets a copy of the Schannel function table in
 * which:
 *   - AcquireCredentialsHandle, for an outbound Schannel credential that
 *     Schannel would validate, also acquires a twin that leaves validation to
 *     the caller (SCH_CRED_MANUAL_CRED_VALIDATION);
 *   - InitializeSecurityContext uses that twin only for a connection to a
 *     name the hosts file routes to the local server, and when that handshake
 *     completes, accepts the server's certificate only if it is exactly this
 *     install's (SHA-256 of the DER). Any other certificate on such a
 *     connection is validated as Schannel would (CertGetCertificateChain and
 *     the SSL chain policy with the server name and the requested revocation
 *     checks). Connections to other names keep Schannel's own validation.
 * Without GWENT_SESSION_CERT_SHA256 the Schannel table is left alone.
 *
 * GWENT_SESSION_NET_TRACE=1 also logs each name client modules look up with
 * GetProcAddress, with the calling module (diagnostics).
 *
 * Build (MinGW, 64-bit like the client):
 *   x86_64-w64-mingw32-gcc -O2 -Wall -Wextra -shared platform/windows/session_net.c \
 *       -o dist/session_net.dll -lws2_32 -lpsapi -lcrypt32 -Wl,--no-insert-timestamp
 */
#define _WIN32_WINNT 0x0601
#define SECURITY_WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <wincrypt.h>
#include <sspi.h>
#include <schannel.h>
#include <psapi.h>
#include <ctype.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

#ifndef SECURITY_FLAG_IGNORE_CERT_CN_INVALID
#define SECURITY_FLAG_IGNORE_CERT_CN_INVALID 0x00001000
#endif

#define MAX_NAMES 64
#define MAX_LOGGED 200
#define MAX_TRACKED 256

static struct { char name[256]; char address[64]; wchar_t waddress[64]; } entries[MAX_NAMES];
static int entry_count;
static int loaded;
static int started;
static wchar_t module_dir[MAX_PATH];
static size_t module_dir_length;
static FILE *log_file;
static CRITICAL_SECTION log_lock;
static LONG logged;
static HMODULE self;
static int trace;
static BYTE pin[32];
static int pinned;

typedef INT (WSAAPI *getaddrinfo_fn)(PCSTR, PCSTR, const ADDRINFOA *, PADDRINFOA *);
typedef INT (WSAAPI *GetAddrInfoW_fn)(PCWSTR, PCWSTR, const ADDRINFOW *, PADDRINFOW *);
typedef struct hostent *(WSAAPI *gethostbyname_fn)(const char *);
typedef int (WSAAPI *bind_fn)(SOCKET, const struct sockaddr *, int);
typedef FARPROC (WINAPI *GetProcAddress_fn)(HMODULE, LPCSTR);
typedef SC_HANDLE (WINAPI *OpenSCManagerA_fn)(LPCSTR, LPCSTR, DWORD);
typedef SC_HANDLE (WINAPI *OpenSCManagerW_fn)(LPCWSTR, LPCWSTR, DWORD);
typedef SC_HANDLE (WINAPI *OpenServiceA_fn)(SC_HANDLE, LPCSTR, DWORD);
typedef SC_HANDLE (WINAPI *OpenServiceW_fn)(SC_HANDLE, LPCWSTR, DWORD);
typedef BOOL (WINAPI *QueryServiceStatus_fn)(SC_HANDLE, LPSERVICE_STATUS);
typedef BOOL (WINAPI *QueryServiceStatusEx_fn)(SC_HANDLE, SC_STATUS_TYPE, LPBYTE, DWORD, LPDWORD);
typedef BOOL (WINAPI *StartServiceA_fn)(SC_HANDLE, DWORD, LPCSTR *);
typedef BOOL (WINAPI *StartServiceW_fn)(SC_HANDLE, DWORD, LPCWSTR *);
typedef BOOL (WINAPI *ControlService_fn)(SC_HANDLE, DWORD, LPSERVICE_STATUS);
typedef BOOL (WINAPI *CloseServiceHandle_fn)(SC_HANDLE);

static getaddrinfo_fn real_getaddrinfo;
static GetAddrInfoW_fn real_GetAddrInfoW;
static gethostbyname_fn real_gethostbyname;
static bind_fn real_bind;
static GetProcAddress_fn real_GetProcAddress;
static OpenSCManagerA_fn real_OpenSCManagerA;
static OpenSCManagerW_fn real_OpenSCManagerW;
static OpenServiceA_fn real_OpenServiceA;
static OpenServiceW_fn real_OpenServiceW;
static QueryServiceStatus_fn real_QueryServiceStatus;
static QueryServiceStatusEx_fn real_QueryServiceStatusEx;
static StartServiceA_fn real_StartServiceA;
static StartServiceW_fn real_StartServiceW;
static ControlService_fn real_ControlService;
static CloseServiceHandle_fn real_CloseServiceHandle;
static INIT_SECURITY_INTERFACE_A real_InitSecurityInterfaceA;
static INIT_SECURITY_INTERFACE_W real_InitSecurityInterfaceW;

/* Handles no real service manager hands out: addresses inside this library. */
static char fake_manager_object, fake_service_object;
#define FAKE_MANAGER ((SC_HANDLE)&fake_manager_object)
#define FAKE_SERVICE ((SC_HANDLE)&fake_service_object)
static const char SERVICE_NAME[] = "GalaxyCommunication";

static void note(const char *format, ...)
{
    if (!log_file) return;
    EnterCriticalSection(&log_lock);
    va_list arguments;
    va_start(arguments, format);
    vfprintf(log_file, format, arguments);
    va_end(arguments);
    fputc('\n', log_file);
    fflush(log_file);
    LeaveCriticalSection(&log_lock);
}

static int may_log(void)
{
    return InterlockedIncrement(&logged) <= MAX_LOGGED;
}

/* The base name of the module holding an address, for the log. */
static void module_name(const void *address, wchar_t *name, DWORD size)
{
    HMODULE module = NULL;
    wchar_t path[MAX_PATH];
    name[0] = 0;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)address, &module)
        || !GetModuleFileNameW(module, path, MAX_PATH)) {
        wcsncpy(name, L"?", size);
        return;
    }
    const wchar_t *base = wcsrchr(path, L'\\');
    wcsncpy(name, base ? base + 1 : path, size - 1);
    name[size - 1] = 0;
}

/* The entry routing a name, or -1. "auth.gog.com." is "auth.gog.com". */
static int lookup(const char *name)
{
    if (!name) return -1;
    size_t length = strlen(name);
    if (length && name[length - 1] == '.') length--;
    if (!length) return -1;
    for (int i = 0; i < entry_count; i++) {
        const char *pattern = entries[i].name;
        if (pattern[0] == '*' && pattern[1] == '.') {
            size_t suffix = strlen(pattern + 1);  /* ".example.com" */
            if (length > suffix && _strnicmp(name + length - suffix, pattern + 1, suffix) == 0) return i;
        } else if (strlen(pattern) == length && _strnicmp(name, pattern, length) == 0) {
            return i;
        }
    }
    return -1;
}

static int lookup_wide(const wchar_t *name)
{
    if (!name) return -1;
    char narrow[256];
    int size = WideCharToMultiByte(CP_UTF8, 0, name, -1, narrow, sizeof narrow, NULL, NULL);
    return size > 0 ? lookup(narrow) : -1;
}

static void routed(const char *name, int entry)
{
    if (may_log()) note("route %s -> %s", name, entries[entry].address);
}

static INT WSAAPI hook_getaddrinfo(PCSTR node, PCSTR service, const ADDRINFOA *hints, PADDRINFOA *result)
{
    int entry = lookup(node);
    if (entry < 0) return real_getaddrinfo(node, service, hints, result);
    routed(node, entry);
    return real_getaddrinfo(entries[entry].address, service, hints, result);
}

static INT WSAAPI hook_GetAddrInfoW(PCWSTR node, PCWSTR service, const ADDRINFOW *hints, PADDRINFOW *result)
{
    int entry = lookup_wide(node);
    if (entry < 0) return real_GetAddrInfoW(node, service, hints, result);
    char narrow[256];
    WideCharToMultiByte(CP_UTF8, 0, node, -1, narrow, sizeof narrow, NULL, NULL);
    routed(narrow, entry);
    return real_GetAddrInfoW(entries[entry].waddress, service, hints, result);
}

static struct hostent *WSAAPI hook_gethostbyname(const char *name)
{
    int entry = lookup(name);
    if (entry < 0) return real_gethostbyname(name);
    routed(name, entry);
    return real_gethostbyname(entries[entry].address);
}

/* Every-interface binds become loopback binds; others pass unchanged. */
static int WSAAPI hook_bind(SOCKET s, const struct sockaddr *name, int length)
{
    struct sockaddr_in v4;
    struct sockaddr_in6 v6;
    const struct sockaddr *use = name;
    unsigned port = 0;
    if (name && name->sa_family == AF_INET && length >= (int)sizeof v4) {
        memcpy(&v4, name, sizeof v4);
        if (v4.sin_addr.s_addr != htonl(INADDR_ANY)) return real_bind(s, name, length);
        v4.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        port = ntohs(v4.sin_port);
        use = (const struct sockaddr *)&v4;
    } else if (name && name->sa_family == AF_INET6 && length >= (int)sizeof v6) {
        memcpy(&v6, name, sizeof v6);
        if (!IN6_IS_ADDR_UNSPECIFIED(&v6.sin6_addr)) return real_bind(s, name, length);
        v6.sin6_addr = in6addr_loopback;
        port = ntohs(v6.sin6_port);
        use = (const struct sockaddr *)&v6;
    } else {
        return real_bind(s, name, length);
    }
    int result = real_bind(s, use, use == (const struct sockaddr *)&v4 ? (int)sizeof v4 : (int)sizeof v6);
    if (may_log()) {
        int type = 0, size = sizeof type;
        getsockopt(s, SOL_SOCKET, SO_TYPE, (char *)&type, &size);
        wchar_t caller[64];
        module_name(__builtin_return_address(0), caller, 64);
        note("bind %ls %s %s port %u -> loopback%s", caller, type == SOCK_DGRAM ? "UDP" : "TCP",
             use == (const struct sockaddr *)&v4 ? "0.0.0.0" : "[::]", port,
             result == 0 ? "" : " (failed)");
    }
    return result;
}

static void fill_running(LPSERVICE_STATUS status)
{
    memset(status, 0, sizeof *status);
    status->dwServiceType = SERVICE_WIN32_OWN_PROCESS;
    status->dwCurrentState = SERVICE_RUNNING;
    status->dwControlsAccepted = SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN;
}

static SC_HANDLE WINAPI hook_OpenSCManagerA(LPCSTR machine, LPCSTR database, DWORD access)
{
    SC_HANDLE manager = real_OpenSCManagerA(machine, database, access);
    return manager ? manager : FAKE_MANAGER;  /* full access needs an administrator */
}

static SC_HANDLE WINAPI hook_OpenSCManagerW(LPCWSTR machine, LPCWSTR database, DWORD access)
{
    SC_HANDLE manager = real_OpenSCManagerW(machine, database, access);
    return manager ? manager : FAKE_MANAGER;
}

static SC_HANDLE WINAPI hook_OpenServiceA(SC_HANDLE manager, LPCSTR name, DWORD access)
{
    if (name && _stricmp(name, SERVICE_NAME) == 0) {
        note("service %s: answered running", name);
        return FAKE_SERVICE;
    }
    if (manager == FAKE_MANAGER) {
        SetLastError(ERROR_SERVICE_DOES_NOT_EXIST);
        return NULL;
    }
    return real_OpenServiceA(manager, name, access);
}

static SC_HANDLE WINAPI hook_OpenServiceW(SC_HANDLE manager, LPCWSTR name, DWORD access)
{
    if (name && _wcsicmp(name, L"GalaxyCommunication") == 0) {
        note("service %s: answered running", SERVICE_NAME);
        return FAKE_SERVICE;
    }
    if (manager == FAKE_MANAGER) {
        SetLastError(ERROR_SERVICE_DOES_NOT_EXIST);
        return NULL;
    }
    return real_OpenServiceW(manager, name, access);
}

static BOOL WINAPI hook_QueryServiceStatus(SC_HANDLE service, LPSERVICE_STATUS status)
{
    if (service != FAKE_SERVICE) return real_QueryServiceStatus(service, status);
    fill_running(status);
    return TRUE;
}

static BOOL WINAPI hook_QueryServiceStatusEx(SC_HANDLE service, SC_STATUS_TYPE level, LPBYTE buffer,
                                             DWORD size, LPDWORD needed)
{
    if (service != FAKE_SERVICE) return real_QueryServiceStatusEx(service, level, buffer, size, needed);
    if (needed) *needed = sizeof(SERVICE_STATUS_PROCESS);
    if (level != SC_STATUS_PROCESS_INFO || size < sizeof(SERVICE_STATUS_PROCESS)) {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return FALSE;
    }
    SERVICE_STATUS_PROCESS *process = (SERVICE_STATUS_PROCESS *)buffer;
    memset(process, 0, sizeof *process);
    process->dwServiceType = SERVICE_WIN32_OWN_PROCESS;
    process->dwCurrentState = SERVICE_RUNNING;
    process->dwControlsAccepted = SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN;
    return TRUE;
}

static BOOL WINAPI hook_StartServiceA(SC_HANDLE service, DWORD count, LPCSTR *arguments)
{
    return service == FAKE_SERVICE ? TRUE : real_StartServiceA(service, count, arguments);
}

static BOOL WINAPI hook_StartServiceW(SC_HANDLE service, DWORD count, LPCWSTR *arguments)
{
    return service == FAKE_SERVICE ? TRUE : real_StartServiceW(service, count, arguments);
}

static BOOL WINAPI hook_ControlService(SC_HANDLE service, DWORD control, LPSERVICE_STATUS status)
{
    if (service != FAKE_SERVICE) return real_ControlService(service, control, status);
    if (status) fill_running(status);
    return TRUE;
}

static BOOL WINAPI hook_CloseServiceHandle(SC_HANDLE handle)
{
    if (handle == FAKE_SERVICE || handle == FAKE_MANAGER) return TRUE;
    return real_CloseServiceHandle(handle);
}

/* ---- This install's certificate, for Schannel connections to routed names ---- */

static CRITICAL_SECTION sspi_lock;
static SecurityFunctionTableA table_a;
static SecurityFunctionTableW table_w;
static ACQUIRE_CREDENTIALS_HANDLE_FN_A real_AcquireCredentialsHandleA;
static ACQUIRE_CREDENTIALS_HANDLE_FN_W real_AcquireCredentialsHandleW;
static INITIALIZE_SECURITY_CONTEXT_FN_A real_InitializeSecurityContextA;
static INITIALIZE_SECURITY_CONTEXT_FN_W real_InitializeSecurityContextW;
static FREE_CREDENTIALS_HANDLE_FN real_FreeCredentialsHandle;
static DELETE_SECURITY_CONTEXT_FN real_DeleteSecurityContext;
static QUERY_CONTEXT_ATTRIBUTES_FN_A real_QueryContextAttributesA;
static QUERY_CONTEXT_ATTRIBUTES_FN_W real_QueryContextAttributesW;

/* SCH_CREDENTIALS (schannel.h hides it behind SCHANNEL_USE_BLACKLISTS). */
typedef struct {
    DWORD dwVersion, dwCredFormat, cCreds;
    PCCERT_CONTEXT *paCred;
    HCERTSTORE hRootStore;
    DWORD cMappers;
    void **aphMappers;
    DWORD dwSessionLifespan, dwFlags, cTlsParameters;
    void *pTlsParameters;
} sch_credentials;
#ifndef SCH_CREDENTIALS_VERSION
#define SCH_CREDENTIALS_VERSION 5
#endif
typedef union { SCHANNEL_CRED old; sch_credentials current; } credential_data;

/* A caller's Schannel credential and its twin that leaves validation to us. */
static struct { CredHandle original, twin; DWORD flags; int used; } twins[MAX_TRACKED];
/* A handshake on a twin: validated by us when it completes. */
static struct { CtxtHandle context; CredHandle twin; DWORD flags; char host[256]; int used; } contexts[MAX_TRACKED];

static int same_handle(const SecHandle *a, const SecHandle *b)
{
    return a->dwLower == b->dwLower && a->dwUpper == b->dwUpper;
}

static int find_twin(PCredHandle original, CredHandle *twin, DWORD *flags)
{
    int found = 0;
    if (!original) return 0;
    EnterCriticalSection(&sspi_lock);
    for (int i = 0; i < MAX_TRACKED; i++)
        if (twins[i].used && same_handle(&twins[i].original, original)) {
            *twin = twins[i].twin;
            *flags = twins[i].flags;
            found = 1;
            break;
        }
    LeaveCriticalSection(&sspi_lock);
    return found;
}

/* The slot of a tracked handshake, or -1. Copies it out when found. */
static int find_context(PCtxtHandle context, CredHandle *twin, DWORD *flags, char *host)
{
    int found = -1;
    if (!context) return -1;
    EnterCriticalSection(&sspi_lock);
    for (int i = 0; i < MAX_TRACKED; i++)
        if (contexts[i].used && same_handle(&contexts[i].context, context)) {
            if (twin) *twin = contexts[i].twin;
            if (flags) *flags = contexts[i].flags;
            if (host) strcpy(host, contexts[i].host);
            found = i;
            break;
        }
    LeaveCriticalSection(&sspi_lock);
    return found;
}

static int reserve_context(void)
{
    int slot = -1;
    EnterCriticalSection(&sspi_lock);
    for (int i = 0; i < MAX_TRACKED; i++)
        if (!contexts[i].used) {
            contexts[i].used = 2;  /* reserved until the first call returns */
            slot = i;
            break;
        }
    LeaveCriticalSection(&sspi_lock);
    return slot;
}

static int is_schannel_a(const char *package)
{
    return package && (_stricmp(package, UNISP_NAME_A) == 0 || _stricmp(package, "Schannel") == 0);
}

static int is_schannel_w(const wchar_t *package)
{
    return package && (_wcsicmp(package, UNISP_NAME_W) == 0 || _wcsicmp(package, L"Schannel") == 0);
}

/* dwFlags of a SCHANNEL_CRED or SCH_CREDENTIALS, or NULL for anything else. */
static DWORD *credential_flags(void *data)
{
    if (!data) return NULL;
    DWORD version = *(DWORD *)data;
    if (version == SCHANNEL_CRED_VERSION) return &((SCHANNEL_CRED *)data)->dwFlags;
    if (version == SCH_CREDENTIALS_VERSION) return &((sch_credentials *)data)->dwFlags;
    return NULL;
}

/* The twin's authentication data: the caller's, validated by the caller. */
static int twin_data(void *data, credential_data *copy, DWORD *flags)
{
    DWORD *original = credential_flags(data);
    if (!original || (*original & SCH_CRED_MANUAL_CRED_VALIDATION)) return 0;  /* already the caller's job */
    *flags = *original;
    memcpy(copy, data, *(DWORD *)data == SCHANNEL_CRED_VERSION ? sizeof copy->old : sizeof copy->current);
    DWORD *twin = credential_flags(copy);
    *twin = (*twin & ~SCH_CRED_AUTO_CRED_VALIDATION) | SCH_CRED_MANUAL_CRED_VALIDATION;
    return 1;
}

static void remember_twin(PCredHandle original, CredHandle *twin, DWORD flags)
{
    EnterCriticalSection(&sspi_lock);
    int slot = -1;
    for (int i = 0; i < MAX_TRACKED && slot < 0; i++)
        if (!twins[i].used) slot = i;
    if (slot >= 0) {
        twins[slot].original = *original;
        twins[slot].twin = *twin;
        twins[slot].flags = flags;
        twins[slot].used = 1;
    }
    LeaveCriticalSection(&sspi_lock);
    if (slot < 0) real_FreeCredentialsHandle(twin);  /* full: Schannel validates as asked */
}

static SECURITY_STATUS SEC_ENTRY hook_AcquireCredentialsHandleA(
    LPSTR principal, LPSTR package, unsigned long use, void *logon, void *data,
    SEC_GET_KEY_FN get_key, void *argument, PCredHandle credential, PTimeStamp expiry)
{
    SECURITY_STATUS status = real_AcquireCredentialsHandleA(principal, package, use, logon, data, get_key,
                                                            argument, credential, expiry);
    credential_data copy;
    DWORD flags;
    if (status != SEC_E_OK || !(use & SECPKG_CRED_OUTBOUND) || !is_schannel_a(package)
        || !twin_data(data, &copy, &flags))
        return status;
    CredHandle twin;
    TimeStamp twin_expiry;
    if (real_AcquireCredentialsHandleA(principal, package, use, logon, &copy, get_key, argument, &twin,
                                       &twin_expiry) == SEC_E_OK)
        remember_twin(credential, &twin, flags);
    return status;
}

static SECURITY_STATUS SEC_ENTRY hook_AcquireCredentialsHandleW(
    LPWSTR principal, LPWSTR package, unsigned long use, void *logon, void *data,
    SEC_GET_KEY_FN get_key, void *argument, PCredHandle credential, PTimeStamp expiry)
{
    SECURITY_STATUS status = real_AcquireCredentialsHandleW(principal, package, use, logon, data, get_key,
                                                            argument, credential, expiry);
    credential_data copy;
    DWORD flags;
    if (status != SEC_E_OK || !(use & SECPKG_CRED_OUTBOUND) || !is_schannel_w(package)
        || !twin_data(data, &copy, &flags))
        return status;
    CredHandle twin;
    TimeStamp twin_expiry;
    if (real_AcquireCredentialsHandleW(principal, package, use, logon, &copy, get_key, argument, &twin,
                                       &twin_expiry) == SEC_E_OK)
        remember_twin(credential, &twin, flags);
    return status;
}

static SECURITY_STATUS SEC_ENTRY hook_FreeCredentialsHandle(PCredHandle credential)
{
    CredHandle twin;
    int found = 0;
    if (credential) {
        EnterCriticalSection(&sspi_lock);
        for (int i = 0; i < MAX_TRACKED; i++)
            if (twins[i].used && same_handle(&twins[i].original, credential)) {
                twin = twins[i].twin;
                twins[i].used = 0;
                found = 1;
                break;
            }
        LeaveCriticalSection(&sspi_lock);
    }
    if (found) real_FreeCredentialsHandle(&twin);
    return real_FreeCredentialsHandle(credential);
}

static SECURITY_STATUS SEC_ENTRY hook_DeleteSecurityContext(PCtxtHandle context)
{
    int slot = find_context(context, NULL, NULL, NULL);
    if (slot >= 0) {
        EnterCriticalSection(&sspi_lock);
        contexts[slot].used = 0;
        LeaveCriticalSection(&sspi_lock);
    }
    return real_DeleteSecurityContext(context);
}

/* What Schannel's own validation would answer for this certificate. */
static SECURITY_STATUS standard_validation(PCCERT_CONTEXT certificate, const char *host, DWORD flags)
{
    CERT_CHAIN_PARA chain_para = {0};
    chain_para.cbSize = sizeof chain_para;
    DWORD chain_flags = 0;
    if (flags & SCH_CRED_REVOCATION_CHECK_END_CERT) chain_flags |= CERT_CHAIN_REVOCATION_CHECK_END_CERT;
    if (flags & SCH_CRED_REVOCATION_CHECK_CHAIN) chain_flags |= CERT_CHAIN_REVOCATION_CHECK_CHAIN;
    if (flags & SCH_CRED_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT)
        chain_flags |= CERT_CHAIN_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT;
    PCCERT_CHAIN_CONTEXT chain = NULL;
    if (!CertGetCertificateChain(NULL, certificate, NULL, certificate->hCertStore, &chain_para, chain_flags,
                                 NULL, &chain))
        return SEC_E_CERT_UNKNOWN;
    wchar_t server[256];
    MultiByteToWideChar(CP_UTF8, 0, host, -1, server, 256);
    size_t length = wcslen(server);
    if (length && server[length - 1] == L'.') server[length - 1] = 0;
    SSL_EXTRA_CERT_CHAIN_POLICY_PARA ssl = {0};
    ssl.cbSize = sizeof ssl;
    ssl.dwAuthType = AUTHTYPE_SERVER;
    ssl.fdwChecks = (flags & SCH_CRED_NO_SERVERNAME_CHECK) ? SECURITY_FLAG_IGNORE_CERT_CN_INVALID : 0;
    ssl.pwszServerName = server;
    CERT_CHAIN_POLICY_PARA policy = {0};
    policy.cbSize = sizeof policy;
    if (flags & (SCH_CRED_IGNORE_NO_REVOCATION_CHECK | SCH_CRED_IGNORE_REVOCATION_OFFLINE))
        policy.dwFlags = CERT_CHAIN_POLICY_IGNORE_ALL_REV_UNKNOWN_FLAGS;
    policy.pvExtraPolicyPara = &ssl;
    CERT_CHAIN_POLICY_STATUS result = {0};
    result.cbSize = sizeof result;
    BOOL ok = CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_SSL, chain, &policy, &result);
    CertFreeCertificateChain(chain);
    if (!ok) return SEC_E_CERT_UNKNOWN;
    switch ((HRESULT)result.dwError) {
    case 0: return SEC_E_OK;
    case CERT_E_UNTRUSTEDROOT: case CERT_E_CHAINING: return SEC_E_UNTRUSTED_ROOT;
    case CERT_E_EXPIRED: return SEC_E_CERT_EXPIRED;
    case CERT_E_CN_NO_MATCH: return SEC_E_WRONG_PRINCIPAL;
    default: return (SECURITY_STATUS)result.dwError;
    }
}

/* At the end of a handshake on a twin: this install's certificate, or Schannel's answer. */
static SECURITY_STATUS validate(PCtxtHandle context, const char *host, DWORD flags)
{
    PCCERT_CONTEXT certificate = NULL;
    SECURITY_STATUS status = real_QueryContextAttributesA
        ? real_QueryContextAttributesA(context, SECPKG_ATTR_REMOTE_CERT_CONTEXT, &certificate)
        : real_QueryContextAttributesW(context, SECPKG_ATTR_REMOTE_CERT_CONTEXT, &certificate);
    if (status != SEC_E_OK || !certificate) {
        note("certificate for %s: none presented", host);
        return SEC_E_CERT_UNKNOWN;
    }
    BYTE digest[32];
    DWORD size = sizeof digest;
    int ours = CryptHashCertificate2(L"SHA256", 0, NULL, certificate->pbCertEncoded, certificate->cbCertEncoded,
                                     digest, &size)
               && size == sizeof digest && memcmp(digest, pin, sizeof digest) == 0;
    if (ours) {
        status = SEC_E_OK;
    } else {
        status = standard_validation(certificate, host, flags);
    }
    CertFreeCertificateContext(certificate);
    if (may_log())
        note("certificate for %s: %s", host,
             ours ? "this install's, accepted"
                  : status == SEC_E_OK ? "another, valid by the trust store" : "another, refused by the trust store");
    return status;
}

/* The connection's twin, if it goes to a routed name; tracks the handshake. */
static SECURITY_STATUS initialize(PCredHandle credential, PCtxtHandle context, const char *host, int wide,
                                  void *target, unsigned long requirements, unsigned long reserved1,
                                  unsigned long representation, PSecBufferDesc input, unsigned long reserved2,
                                  PCtxtHandle new_context, PSecBufferDesc output, unsigned long *attributes,
                                  PTimeStamp expiry)
{
    CredHandle twin;
    DWORD flags = 0;
    char tracked_host[256];
    int slot = -1;
    PCredHandle use = credential;
    if (!context) {
        if (host && lookup(host) >= 0 && find_twin(credential, &twin, &flags) && (slot = reserve_context()) >= 0) {
            use = &twin;
            strncpy(tracked_host, host, sizeof tracked_host - 1);
            tracked_host[sizeof tracked_host - 1] = 0;
        }
    } else if ((slot = find_context(context, &twin, &flags, tracked_host)) >= 0) {
        use = &twin;
    }
    SECURITY_STATUS status = wide
        ? real_InitializeSecurityContextW(use, context, (SEC_WCHAR *)target, requirements, reserved1, representation,
                                          input, reserved2, new_context, output, attributes, expiry)
        : real_InitializeSecurityContextA(use, context, (SEC_CHAR *)target, requirements, reserved1, representation,
                                          input, reserved2, new_context, output, attributes, expiry);
    if (slot < 0) return status;
    if (!context) {
        EnterCriticalSection(&sspi_lock);
        if (FAILED(status) || !new_context) {
            contexts[slot].used = 0;
        } else {
            contexts[slot].context = *new_context;
            contexts[slot].twin = twin;
            contexts[slot].flags = flags;
            strcpy(contexts[slot].host, tracked_host);
            contexts[slot].used = 1;
        }
        LeaveCriticalSection(&sspi_lock);
    }
    if (status == SEC_E_OK) status = validate(context ? context : new_context, tracked_host, flags);
    return status;
}

static SECURITY_STATUS SEC_ENTRY hook_InitializeSecurityContextA(
    PCredHandle credential, PCtxtHandle context, SEC_CHAR *target, unsigned long requirements,
    unsigned long reserved1, unsigned long representation, PSecBufferDesc input, unsigned long reserved2,
    PCtxtHandle new_context, PSecBufferDesc output, unsigned long *attributes, PTimeStamp expiry)
{
    return initialize(credential, context, target, 0, target, requirements, reserved1, representation, input,
                      reserved2, new_context, output, attributes, expiry);
}

static SECURITY_STATUS SEC_ENTRY hook_InitializeSecurityContextW(
    PCredHandle credential, PCtxtHandle context, SEC_WCHAR *target, unsigned long requirements,
    unsigned long reserved1, unsigned long representation, PSecBufferDesc input, unsigned long reserved2,
    PCtxtHandle new_context, PSecBufferDesc output, unsigned long *attributes, PTimeStamp expiry)
{
    char host[256] = "";
    if (target) WideCharToMultiByte(CP_UTF8, 0, target, -1, host, sizeof host, NULL, NULL);
    return initialize(credential, context, target ? host : NULL, 1, target, requirements, reserved1,
                      representation, input, reserved2, new_context, output, attributes, expiry);
}

static void take_common(FREE_CREDENTIALS_HANDLE_FN free_credentials, DELETE_SECURITY_CONTEXT_FN delete_context)
{
    if (!real_FreeCredentialsHandle) real_FreeCredentialsHandle = free_credentials;
    if (!real_DeleteSecurityContext) real_DeleteSecurityContext = delete_context;
}

static PSecurityFunctionTableA SEC_ENTRY hook_InitSecurityInterfaceA(void)
{
    PSecurityFunctionTableA real = real_InitSecurityInterfaceA();
    if (!real || !pinned) return real;
    EnterCriticalSection(&sspi_lock);
    if (!table_a.dwVersion) {
        table_a = *real;
        real_AcquireCredentialsHandleA = real->AcquireCredentialsHandleA;
        real_InitializeSecurityContextA = real->InitializeSecurityContextA;
        real_QueryContextAttributesA = real->QueryContextAttributesA;
        take_common(real->FreeCredentialsHandle, real->DeleteSecurityContext);
        table_a.AcquireCredentialsHandleA = hook_AcquireCredentialsHandleA;
        table_a.InitializeSecurityContextA = hook_InitializeSecurityContextA;
        table_a.FreeCredentialsHandle = hook_FreeCredentialsHandle;
        table_a.DeleteSecurityContext = hook_DeleteSecurityContext;
        note("Schannel (ANSI): this install's certificate is accepted for routed names");
    }
    LeaveCriticalSection(&sspi_lock);
    return &table_a;
}

static PSecurityFunctionTableW SEC_ENTRY hook_InitSecurityInterfaceW(void)
{
    PSecurityFunctionTableW real = real_InitSecurityInterfaceW();
    if (!real || !pinned) return real;
    EnterCriticalSection(&sspi_lock);
    if (!table_w.dwVersion) {
        table_w = *real;
        real_AcquireCredentialsHandleW = real->AcquireCredentialsHandleW;
        real_InitializeSecurityContextW = real->InitializeSecurityContextW;
        real_QueryContextAttributesW = real->QueryContextAttributesW;
        take_common(real->FreeCredentialsHandle, real->DeleteSecurityContext);
        table_w.AcquireCredentialsHandleW = hook_AcquireCredentialsHandleW;
        table_w.InitializeSecurityContextW = hook_InitializeSecurityContextW;
        table_w.FreeCredentialsHandle = hook_FreeCredentialsHandle;
        table_w.DeleteSecurityContext = hook_DeleteSecurityContext;
        note("Schannel (Unicode): this install's certificate is accepted for routed names");
    }
    LeaveCriticalSection(&sspi_lock);
    return &table_w;
}

/* ---- Import-table patching ---- */

struct target { const char *name; void **real; void *hook; int galaxy_only; };

static FARPROC WINAPI hook_GetProcAddress(HMODULE module, LPCSTR name);

#define TARGET(name, galaxy_only) {#name, (void **)&real_##name, (void *)hook_##name, galaxy_only}
static struct target targets[] = {
    TARGET(getaddrinfo, 0),
    TARGET(GetAddrInfoW, 0),
    TARGET(gethostbyname, 0),
    TARGET(bind, 0),
    TARGET(GetProcAddress, 0),
    TARGET(InitSecurityInterfaceA, 0),
    TARGET(InitSecurityInterfaceW, 0),
    TARGET(OpenSCManagerA, 1),
    TARGET(OpenSCManagerW, 1),
    TARGET(OpenServiceA, 1),
    TARGET(OpenServiceW, 1),
    TARGET(QueryServiceStatus, 1),
    TARGET(QueryServiceStatusEx, 1),
    TARGET(StartServiceA, 1),
    TARGET(StartServiceW, 1),
    TARGET(ControlService, 1),
    TARGET(CloseServiceHandle, 1),
};
#define TARGET_COUNT (sizeof targets / sizeof targets[0])

static FARPROC WINAPI hook_GetProcAddress(HMODULE module, LPCSTR name)
{
    FARPROC result = real_GetProcAddress(module, name);
    if (trace && HIWORD((ULONG_PTR)name) && may_log()) {
        wchar_t caller[64];
        module_name(__builtin_return_address(0), caller, 64);
        note("GetProcAddress %s from %ls%s", name, caller, result ? "" : " (absent)");
    }
    if (!result) return result;
    /* The Galaxy service answer stays with Galaxy64.dll's imports. */
    for (size_t i = 0; i < TARGET_COUNT; i++)
        if (!targets[i].galaxy_only && targets[i].hook != (void *)hook_GetProcAddress
            && *targets[i].real && (void *)result == *targets[i].real)
            return (FARPROC)targets[i].hook;
    return result;
}

static int load_hosts(const wchar_t *path)
{
    FILE *file = path ? _wfopen(path, L"r") : NULL;
    if (!file) return -1;
    char line[4096];
    int count = 0;
    while (fgets(line, sizeof line, file)) {
        char *comment = strchr(line, '#');
        if (comment) *comment = '\0';
        char *save, *address = strtok_s(line, " \t\r\n", &save);
        if (!address) continue;
        for (char *name; (name = strtok_s(NULL, " \t\r\n", &save));) {
            size_t length = strlen(name);
            if (length && name[length - 1] == '.') name[--length] = '\0';
            if (count == MAX_NAMES || length >= sizeof entries[0].name
                || strlen(address) >= sizeof entries[0].address) {
                fclose(file);
                return -2;
            }
            strcpy(entries[count].name, name);
            strcpy(entries[count].address, address);
            MultiByteToWideChar(CP_UTF8, 0, address, -1, entries[count].waddress, 64);
            count++;
        }
    }
    fclose(file);
    return count;
}

/* GWENT_SESSION_CERT_SHA256: 64 hex digits, or unset. */
static int load_pin(void)
{
    char text[128];
    DWORD length = GetEnvironmentVariableA("GWENT_SESSION_CERT_SHA256", text, sizeof text);
    if (!length) return 0;
    if (length != 64) return -4;
    for (int i = 0; i < 32; i++) {
        unsigned value;
        if (!isxdigit((unsigned char)text[2 * i]) || !isxdigit((unsigned char)text[2 * i + 1])
            || sscanf(text + 2 * i, "%2x", &value) != 1)
            return -4;
        pin[i] = (BYTE)value;
    }
    return 1;
}

/* Whether a module path lies below the client folder (MelonLoader's folder
 * and its Mono runtime included); *galaxy for a Galaxy* module. */
static int in_client_dir(const wchar_t *path, size_t length, int *galaxy)
{
    if (length <= module_dir_length || _wcsnicmp(path, module_dir, module_dir_length) != 0) return 0;
    const wchar_t *base = wcsrchr(path, L'\\');
    base = base ? base + 1 : path;
    *galaxy = _wcsnicmp(base, L"Galaxy", 6) == 0;
    return 1;
}

static int in_module_dir(HMODULE module, int *galaxy)
{
    wchar_t path[MAX_PATH];
    DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
    if (!length || length >= MAX_PATH || module == self) return 0;
    return in_client_dir(path, length, galaxy);
}

/* Writes one import slot, keeping its page's execute right if it had one. */
static int write_slot(ULONG_PTR *slot, ULONG_PTR value)
{
    MEMORY_BASIC_INFORMATION page;
    if (!VirtualQuery(slot, &page, sizeof page)) return 0;
    DWORD executable = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    DWORD old;
    if (!VirtualProtect(slot, sizeof *slot, (page.Protect & executable) ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE,
                        &old))
        return 0;
    *slot = value;
    VirtualProtect(slot, sizeof *slot, old, &old);
    return 1;
}

/* One patcher at a time: install's rescan and the loader's notifications. */
static CRITICAL_SECTION patch_lock;

/* Rewrites the module's import-table slots that hold one of the targets.
 * Called with patch_lock held; takes no loader lock, so a load notification
 * (which holds the loader lock) may call it. */
static int patch_module(HMODULE module, int galaxy)
{
    BYTE *base = (BYTE *)module;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
    IMAGE_DATA_DIRECTORY directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress) return 0;
    int patched = 0;
    for (IMAGE_IMPORT_DESCRIPTOR *import = (IMAGE_IMPORT_DESCRIPTOR *)(base + directory.VirtualAddress);
         import->Name; import++) {
        for (IMAGE_THUNK_DATA *slot = (IMAGE_THUNK_DATA *)(base + import->FirstThunk); slot->u1.Function; slot++) {
            for (size_t i = 0; i < TARGET_COUNT; i++) {
                if (!*targets[i].real || (targets[i].galaxy_only && !galaxy)
                    || (void *)slot->u1.Function != *targets[i].real)
                    continue;
                if (write_slot(&slot->u1.Function, (ULONG_PTR)targets[i].hook)) {
                    note("  %s (%s)", targets[i].name, (const char *)(base + import->Name));
                    patched++;
                }
                break;
            }
        }
    }
    return patched;
}

static void resolve_targets(void)
{
    HMODULE ws2 = LoadLibraryW(L"ws2_32.dll");
    HMODULE advapi = LoadLibraryW(L"advapi32.dll");
    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    real_getaddrinfo = (getaddrinfo_fn)(void *)GetProcAddress(ws2, "getaddrinfo");
    real_GetAddrInfoW = (GetAddrInfoW_fn)(void *)GetProcAddress(ws2, "GetAddrInfoW");
    real_gethostbyname = (gethostbyname_fn)(void *)GetProcAddress(ws2, "gethostbyname");
    real_bind = (bind_fn)(void *)GetProcAddress(ws2, "bind");
    real_GetProcAddress = (GetProcAddress_fn)(void *)GetProcAddress(kernel, "GetProcAddress");
    real_OpenSCManagerA = (OpenSCManagerA_fn)(void *)GetProcAddress(advapi, "OpenSCManagerA");
    real_OpenSCManagerW = (OpenSCManagerW_fn)(void *)GetProcAddress(advapi, "OpenSCManagerW");
    real_OpenServiceA = (OpenServiceA_fn)(void *)GetProcAddress(advapi, "OpenServiceA");
    real_OpenServiceW = (OpenServiceW_fn)(void *)GetProcAddress(advapi, "OpenServiceW");
    real_QueryServiceStatus = (QueryServiceStatus_fn)(void *)GetProcAddress(advapi, "QueryServiceStatus");
    real_QueryServiceStatusEx = (QueryServiceStatusEx_fn)(void *)GetProcAddress(advapi, "QueryServiceStatusEx");
    real_StartServiceA = (StartServiceA_fn)(void *)GetProcAddress(advapi, "StartServiceA");
    real_StartServiceW = (StartServiceW_fn)(void *)GetProcAddress(advapi, "StartServiceW");
    real_ControlService = (ControlService_fn)(void *)GetProcAddress(advapi, "ControlService");
    real_CloseServiceHandle = (CloseServiceHandle_fn)(void *)GetProcAddress(advapi, "CloseServiceHandle");
    if (pinned) {
        /* secur32.dll forwards these to sspicli.dll; both names reach the same code. */
        HMODULE secur32 = LoadLibraryW(L"secur32.dll");
        real_InitSecurityInterfaceA = (INIT_SECURITY_INTERFACE_A)(void *)GetProcAddress(secur32, "InitSecurityInterfaceA");
        real_InitSecurityInterfaceW = (INIT_SECURITY_INTERFACE_W)(void *)GetProcAddress(secur32, "InitSecurityInterfaceW");
    }
}

/* ---- Client modules loaded after install (ntdll's DLL load notifications) ---- */

/* UNICODE_STRING and LDR_DLL_LOADED_NOTIFICATION_DATA, as ntdll passes them. */
typedef struct { USHORT length, maximum; PWSTR buffer; } counted_string;
typedef struct {
    ULONG flags;
    const counted_string *full_name, *base_name;
    void *base;
    ULONG size;
} dll_notification;
#define DLL_LOADED 1  /* LDR_DLL_NOTIFICATION_REASON_LOADED */
typedef VOID (CALLBACK *dll_notify_fn)(ULONG, const dll_notification *, PVOID);
typedef LONG (NTAPI *LdrRegisterDllNotification_fn)(ULONG, dll_notify_fn, PVOID, PVOID *);
typedef LONG (NTAPI *LdrUnregisterDllNotification_fn)(PVOID);
static void *notification_cookie;

/* The loader calls this under its lock: only the import-table patch runs here
 * (VirtualProtect and pointer writes), so a module loaded later from the
 * client folder (the Wwise engine AkSoundEngine.dll loads after the plugin's
 * last rescan) has its listeners on loopback before its code runs. */
static VOID CALLBACK loaded_later(ULONG reason, const dll_notification *data, PVOID context)
{
    (void)context;
    if (reason != DLL_LOADED || !data || !data->base || !data->full_name || !data->full_name->buffer) return;
    size_t length = data->full_name->length / sizeof(wchar_t);
    if (length >= MAX_PATH || (HMODULE)data->base == self) return;
    wchar_t path[MAX_PATH];
    memcpy(path, data->full_name->buffer, length * sizeof(wchar_t));
    path[length] = 0;
    int galaxy = 0;
    if (!in_client_dir(path, length, &galaxy)) return;
    EnterCriticalSection(&patch_lock);
    int patched = patch_module((HMODULE)data->base, galaxy);
    LeaveCriticalSection(&patch_lock);
    if (patched) note("patched %d imports in %ls (loaded later)", patched, path);
}

static void watch_loads(void)
{
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    LdrRegisterDllNotification_fn watch =
        (LdrRegisterDllNotification_fn)(void *)GetProcAddress(ntdll, "LdrRegisterDllNotification");
    if (watch && watch(0, loaded_later, NULL, &notification_cookie) >= 0) return;
    notification_cookie = NULL;
    note("no DLL load notifications: client modules loaded later stay unpatched until the next install");
}

/*
 * hosts_file: the routed names. directory: modules loaded from below it are
 * patched. preload: library paths separated by ';' to load first (Galaxy64.dll
 * loads late otherwise). log: a file to append to, or NULL. Client modules
 * loaded after the first call are patched as they load; calling again
 * rescans (a safety net where the load notification is missing); the hosts
 * file is read once. The environment
 * variable GWENT_SESSION_CERT_SHA256 names this install's certificate.
 * Returns the number of slots patched by this call, or a negative error:
 * -1 hosts file unreadable, -2 hosts file too large, -3 bad directory,
 * -4 GWENT_SESSION_CERT_SHA256 is not 64 hex digits. After an error every
 * later call returns the same error.
 */
__declspec(dllexport) int session_net_install(const wchar_t *hosts_file, const wchar_t *directory,
                                              const wchar_t *preload, const wchar_t *log)
{
    static int failure;
    if (failure) return failure;
    if (!started) {
        started = 1;
        InitializeCriticalSection(&log_lock);
        InitializeCriticalSection(&sspi_lock);
        InitializeCriticalSection(&patch_lock);
        if (log && *log) log_file = _wfopen(log, L"a");
    }
    if (!loaded) {
        int count = load_hosts(hosts_file);
        if (count < 0) {
            note("cannot read the hosts file (%d)", count);
            return failure = count;
        }
        entry_count = count;
        if (!directory || !GetFullPathNameW(directory, MAX_PATH, module_dir, NULL)) {
            note("bad client directory");
            return failure = -3;
        }
        int pin_state = load_pin();
        if (pin_state < 0) {
            note("GWENT_SESSION_CERT_SHA256 is not a SHA-256 in hex");
            return failure = pin_state;
        }
        pinned = pin_state;
        char flag[8];
        trace = GetEnvironmentVariableA("GWENT_SESSION_NET_TRACE", flag, sizeof flag) && flag[0] == '1';
        module_dir_length = wcslen(module_dir);
        if (module_dir_length && module_dir[module_dir_length - 1] != L'\\' && module_dir_length + 1 < MAX_PATH) {
            module_dir[module_dir_length++] = L'\\';
            module_dir[module_dir_length] = 0;
        }
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           (LPCWSTR)(void *)session_net_install, &self);
        resolve_targets();
        loaded = 1;
        note("session_net: %d names routed; modules under %ls; %s", count, module_dir,
             pinned ? "this install's certificate pinned" : "no certificate pinned");
    }
    if (preload && *preload) {
        wchar_t list[4096];
        wcsncpy(list, preload, 4095);
        list[4095] = 0;
        wchar_t *save;
        for (wchar_t *path = wcstok_s(list, L";", &save); path; path = wcstok_s(NULL, L";", &save))
            if (!LoadLibraryW(path)) note("preload failed (%lu): %ls", GetLastError(), path);
    }
    /* From here on the loader reports each new module; the scan below covers
     * those already loaded (a module both see is patched once). */
    static int watching;
    if (!watching) {
        watching = 1;
        watch_loads();
    }
    HMODULE modules[1024];
    DWORD needed;
    if (!EnumProcessModules(GetCurrentProcess(), modules, sizeof modules, &needed)) return 0;
    int total = 0;
    for (DWORD i = 0; i < needed / sizeof(HMODULE) && i < 1024; i++) {
        int galaxy = 0;
        if (!in_module_dir(modules[i], &galaxy)) continue;
        EnterCriticalSection(&patch_lock);
        int patched = patch_module(modules[i], galaxy);
        LeaveCriticalSection(&patch_lock);
        if (patched) {
            wchar_t name[MAX_PATH];
            GetModuleFileNameW(modules[i], name, MAX_PATH);
            note("patched %d imports in %ls", patched, name);
        }
        total += patched;
    }
    return total;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance;
    /* Unloaded with FreeLibrary (not at process exit): no callback into freed code. */
    if (reason == DLL_PROCESS_DETACH && !reserved && notification_cookie) {
        LdrUnregisterDllNotification_fn unwatch = (LdrUnregisterDllNotification_fn)(void *)GetProcAddress(
            GetModuleHandleW(L"ntdll.dll"), "LdrUnregisterDllNotification");
        if (unwatch) unwatch(notification_cookie);
        notification_cookie = NULL;
    }
    return TRUE;
}
