#include "control.h"
#include "disk.h"
#include "files.h"
#include "games.h"
#include "install.h"
#include "upgrade.h"
#include "wire.h"

#include <errno.h>
#include <kernel.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#define CONTROL_MAGIC 0x3143424Cu  // "LBC1"
#define HEADER_BYTES 12
// A FILE_WRITE carries a 64 KiB chunk after its 76-byte fields.
#define MAX_REQUEST_BYTES (FILES_CHUNK_MAX + 128)
#define LIST_PAGE_GAMES 64
#define MAX_REPLY_BYTES (8 + LIST_PAGE_GAMES * GAMES_ENTRY_BYTES)
_Static_assert(MAX_REPLY_BYTES >= FILES_CHUNK_MAX && MAX_REPLY_BYTES >= 8 + FILES_LIST_PAGE * FILES_ENTRY_BYTES,
               "A file reply must fit in the reply buffer.");
#define IDLE_SECONDS 30

#define COMMAND_HELLO 1
#define COMMAND_FREE_SPACE 2
#define COMMAND_LIST_GAMES 3
#define COMMAND_READ_ENTRY 4
#define COMMAND_INSTALL_BEGIN 5
#define COMMAND_INSTALL_STATUS 6
#define COMMAND_INSTALL_END 7
#define COMMAND_REMOVE_INCOMPLETE 8
#define COMMAND_READ_SECTORS 9
#define COMMAND_CHECKSUM_SECTORS 10
#define COMMAND_DELETE_GAME 11
#define COMMAND_UPDATE_GAME 12
#define COMMAND_FILE_LIST 13
#define COMMAND_FILE_READ 14
#define COMMAND_FILE_WRITE 15
#define COMMAND_FILE_COMMIT 16
#define COMMAND_FILE_DELETE 17
#define COMMAND_ELF_BEGIN 18
#define COMMAND_ELF_DATA 19
#define COMMAND_ELF_RUN 20
#define COMMAND_ELF_SAVE 21
#define COMMAND_FILE_RUNS 22

#define HELLO_REPLY_BYTES (24 + UPGRADE_TAG_BYTES)
#define HELLO_FLAG_DISK 0x1u
#define VERSION_BYTES 16
#define FREE_SPACE_REPLY_BYTES 16
#define INSTALL_BEGIN_REQUEST_BYTES 252
#define INSTALL_BEGIN_REPLY_BYTES (GAMES_NAME_BYTES + 4)
#define INSTALL_STATUS_REPLY_BYTES 24
#define UPDATE_GAME_REQUEST_BYTES (GAMES_NAME_BYTES + INSTALL_TITLE_BYTES + 4)
// File commands start with the folder and the name field.
#define FILE_FIELDS_BYTES (4 + FILES_NAME_BYTES)

static int listener = -1;
static u8 request[MAX_REQUEST_BYTES];
// The reply header and payload go out in one send, so a small reply does not
// wait for the PC to acknowledge the header first.
static u8 reply[HEADER_BYTES + MAX_REPLY_BYTES];

int control_open(void)
{
    listener = wire_open_socket(SOCK_STREAM, CONTROL_PORT);
    return listener >= 0 ? 0 : -1;
}

int control_add_fd(fd_set *set)
{
    FD_SET(listener, set);
    return listener;
}

static int hello(u32 length, u8 *out, u32 *out_length)
{
    if (length != 4)
    {
        return -EINVAL;
    }

    // The reply goes out even when the versions differ, so the PC can name this version.
    memset(out, 0, HELLO_REPLY_BYTES);
    wire_write_u32(out, CONTROL_PROTOCOL_VERSION);
    wire_write_u32(out + 4, disk_ready() ? HELLO_FLAG_DISK : 0);
    snprintf((char *)out + 8, VERSION_BYTES, "%s", LOADOUT_VERSION);
    memcpy(out + 24, upgrade_tag(), UPGRADE_TAG_BYTES);
    *out_length = HELLO_REPLY_BYTES;
    return wire_read_u32(request) == CONTROL_PROTOCOL_VERSION ? 0 : -EPROTONOSUPPORT;
}

