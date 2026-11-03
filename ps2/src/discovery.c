#include "discovery.h"
#include "control.h"
#include "disk.h"
#include "wire.h"

#include <stdio.h>
#include <string.h>

#define DISCOVERY_MAGIC 0x3144424Cu  // "LBD1"
#define PROBE_BYTES 8
#define VERSION_BYTES 16
#define ANSWER_BYTES (12 + VERSION_BYTES)
#define ANSWER_FLAG_DISK 0x1u

static int fd = -1;

int discovery_open(void)
{
    fd = wire_open_socket(SOCK_DGRAM, DISCOVERY_PORT);
    return fd >= 0 ? 0 : -1;
}

int discovery_add_fd(fd_set *set)
{
    FD_SET(fd, set);
    return fd;
}

void discovery_serve_ready(fd_set *readable)
{
    if (!FD_ISSET(fd, readable))
    {
        return;
    }

    u8 probe[64];
    struct sockaddr_in from;
    socklen_t from_length = sizeof(from);
    int count = lwip_recvfrom(fd, probe, sizeof(probe), 0, (struct sockaddr *)&from, &from_length);
    // Anything but a probe gets no answer, so a stray datagram costs nothing.
    if (count != PROBE_BYTES || wire_read_u32(probe) != DISCOVERY_MAGIC)
    {
        return;
    }

    // The answer goes to the sender alone; the PC reads Loadout Bay's address from it.
    u8 answer[ANSWER_BYTES];
    memset(answer, 0, sizeof(answer));
    wire_write_u32(answer, DISCOVERY_MAGIC);
    wire_write_u32(answer + 4, CONTROL_PROTOCOL_VERSION);
    wire_write_u32(answer + 8, disk_ready() ? ANSWER_FLAG_DISK : 0);
    snprintf((char *)answer + 12, VERSION_BYTES, "%s", LOADOUT_VERSION);
    lwip_sendto(fd, answer, sizeof(answer), 0, (struct sockaddr *)&from, from_length);
}
