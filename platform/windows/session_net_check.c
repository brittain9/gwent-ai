/*
 * Checks dist/session_net.dll in an ordinary process, without the game
 * (scripts/tests/test_session_net_windows.py builds and runs it):
 *
 *   session_net_check DLL HOSTS LOG [galaxy]
 *
 * The program imports the functions the client's modules import. It loads
 * the library, routes its own folder's modules and checks that listed names
 * resolve to their addresses (with or without a trailing dot), others to the
 * system resolver, that sockets bound to every interface land on loopback, and (named
 * Galaxy*.exe, last argument "galaxy") that the GalaxyCommunication service
 * reads as running while other services stay real. Prints PASS or the first
 * failure, and exits 0 only on PASS.
 */
#define _WIN32_WINNT 0x0601
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

typedef int (*install_fn)(const wchar_t *, const wchar_t *, const wchar_t *, const wchar_t *);
typedef INT (WSAAPI *getaddrinfo_fn)(PCSTR, PCSTR, const ADDRINFOA *, PADDRINFOA *);

static int failures;

static void check(int condition, const char *what)
{
    if (!condition) {
        printf("FAIL: %s\n", what);
        failures++;
    }
}

static int resolves_to(getaddrinfo_fn resolve, const char *name, const char *expected)
{
    ADDRINFOA hints = {0}, *result = NULL;
    hints.ai_family = AF_INET;
    if (resolve(name, "443", &hints, &result) != 0 || !result) return 0;
    char text[64];
    inet_ntop(AF_INET, &((struct sockaddr_in *)result->ai_addr)->sin_addr, text, sizeof text);
    freeaddrinfo(result);
    return expected ? strcmp(text, expected) == 0 : 1;
}

/* Its own function: gcc may keep an import address it loaded before the
 * install in a register, which the client's modules do not do. */
__attribute__((noinline)) static getaddrinfo_fn look_up_getaddrinfo(void)
{
    return (getaddrinfo_fn)(void *)GetProcAddress(GetModuleHandleW(L"ws2_32.dll"), "getaddrinfo");
}

