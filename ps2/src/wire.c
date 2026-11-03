#include "wire.h"

#include <ps2ip.h>
#include <string.h>

u32 wire_read_u32(const u8 *bytes)
{
    return (u32)bytes[0] | (u32)bytes[1] << 8 | (u32)bytes[2] << 16 | (u32)bytes[3] << 24;
}

u64 wire_read_u64(const u8 *bytes)
{
    return (u64)wire_read_u32(bytes) | (u64)wire_read_u32(bytes + 4) << 32;
}

void wire_write_u32(u8 *bytes, u32 value)
{
    for (int i = 0; i < 4; i++)
    {
        bytes[i] = (u8)(value >> (8 * i));
    }
}

void wire_write_u64(u8 *bytes, u64 value)
{
    wire_write_u32(bytes, (u32)value);
    wire_write_u32(bytes + 4, (u32)(value >> 32));
}

int wire_receive_exactly(int fd, u8 *data, int length)
{
    int done = 0;
    while (done < length)
    {
        int count = lwip_recv(fd, data + done, length - done, 0);
        if (count <= 0)
        {
            return -1;
        }

        done += count;
    }

    return 0;
}

int wire_send_all(int fd, const u8 *data, int length)
{
    int done = 0;
    while (done < length)
    {
        // lwIP takes a send's length as 16 bits: 64 KiB in one call never goes out.
        int step = length - done < WIRE_SEND_CHUNK ? length - done : WIRE_SEND_CHUNK;
        int count = lwip_send(fd, data + done, step, 0);
        if (count <= 0)
        {
            return -1;
        }

        done += count;
    }

    return 0;
}

int wire_open_socket(int type, int port)
{
    int fd = lwip_socket(AF_INET, type, 0);
    if (fd < 0)
    {
        return -1;
    }

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    if (lwip_bind(fd, (struct sockaddr *)&address, sizeof(address)) < 0
        || (type == SOCK_STREAM && lwip_listen(fd, 1) < 0))
    {
        lwip_close(fd);
        return -1;
    }

    return fd;
}