static int free_space(u32 length, u8 *out, u32 *out_length)
{
    if (length != 0)
    {
        return -EINVAL;
    }

    u64 total, free;
    int status = disk_space(&total, &free);
    if (status < 0)
    {
        return status;
    }

    wire_write_u64(out, total);
    wire_write_u64(out + 8, free);
    *out_length = FREE_SPACE_REPLY_BYTES;
    return 0;
}

static int list_games(u32 length, u8 *out, u32 *out_length)
{
    if (length != 4)
    {
        return -EINVAL;
    }

    if (!disk_ready())
    {
        return -ENODEV;
    }

    u32 total, count;
    int status = games_list(wire_read_u32(request), LIST_PAGE_GAMES, out + 8, &total, &count);
    if (status < 0)
    {
        return status;
    }

    wire_write_u32(out, total);
    wire_write_u32(out + 4, count);
    *out_length = 8 + count * GAMES_ENTRY_BYTES;
    return 0;
}

// A request that is only a partition name: 1 to 32 bytes, without a NUL.
// Copies it into name (33 bytes).
static int request_name(u32 length, char *name)
{
    if (length == 0 || length > 32)
    {
        return -EINVAL;
    }

    if (!disk_ready())
    {
        return -ENODEV;
    }

    memcpy(name, request, length);
    name[length] = '\0';
    return 0;
}

static int read_entry(u32 length, u8 *out, u32 *out_length)
{
    char name[33];
    int status = request_name(length, name);
    if (status == 0)
    {
        status = games_read(name, out);
    }

    if (status < 0)
    {
        return status;
    }

    *out_length = GAMES_ENTRY_BYTES;
    return 0;
}

static int remove_incomplete(u32 length)
{
    char name[33];
    int status = request_name(length, name);
    return status < 0 ? status : install_remove_incomplete(name);
}

static int delete_game(u32 length)
{
    char name[33];
    int status = request_name(length, name);
    return status < 0 ? status : install_delete(name);
}

// Request: the partition name (40 bytes, NUL-padded), the title (160 bytes),
// then the compatibility flags, the transfer type and mode, and the disc type.
// Reply: the game's entry as the disk now holds it.
static int update_game(u32 length, u8 *out, u32 *out_length)
{
    if (length != UPDATE_GAME_REQUEST_BYTES || request[GAMES_NAME_BYTES - 1] != '\0')
    {
        return -EINVAL;
    }

    if (!disk_ready())
    {
        return -ENODEV;
    }

    struct install_edit edit;
    const u8 *fields = request + GAMES_NAME_BYTES + INSTALL_TITLE_BYTES;
    memcpy(edit.title, request + GAMES_NAME_BYTES, INSTALL_TITLE_BYTES);
    edit.compat_flags = fields[0];
    edit.transfer_type = fields[1];
    edit.transfer_mode = fields[2];
    edit.disc_type = fields[3];
    int status = install_update((const char *)request, &edit, out);
    if (status < 0)
    {
        return status;
    }

    *out_length = GAMES_ENTRY_BYTES;
    return 0;
}

// Request: the partition name (40 bytes, NUL-padded), then the part, the first
// sector, and the number of sectors. Reply: the sectors.
static int read_sectors(u32 length, u8 *out, u32 *out_length)
{
    if (length != GAMES_NAME_BYTES + 12 || request[GAMES_NAME_BYTES - 1] != '\0')
    {
        return -EINVAL;
    }

    if (!disk_ready())
    {
        return -ENODEV;
    }

    u32 sectors = wire_read_u32(request + GAMES_NAME_BYTES + 8);
    int status = install_read_sectors((const char *)request, wire_read_u32(request + GAMES_NAME_BYTES),
                                      wire_read_u32(request + GAMES_NAME_BYTES + 4), sectors, out);
    if (status < 0)
    {
        return status;
    }

    *out_length = sectors * 512;
    return 0;
}

// Request: the partition name (40 bytes, NUL-padded), then the part, the first
// sector, the number of sectors, and the sectors per read. Reply: the CRC-32C.
static int checksum_sectors(u32 length, u8 *out, u32 *out_length)
{
    if (length != GAMES_NAME_BYTES + 16 || request[GAMES_NAME_BYTES - 1] != '\0')
    {
        return -EINVAL;
    }

    if (!disk_ready())
    {
        return -ENODEV;
    }

    u32 crc;
    int status = install_checksum_sectors((const char *)request, wire_read_u32(request + GAMES_NAME_BYTES),
                                          wire_read_u32(request + GAMES_NAME_BYTES + 4),
                                          wire_read_u32(request + GAMES_NAME_BYTES + 8),
                                          wire_read_u32(request + GAMES_NAME_BYTES + 12), &crc);
    if (status < 0)
    {
        return status;
    }

    wire_write_u32(out, crc);
    *out_length = 4;
    return 0;
}

