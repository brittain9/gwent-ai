/*
 * Checks a Play session's networking from inside Wine, through the same
 * ws2_32 calls the client makes (scripts/local_session.py --smoke):
 *   1. auth.gog.com resolves to 127.0.0.1;
 *   2. it accepts a TCP connection there (the local nginx);
 *   3. a public address is unreachable.
 * Prints one PASS or FAIL line per check; exits 0 only if all pass.
 *
 * Build: x86_64-w64-mingw32-gcc netprobe.c -o netprobe.exe -lws2_32
 */
#include <winsock2.h>
#include <ws2tcpip.h>
#include <stdio.h>
#include <string.h>

static int failures;

static void report(int ok, const char *check)
{
    printf("%s: %s\n", ok ? "PASS" : "FAIL", check);
    failures += !ok;
}

static int can_connect(const struct sockaddr *address, int length)
{
    SOCKET s = socket(address->sa_family, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return 0;
    /* A blocked route fails at once; an open one answers within a second. */
    u_long nonblocking = 1;
    ioctlsocket(s, FIONBIO, &nonblocking);
    int ok = connect(s, address, length) == 0;
    if (!ok && WSAGetLastError() == WSAEWOULDBLOCK) {
        fd_set writable, failed;
        FD_ZERO(&writable); FD_SET(s, &writable);
        FD_ZERO(&failed); FD_SET(s, &failed);
        struct timeval timeout = {1, 0};
        ok = select(0, NULL, &writable, &failed, &timeout) > 0 && FD_ISSET(s, &writable);
    }
    closesocket(s);
    return ok;
}

int main(void)
{
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa)) {
        puts("FAIL: WSAStartup");
        return 1;
    }
    struct addrinfo hints = {0}, *result = NULL;
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    int resolved = getaddrinfo("auth.gog.com", "443", &hints, &result) == 0;
    char text[INET_ADDRSTRLEN] = "";
    if (resolved)
        inet_ntop(AF_INET, &((struct sockaddr_in *)result->ai_addr)->sin_addr, text, sizeof text);
    report(resolved && !strcmp(text, "127.0.0.1"), "auth.gog.com resolves to 127.0.0.1 in Wine");
    report(resolved && can_connect(result->ai_addr, (int)result->ai_addrlen),
           "auth.gog.com:443 accepts connections in Wine");
    if (result) freeaddrinfo(result);

    struct sockaddr_in public_address = {0};
    public_address.sin_family = AF_INET;
    public_address.sin_port = htons(443);
    inet_pton(AF_INET, "1.1.1.1", &public_address.sin_addr);
    report(!can_connect((struct sockaddr *)&public_address, sizeof public_address),
           "1.1.1.1:443 is unreachable from Wine");
    WSACleanup();
    return failures != 0;
}
