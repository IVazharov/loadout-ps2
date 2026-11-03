// Little-endian numbers and TCP/UDP sockets shared by the speed test and the
// control protocol.

#ifndef LOADOUT_WIRE_H
#define LOADOUT_WIRE_H

#include <tamtypes.h>

u32 wire_read_u32(const u8 *bytes);
u64 wire_read_u64(const u8 *bytes);
void wire_write_u32(u8 *bytes, u32 value);
void wire_write_u64(u8 *bytes, u64 value);

// Receives exactly length bytes. Returns 0, or -1 if the connection ended first.
int wire_receive_exactly(int fd, u8 *data, int length);

// Sends all length bytes, at most WIRE_SEND_CHUNK per lwip_send. Returns 0, or
// -1 if the connection failed.
#define WIRE_SEND_CHUNK (16 * 1024)
int wire_send_all(int fd, const u8 *data, int length);

// Opens a socket bound to port on every address; a TCP socket also listens.
// Returns the socket, or -1.
int wire_open_socket(int type, int port);

#endif