// File requests start with the folder and a NUL-padded name.
static int file_name(u32 length, u32 fields)
{
    if (length < FILE_FIELDS_BYTES + fields || request[FILE_FIELDS_BYTES - 1] != '\0')
    {
        return -EINVAL;
    }

    return disk_ready() ? 0 : -ENODEV;
}

static int file_list(u32 length, u8 *out, u32 *out_length)
{
    if (length != 8)
    {
        return -EINVAL;
    }

    if (!disk_ready())
    {
        return -ENODEV;
    }

    u32 total, count;
    int status = files_list(wire_read_u32(request), wire_read_u32(request + 4), out + 8, &total, &count);
    if (status < 0)
    {
        return status;
    }

    wire_write_u32(out, total);
    wire_write_u32(out + 4, count);
    *out_length = 8 + count * FILES_ENTRY_BYTES;
    return 0;
}

// Request: the folder, the name, the offset, and the bytes wanted. Reply: the bytes read.
static int file_read(u32 length, u8 *out, u32 *out_length)
{
    int status = length == FILE_FIELDS_BYTES + 8 ? file_name(length, 8) : -EINVAL;
    if (status == 0)
    {
        status = files_read(wire_read_u32(request), (const char *)request + 4, wire_read_u32(request + FILE_FIELDS_BYTES),
                            wire_read_u32(request + FILE_FIELDS_BYTES + 4), out);
    }

    if (status < 0)
    {
        return status;
    }

    *out_length = (u32)status;
    return 0;
}

// Request: the folder, the name, the offset, then the chunk.
static int file_write(u32 length)
{
    int status = file_name(length, 5);
    return status < 0 ? status
                      : files_write(wire_read_u32(request), (const char *)request + 4,
                                    wire_read_u32(request + FILE_FIELDS_BYTES), request + FILE_FIELDS_BYTES + 4,
                                    length - FILE_FIELDS_BYTES - 4);
}

// Request: the folder, the name, and the file's size.
static int file_commit(u32 length)
{
    int status = length == FILE_FIELDS_BYTES + 4 ? file_name(length, 4) : -EINVAL;
    return status < 0 ? status
                      : files_commit(wire_read_u32(request), (const char *)request + 4,
                                     wire_read_u32(request + FILE_FIELDS_BYTES));
}

// Request: the folder and the name. Reply: the block runs the file's inode lists.
static int file_runs(u32 length, u8 *out, u32 *out_length)
{
    u32 runs = 0;
    int status = length == FILE_FIELDS_BYTES ? file_name(length, 0) : -EINVAL;
    if (status == 0)
    {
        status = files_runs(wire_read_u32(request), (const char *)request + 4, &runs);
    }

    if (status < 0)
    {
        return status;
    }

    wire_write_u32(out, runs);
    *out_length = 4;
    return 0;
}

static int file_delete(u32 length)
{
    int status = length == FILE_FIELDS_BYTES ? file_name(length, 0) : -EINVAL;
    return status < 0 ? status : files_delete(wire_read_u32(request), (const char *)request + 4);
}

// Request: the offset, then the chunk.
static int elf_data(u32 length)
{
    return length > 4 ? upgrade_data(wire_read_u32(request), request + 4, length - 4) : -EINVAL;
}