int wmain(int argc, wchar_t **argv)
{
    if (argc < 4) {
        printf("usage: session_net_check DLL HOSTS LOG [galaxy]\n");
        return 2;
    }
    int galaxy = argc > 4 && wcscmp(argv[4], L"galaxy") == 0;
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    wchar_t directory[MAX_PATH];
    GetModuleFileNameW(NULL, directory, MAX_PATH);
    *wcsrchr(directory, L'\\') = 0;

    HMODULE library = LoadLibraryW(argv[1]);
    if (!library) {
        printf("FAIL: cannot load %ls (%lu)\n", argv[1], GetLastError());
        return 1;
    }
    install_fn install = (install_fn)(void *)GetProcAddress(library, "session_net_install");
    check(install != NULL, "session_net_install exported");
    if (!install) return 1;
    int patched = install(argv[2], directory, NULL, argv[3]);
    printf("patched %d imports\n", patched);
    check(patched > 0, "imports patched");
    check(install(argv[2], directory, NULL, argv[3]) == 0, "a second install patches nothing new");

    check(resolves_to(getaddrinfo, "auth.gog.com", "127.0.0.1"), "getaddrinfo routes a listed name");
    check(resolves_to(getaddrinfo, "AUTH.GOG.COM", "127.0.0.1"), "names match without case");
    check(resolves_to(getaddrinfo, "unlisted-host.gog.com", "127.0.0.1"), "a wildcard routes subdomains");
    check(resolves_to(getaddrinfo, "localhost", NULL), "other names reach the system resolver");
    check(!resolves_to(getaddrinfo, "gog.com.invalid", "127.0.0.1"), "a wildcard needs the suffix");
    check(resolves_to(getaddrinfo, "auth.gog.com.", "127.0.0.1"), "a trailing dot routes a listed name");
    check(resolves_to(getaddrinfo, "unlisted-host.gog.com.", "127.0.0.1"), "a trailing dot routes a wildcard");

    /* Every-interface binds land on loopback; a specific address stays. */
    for (int type = SOCK_STREAM; type <= SOCK_DGRAM; type++) {
        SOCKET s = socket(AF_INET, type, 0);
        struct sockaddr_in any = {0}, bound = {0};
        any.sin_family = AF_INET;
        any.sin_addr.s_addr = htonl(INADDR_ANY);
        int size = sizeof bound;
        check(bind(s, (struct sockaddr *)&any, sizeof any) == 0
              && getsockname(s, (struct sockaddr *)&bound, &size) == 0
              && bound.sin_addr.s_addr == htonl(INADDR_LOOPBACK), "a 0.0.0.0 bind lands on 127.0.0.1");
        closesocket(s);
        SOCKET s6 = socket(AF_INET6, type, 0);
        if (s6 != INVALID_SOCKET) {
            struct sockaddr_in6 any6 = {0}, bound6 = {0};
            any6.sin6_family = AF_INET6;
            size = sizeof bound6;
            check(bind(s6, (struct sockaddr *)&any6, sizeof any6) == 0
                  && getsockname(s6, (struct sockaddr *)&bound6, &size) == 0
                  && IN6_IS_ADDR_LOOPBACK(&bound6.sin6_addr), "a [::] bind lands on [::1]");
            closesocket(s6);
        }
    }
    {
        SOCKET s = socket(AF_INET, SOCK_STREAM, 0);
        struct sockaddr_in loop = {0}, bound = {0};
        loop.sin_family = AF_INET;
        loop.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        int size = sizeof bound;
        check(bind(s, (struct sockaddr *)&loop, sizeof loop) == 0
              && getsockname(s, (struct sockaddr *)&bound, &size) == 0
              && bound.sin_addr.s_addr == htonl(INADDR_LOOPBACK), "a loopback bind stays");
        closesocket(s);
    }

    struct hostent *host = gethostbyname("seawolf-deck.gog.com");
    check(host && host->h_addrtype == AF_INET
          && memcmp(host->h_addr_list[0], "\x7f\x00\x00\x01", 4) == 0, "gethostbyname routes a listed name");

    ADDRINFOW whints = {0}, *wresult = NULL;
    whints.ai_family = AF_INET;
    int wrc = GetAddrInfoW(L"users.gog.com", L"443", &whints, &wresult);
    check(wrc == 0 && wresult
          && ((struct sockaddr_in *)wresult->ai_addr)->sin_addr.s_addr == htonl(INADDR_LOOPBACK),
          "GetAddrInfoW routes a listed name");
    if (wresult) FreeAddrInfoW(wresult);

    getaddrinfo_fn dynamic = look_up_getaddrinfo();
    check(dynamic && resolves_to(dynamic, "presence.gog.com", "127.0.0.1"),
          "getaddrinfo looked up at run time routes too");

    SC_HANDLE manager = OpenSCManagerA(NULL, NULL, SC_MANAGER_CONNECT);
    check(manager != NULL, "OpenSCManagerA");
    SC_HANDLE service = OpenServiceA(manager, "GalaxyCommunication", SERVICE_QUERY_STATUS | SERVICE_START);
    if (galaxy) {
        SERVICE_STATUS status = {0};
        check(service != NULL, "the Galaxy service opens");
        check(QueryServiceStatus(service, &status) && status.dwCurrentState == SERVICE_RUNNING,
              "the Galaxy service reads as running");
        check(StartServiceA(service, 0, NULL), "starting the Galaxy service succeeds");
        check(CloseServiceHandle(service), "closing the Galaxy service handle");
        SC_HANDLE real = OpenServiceA(manager, "EventLog", SERVICE_QUERY_STATUS);
        check(real != NULL && real != service, "other services stay real");
        if (real) {
            check(QueryServiceStatus(real, &status), "a real service answers");
            CloseServiceHandle(real);
        }
    } else {
        /* Outside Galaxy* modules the service manager stays untouched. */
        if (service) CloseServiceHandle(service);
    }
    void *real_open = (void *)GetProcAddress(GetModuleHandleW(L"advapi32.dll"), "OpenServiceA");
    if (galaxy)
        check((void *)OpenServiceA != real_open, "Galaxy modules get the service answer");
    else
        check((void *)OpenServiceA == real_open, "the service manager stays real outside Galaxy modules");
    if (manager) CloseServiceHandle(manager);
    if (!failures) printf("PASS\n");
    return failures ? 1 : 0;
}
