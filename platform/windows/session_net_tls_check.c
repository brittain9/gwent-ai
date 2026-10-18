/*
 * Checks session_net.dll's certificate acceptance in an ordinary process,
 * without the game (the Windows session tests build and run it): a TLS
 * handshake through Schannel exactly as Galaxy64.dll's libcurl
 * 7.57 makes one (InitSecurityInterfaceA fetched with GetProcAddress, an
 * outbound credential with SCH_CRED_AUTO_CRED_VALIDATION and
 * SCH_CRED_REVOCATION_CHECK_CHAIN, the server name as the target).
 *
 *   session_net_tls_check DLL|- HOSTS LOG PORT TARGET
 *
 * DLL "-" skips session_net, for the baseline. Connects to 127.0.0.1:PORT.
 * Prints "HANDSHAKE OK" or "HANDSHAKE FAILED 0x..." and exits 0 only on OK;
 * 3 if session_net_install fails.
 */
#define _WIN32_WINNT 0x0601
#define SECURITY_WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <wincrypt.h>
#include <sspi.h>
#include <schannel.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

typedef int (*install_fn)(const wchar_t *, const wchar_t *, const wchar_t *, const wchar_t *);

/* Its own function: gcc may keep an import address it loaded before the
 * install in a register, which the client's modules do not do. */
__attribute__((noinline)) static INIT_SECURITY_INTERFACE_A look_up_interface(void)
{
    HMODULE secur32 = LoadLibraryW(L"secur32.dll");
    return (INIT_SECURITY_INTERFACE_A)(void *)GetProcAddress(secur32, "InitSecurityInterfaceA");
}

static int send_all(SOCKET s, const char *data, int length)
{
    while (length > 0) {
        int sent = send(s, data, length, 0);
        if (sent <= 0) return 0;
        data += sent;
        length -= sent;
    }
    return 1;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc < 6) {
        printf("usage: session_net_tls_check DLL|- HOSTS LOG PORT TARGET\n");
        return 2;
    }
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    wchar_t directory[MAX_PATH];
    GetModuleFileNameW(NULL, directory, MAX_PATH);
    *wcsrchr(directory, L'\\') = 0;
    if (wcscmp(argv[1], L"-") != 0) {
        HMODULE library = LoadLibraryW(argv[1]);
        install_fn install = library ? (install_fn)(void *)GetProcAddress(library, "session_net_install") : NULL;
        int patched = install ? install(argv[2], directory, NULL, argv[3]) : -100;
        printf("install: %d\n", patched);
        if (patched < 0) return 3;
    }
    char target[256];
    WideCharToMultiByte(CP_UTF8, 0, argv[5], -1, target, sizeof target, NULL, NULL);

    /* As curl_sspi.c: the interface by name from secur32.dll. */
    INIT_SECURITY_INTERFACE_A init = look_up_interface();
    PSecurityFunctionTableA sspi = init ? init() : NULL;
    if (!sspi) {
        printf("HANDSHAKE FAILED no SSPI\n");
        return 1;
    }

    /* As curl 7.57's schannel_connect_step1 with peer and host verification on. */
    SCHANNEL_CRED schannel = {0};
    schannel.dwVersion = SCHANNEL_CRED_VERSION;
    schannel.dwFlags = SCH_CRED_AUTO_CRED_VALIDATION | SCH_CRED_REVOCATION_CHECK_CHAIN;
    schannel.grbitEnabledProtocols = SP_PROT_TLS1_2_CLIENT;
    CredHandle credential;
    TimeStamp expiry;
    SECURITY_STATUS status = sspi->AcquireCredentialsHandleA(NULL, (SEC_CHAR *)UNISP_NAME_A, SECPKG_CRED_OUTBOUND,
                                                             NULL, &schannel, NULL, NULL, &credential, &expiry);
    if (status != SEC_E_OK) {
        printf("HANDSHAKE FAILED AcquireCredentialsHandle 0x%08lx\n", (unsigned long)status);
        return 1;
    }

    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in server = {0};
    server.sin_family = AF_INET;
    server.sin_port = htons((u_short)_wtoi(argv[4]));
    server.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(s, (struct sockaddr *)&server, sizeof server) != 0) {
        printf("HANDSHAKE FAILED connect %d\n", WSAGetLastError());
        return 1;
    }

    unsigned long requirements = ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT | ISC_REQ_CONFIDENTIALITY
                                 | ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_STREAM;
    unsigned long attributes;
    CtxtHandle context;
    SecBuffer out = {0, SECBUFFER_TOKEN, NULL};
    SecBufferDesc out_desc = {SECBUFFER_VERSION, 1, &out};
    status = sspi->InitializeSecurityContextA(&credential, NULL, target, requirements, 0, 0, NULL, 0, &context,
                                              &out_desc, &attributes, &expiry);
    if (out.cbBuffer) {
        send_all(s, out.pvBuffer, out.cbBuffer);
        sspi->FreeContextBuffer(out.pvBuffer);
    }
    static char buffer[65536];
    int filled = 0;
    while (status == SEC_I_CONTINUE_NEEDED || status == SEC_E_INCOMPLETE_MESSAGE) {
        if (filled == 0 || status == SEC_E_INCOMPLETE_MESSAGE) {
            int got = recv(s, buffer + filled, sizeof buffer - filled, 0);
            if (got <= 0) {
                printf("HANDSHAKE FAILED server closed\n");
                return 1;
            }
            filled += got;
        }
        SecBuffer in[2] = {{(unsigned long)filled, SECBUFFER_TOKEN, buffer}, {0, SECBUFFER_EMPTY, NULL}};
        SecBufferDesc in_desc = {SECBUFFER_VERSION, 2, in};
        out.cbBuffer = 0;
        out.pvBuffer = NULL;
        out.BufferType = SECBUFFER_TOKEN;
        status = sspi->InitializeSecurityContextA(&credential, &context, target, requirements, 0, 0, &in_desc, 0,
                                                  NULL, &out_desc, &attributes, &expiry);
        if (out.cbBuffer && out.pvBuffer) {
            if (status == SEC_E_OK || status == SEC_I_CONTINUE_NEEDED) send_all(s, out.pvBuffer, out.cbBuffer);
            sspi->FreeContextBuffer(out.pvBuffer);
        }
        if (status == SEC_E_INCOMPLETE_MESSAGE) continue;
        if (in[1].BufferType == SECBUFFER_EXTRA && in[1].cbBuffer) {
            memmove(buffer, buffer + filled - in[1].cbBuffer, in[1].cbBuffer);
            filled = in[1].cbBuffer;
        } else {
            filled = 0;
        }
    }
    sspi->DeleteSecurityContext(&context);
    sspi->FreeCredentialsHandle(&credential);
    closesocket(s);
    if (status != SEC_E_OK) {
        printf("HANDSHAKE FAILED 0x%08lx\n", (unsigned long)status);
        return 1;
    }
    printf("HANDSHAKE OK\n");
    return 0;
}