static int install_begin_request(u32 length, u8 *out, u32 *out_length)
{
    if (length != INSTALL_BEGIN_REQUEST_BYTES)
    {
        return -EINVAL;
    }

    struct install_game game;
    memcpy(game.disc_id, request, INSTALL_DISC_ID_BYTES);
    memcpy(game.startup, request + 16, INSTALL_STARTUP_BYTES);
    memcpy(game.title, request + 76, INSTALL_TITLE_BYTES);
    game.layer0_sectors = wire_read_u32(request + 236);
    game.layer1_sectors = wire_read_u32(request + 240);
    game.disc_type = request[244];
    game.compat_flags = request[245];
    game.transfer_type = request[246];
    game.transfer_mode = request[247];
    game.flags = wire_read_u32(request + 248);
    // The text fields must end inside their space, apart from the title, which the header holds as it is.
    if (game.disc_id[INSTALL_DISC_ID_BYTES - 1] != '\0' || game.startup[INSTALL_STARTUP_BYTES - 1] != '\0')
    {
        return -EINVAL;
    }

    char name[GAMES_NAME_BYTES];
    u32 parts;
    memset(name, 0, sizeof(name));
    int status = install_begin(&game, name, &parts);
    if (status < 0)
    {
        return status;
    }

    memcpy(out, name, GAMES_NAME_BYTES);
    wire_write_u32(out + GAMES_NAME_BYTES, parts);
    *out_length = INSTALL_BEGIN_REPLY_BYTES;
    return 0;
}

static int install_status_request(u32 length, u8 *out, u32 *out_length)
{
    if (length != 0)
    {
        return -EINVAL;
    }

    u64 total, received, written;
    int status = install_status(&total, &received, &written);
    if (status == -ENOENT)
    {
        return status;
    }

    // The counts go out with a write error too, so the PC can say how far the copy got.
    wire_write_u64(out, total);
    wire_write_u64(out + 8, received);
    wire_write_u64(out + 16, written);
    *out_length = INSTALL_STATUS_REPLY_BYTES;
    return status;
}

// An install that failed answers with the first different MiB, when the check
// found one, and whether its partitions were removed.
static int install_end_request(u8 *out, u32 *out_length, status_report report)
{
    u32 bad_block = 0;
    int removed = 0;
    int status = install_end(report, &bad_block, &removed);
    if (status != 0)
    {
        wire_write_u32(out, bad_block);
        wire_write_u32(out + 4, (u32)removed);
        *out_length = 8;
    }

    return status;
}

