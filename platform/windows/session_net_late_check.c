/*
 * Checks that dist/session_net.dll patches a client module loaded after its
 * install, as the client loads the Wwise engine (AkSoundEngine.dll) after the
 * plugin's last rescan (the Windows session tests build and run it). One
 * source, two builds:
 *
 *   -DLATE_MODULE -shared   late.dll, whose bind_any() binds a UDP socket to
 *                           0.0.0.0 and returns the address it landed on, and
 *                           whose bind_import() returns its import of bind
 *   (default)               session_net_late_check DLL HOSTS LOG CLIENT MODULE patched|unpatched
 *
 * The program installs session_net for the folder CLIENT, then loads MODULE.
 * "patched" (MODULE under CLIENT): its 0.0.0.0 bind lands on 127.0.0.1.
 * "unpatched" (MODULE elsewhere): its bind import is still ws2_32's own; no
 * socket is bound to every interface, so Windows Firewall has nothing to ask.
 * Prints PASS or the failure, and exits 0 only on PASS.
 */
#define _WIN32_WINNT 0x0601
#include <winsock2.h>
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

#ifdef LATE_MODULE

/* The address a 0.0.0.0 UDP bind landed on (network order), or INADDR_NONE. */
__declspec(dllexport) unsigned long bind_any(void)
{
    SOCKET s = socket(AF_INET, SOCK_DGRAM, 0);
    if (s == INVALID_SOCKET) return INADDR_NONE;
    struct sockaddr_in any = {0}, bound = {0};
    any.sin_family = AF_INET;
    any.sin_addr.s_addr = htonl(INADDR_ANY);
    int size = sizeof bound;
    unsigned long result = INADDR_NONE;
    if (bind(s, (struct sockaddr *)&any, sizeof any) == 0 && getsockname(s, (struct sockaddr *)&bound, &size) == 0)
        result = bound.sin_addr.s_addr;
    closesocket(s);
    return result;
}

/* What this module's import slot for bind holds (gcc reads it through __imp_bind). */
__declspec(dllexport) void *bind_import(void)
{
    return (void *)bind;
}

#else

typedef int (*install_fn)(const wchar_t *, const wchar_t *, const wchar_t *, const wchar_t *);
typedef unsigned long (*bind_any_fn)(void);
typedef void *(*bind_import_fn)(void);

int wmain(int argc, wchar_t **argv)
{
    if (argc < 7) {
        printf("usage: session_net_late_check DLL HOSTS LOG CLIENT MODULE patched|unpatched\n");
        return 2;
    }
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    HMODULE library = LoadLibraryW(argv[1]);
    install_fn install = library ? (install_fn)(void *)GetProcAddress(library, "session_net_install") : NULL;
    if (!install) {
        printf("FAIL: cannot load %ls (%lu)\n", argv[1], GetLastError());
        return 1;
    }
    int patched = install(argv[2], argv[4], NULL, argv[3]);
    if (patched < 0) {
        printf("FAIL: install returned %d\n", patched);
        return 1;
    }
    if (GetModuleHandleW(argv[5])) {
        printf("FAIL: the module was loaded before the install\n");
        return 1;
    }
    HMODULE module = LoadLibraryW(argv[5]);
    bind_any_fn bind_any = module ? (bind_any_fn)(void *)GetProcAddress(module, "bind_any") : NULL;
    bind_import_fn bind_import = module ? (bind_import_fn)(void *)GetProcAddress(module, "bind_import") : NULL;
    if (!bind_any || !bind_import) {
        printf("FAIL: cannot load %ls (%lu)\n", argv[5], GetLastError());
        return 1;
    }
    void *real = (void *)GetProcAddress(GetModuleHandleW(L"ws2_32.dll"), "bind");
    void *imported = bind_import();
    if (wcscmp(argv[6], L"unpatched") == 0) {
        if (imported != real) {
            printf("FAIL: a module outside the client folder was patched\n");
            return 1;
        }
        printf("PASS\n");
        return 0;
    }
    if (imported == real) {
        printf("FAIL: the module's bind import is ws2_32's own\n");
        return 1;
    }
    unsigned long address = bind_any();
    struct in_addr shown;
    shown.s_addr = address;
    printf("bound to %s\n", inet_ntoa(shown));
    if (address != htonl(INADDR_LOOPBACK)) {
        printf("FAIL: a 0.0.0.0 bind did not land on 127.0.0.1\n");
        return 1;
    }
    printf("PASS\n");
    return 0;
}

#endif
