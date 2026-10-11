/*
 * Private name and port routing for Wine in a macOS Play session, the
 * counterpart of the /etc/hosts that scripts/run-local.sh bind-mounts on
 * Linux. scripts/local_session.py loads it into every Wine process with
 * DYLD_INSERT_LIBRARIES; Wine's ws2_32 and wineserver use the host's
 * resolver and sockets, so this covers the client, its Mono runtime and
 * the native Galaxy library alike.
 *
 *   GWENT_HOSTS_FILE  hosts-file syntax (.runtime/hosts): the listed names
 *                     resolve to their listed addresses.
 *   GWENT_HTTPS_PORT  connections to 127.0.0.1:443 go to this loopback port.
 *                     macOS reserves ports below 1024 on loopback for root,
 *                     so the session's nginx listens here instead.
 *
 * Other names fall through to the system resolver, which the session's
 * sandbox (platform/macos/session.sb) denies, as it denies every
 * non-loopback address.
 */
#include <arpa/inet.h>
#include <netdb.h>
#include <netinet/in.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/socket.h>

#define MAX_NAMES 64

static struct { char name[256]; char address[64]; } entries[MAX_NAMES];
static int entry_count;
static in_port_t https_port;
static pthread_once_t loaded = PTHREAD_ONCE_INIT;

static void fail(const char *message, const char *detail)
{
    fprintf(stderr, "session_net: %s (%s)\n", message, detail ? detail : "unset");
    abort();
}

static void load(void)
{
    const char *path = getenv("GWENT_HOSTS_FILE");
    FILE *file = path ? fopen(path, "r") : NULL;
    if (!file) fail("cannot read GWENT_HOSTS_FILE", path);
    char line[4096];
    while (fgets(line, sizeof line, file)) {
        char *comment = strchr(line, '#');
        if (comment) *comment = '\0';
        char *save, *address = strtok_r(line, " \t\r\n", &save);
        if (!address) continue;
        for (char *name; (name = strtok_r(NULL, " \t\r\n", &save));) {
            if (entry_count == MAX_NAMES || strlen(name) >= sizeof entries[0].name
                || strlen(address) >= sizeof entries[0].address)
                fail("GWENT_HOSTS_FILE exceeds the supported size", path);
            strcpy(entries[entry_count].name, name);
            strcpy(entries[entry_count].address, address);
            entry_count++;
        }
    }
    fclose(file);
    const char *port = getenv("GWENT_HTTPS_PORT");
    char *end;
    long value = port ? strtol(port, &end, 10) : 0;
    if (!port || *end || value < 1024 || value > 65535) fail("invalid GWENT_HTTPS_PORT", port);
    https_port = htons((in_port_t)value);
}

/* The listed address for name, or NULL. Names compare like DNS names:
   case-insensitive, with or without the root's trailing dot. */
static const char *lookup(const char *name)
{
    if (!name) return NULL;
    pthread_once(&loaded, load);
    size_t length = strlen(name);
    if (length && name[length - 1] == '.') length--;
    for (int i = 0; i < entry_count; i++)
        if (strlen(entries[i].name) == length && !strncasecmp(entries[i].name, name, length))
            return entries[i].address;
    return NULL;
}

static int session_getaddrinfo(const char *node, const char *service,
                               const struct addrinfo *hints, struct addrinfo **result)
{
    const char *address = lookup(node);
    return getaddrinfo(address ? address : node, service, hints, result);
}

static struct hostent *session_gethostbyname(const char *name)
{
    const char *address = lookup(name);
    return gethostbyname(address ? address : name);
}

static int session_connect(int socket, const struct sockaddr *address, socklen_t length)
{
    pthread_once(&loaded, load);
    const struct sockaddr_in *in = (const struct sockaddr_in *)address;
    if (address && length >= sizeof *in && in->sin_family == AF_INET
        && in->sin_addr.s_addr == htonl(INADDR_LOOPBACK) && in->sin_port == htons(443)) {
        struct sockaddr_in routed = *in;
        routed.sin_port = https_port;
        return connect(socket, (const struct sockaddr *)&routed, sizeof routed);
    }
    return connect(socket, address, length);
}

/* dyld's interposing: calls from every other image reach these functions;
   calls from this image reach the originals. */
__attribute__((used, section("__DATA,__interpose"))) static const struct {
    const void *replacement, *original;
} interposers[] = {
    {(const void *)session_getaddrinfo, (const void *)getaddrinfo},
    {(const void *)session_gethostbyname, (const void *)gethostbyname},
    {(const void *)session_connect, (const void *)connect},
};