// Serves requests until the PC disconnects, sends something that is not this
// protocol, or stays idle. Returns the status line to show afterwards. While a
// game is installed, its data connection is served in the same wait.
static const char *serve_requests(int fd, status_report report)
{
    int greeted = 0;
    int hello_seen = 0;
    for (;;)
    {
        fd_set readable;
        FD_ZERO(&readable);
        FD_SET(fd, &readable);
        int data = install_add_fds(&readable);
        struct timeval wait = {IDLE_SECONDS, 0};
        if (lwip_select((data > fd ? data : fd) + 1, &readable, NULL, NULL, &wait) <= 0)
        {
            return "Loadout was idle, so the connection was closed.";
        }

        install_serve_ready(&readable, report);
        if (!FD_ISSET(fd, &readable))
        {
            continue;
        }

        u8 header[HEADER_BYTES];
        if (wire_receive_exactly(fd, header, sizeof(header)) < 0)
        {
            return "Loadout disconnected.";
        }

        u32 command = wire_read_u32(header + 4);
        u32 length = wire_read_u32(header + 8);
        if (wire_read_u32(header) != CONTROL_MAGIC || length > MAX_REQUEST_BYTES)
        {
            return "A connection that is not Loadout was closed.";
        }

        if (length > 0 && wire_receive_exactly(fd, request, (int)length) < 0)
        {
            return "Loadout disconnected.";
        }

        u8 *out = reply + HEADER_BYTES;
        u32 out_length = 0;
        int status;
        // ELF_BEGIN, ELF_DATA, and ELF_RUN keep their form in every protocol version from 5 on, so
        // they follow any HELLO: a newer Loadout can always send a newer Loadout Bay.
        int upgrade = command == COMMAND_ELF_BEGIN || command == COMMAND_ELF_DATA || command == COMMAND_ELF_RUN;
        if (command != COMMAND_HELLO && !greeted && !(upgrade && hello_seen))
        {
            status = -EPROTO;
        }
        else
        {
            switch (command)
            {
            case COMMAND_HELLO:
                status = hello(length, out, &out_length);
                greeted = status == 0;
                hello_seen = 1;
                break;
            case COMMAND_FREE_SPACE:
                status = free_space(length, out, &out_length);
                break;
            case COMMAND_LIST_GAMES:
                status = list_games(length, out, &out_length);
                break;
            case COMMAND_READ_ENTRY:
                status = read_entry(length, out, &out_length);
                break;
            case COMMAND_INSTALL_BEGIN:
                status = install_begin_request(length, out, &out_length);
                break;
            case COMMAND_INSTALL_STATUS:
                status = install_status_request(length, out, &out_length);
                break;
            case COMMAND_INSTALL_END:
                status = length == 0 ? install_end_request(out, &out_length, report) : -EINVAL;
                break;
            case COMMAND_REMOVE_INCOMPLETE:
                status = remove_incomplete(length);
                break;
            case COMMAND_READ_SECTORS:
                status = read_sectors(length, out, &out_length);
                break;
            case COMMAND_CHECKSUM_SECTORS:
                status = checksum_sectors(length, out, &out_length);
                break;
            case COMMAND_DELETE_GAME:
                status = delete_game(length);
                break;
            case COMMAND_UPDATE_GAME:
                status = update_game(length, out, &out_length);
                break;
            case COMMAND_FILE_LIST:
                status = file_list(length, out, &out_length);
                break;
            case COMMAND_FILE_READ:
                status = file_read(length, out, &out_length);
                break;
            case COMMAND_FILE_WRITE:
                status = file_write(length);
                break;
            case COMMAND_FILE_COMMIT:
                status = file_commit(length);
                break;
            case COMMAND_FILE_DELETE:
                status = file_delete(length);
                break;
            case COMMAND_ELF_BEGIN:
                status = length == 4 ? upgrade_begin(wire_read_u32(request)) : -EINVAL;
                break;
            case COMMAND_ELF_DATA:
                status = elf_data(length);
                break;
            case COMMAND_ELF_RUN:
                status = length == UPGRADE_TAG_BYTES ? upgrade_prepare_run(request) : -EINVAL;
                break;
            case COMMAND_ELF_SAVE:
                status = length == 0 ? upgrade_save() : -EINVAL;
                break;
            case COMMAND_FILE_RUNS:
                status = file_runs(length, out, &out_length);
                break;
            default:
                status = -ENOSYS;
                break;
            }

            // Before the reply: once it has an install's answer, Loadout starts sending and the disk is busy.
            if (command == COMMAND_INSTALL_BEGIN || command == COMMAND_INSTALL_END || command == COMMAND_REMOVE_INCOMPLETE
                || command == COMMAND_DELETE_GAME)
            {
                disk_show_free();
            }
        }

        wire_write_u32(reply, CONTROL_MAGIC);
        wire_write_u32(reply + 4, (u32)status);
        wire_write_u32(reply + 8, out_length);
        if (wire_send_all(fd, reply, (int)(HEADER_BYTES + out_length)) < 0)
        {
            return "Loadout disconnected.";
        }

        if (upgrade_run_pending())
        {
            return "Starting the new Loadout Bay...";
        }
    }
}

// An install that Loadout leaves open is removed, and OPL's
// partition is unmounted, so its files are all on the disk.
static const char *serve_session(int fd, status_report report)
{
    const char *result = serve_requests(fd, report);
    if (install_busy())
    {
        install_abort();
        disk_show_free();
    }

    files_close();
    return result;
}

void control_serve_ready(fd_set *readable, status_report report)
{
    if (!FD_ISSET(listener, readable))
    {
        return;
    }

    struct sockaddr_in peer;
    socklen_t peer_length = sizeof(peer);
    int client = lwip_accept(listener, (struct sockaddr *)&peer, &peer_length);
    if (client < 0)
    {
        return;
    }

    // s_addr is in network byte order, so its bytes are already in display order.
    const unsigned char *address = (const unsigned char *)&peer.sin_addr.s_addr;
    char from[32];
    snprintf(from, sizeof(from), "From %u.%u.%u.%u", address[0], address[1], address[2], address[3]);
    report(STATUS_CONNECTED, from, STATUS_NO_PROGRESS);
    const char *end = serve_session(client, report);
    lwip_close(client);
    if (upgrade_run_pending())
    {
        report(STATUS_UPDATING, NULL, STATUS_NO_PROGRESS);
        // Gives lwIP time to deliver the last reply before the IOP reset ends the network.
        sleep(1);
        upgrade_run();
        report(STATUS_UPDATE_FAILED, NULL, STATUS_NO_PROGRESS);
        SleepThread();
    }

    report(STATUS_READY, end, STATUS_NO_PROGRESS);
}
