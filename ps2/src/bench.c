#include "bench.h"
#include "disk.h"
#include "wire.h"

#include <kernel.h>
#include <ps2ip.h>
#include <stdio.h>
#include <string.h>
#include <timer.h>

#define TCP_MAGIC 0x3154424Cu  // "LBT1"
#define UDP_MAGIC 0x3155424Cu  // "LBU1"
#define REPLY_MAGIC 0x3152424Cu  // "LBR1"
#define UDP_END_SEQUENCE 0xFFFFFFFFu
#define UDP_IDLE_SECONDS 3
#define REPORT_MICROSECONDS 1000000u
#define BYTES_PER_MB 1048576u

static u8 buffer[DISK_CHUNK_BYTES] __attribute__((aligned(64)));

u64 bench_now_microseconds(void)
{
    return GetTimerSystemTime() * 1000u / (kBUSCLK / 1000u);
}

void bench_format_rate(char *text, int size, u64 bytes, u64 microseconds)
{
    if (microseconds == 0)
    {
        snprintf(text, size, "-- MB/s");
        return;
    }

    // Hundredths of a MB per second, in integers: the EE has no fast double.
    u64 hundredths = bytes * 100u * 1000000u / BYTES_PER_MB / microseconds;
    snprintf(text, size, "%u.%02u MB/s", (unsigned)(hundredths / 100u), (unsigned)(hundredths % 100u));
}

static void report_progress(bench_report report, const char *transport, u64 bytes, u64 microseconds,
                            const struct bench_result *last)
{
    char rate[24], detail[72];
    bench_format_rate(rate, sizeof(rate), bytes, microseconds);
    snprintf(detail, sizeof(detail), "%s \xC2\xB7 %u MB, %s", transport, (unsigned)(bytes / BYTES_PER_MB), rate);
    report(STATUS_BENCH, detail, last);
}

// Receives up to length bytes. In disk mode it waits for the whole block, because
// the disk is written a block at a time. Returns the byte count, or -1 if the
// connection ended first.
static int receive_block(int fd, u8 *data, int length, int whole)
{
    int done = 0;
    while (done < length)
    {
        int count = lwip_recv(fd, data + done, length - done, 0);
        if (count <= 0)
        {
            return done > 0 ? done : -1;
        }

        done += count;
        if (!whole)
        {
            break;
        }
    }

    return done;
}

static void serve_tcp(int listener, bench_report report, struct bench_result *last)
{
    int client = lwip_accept(listener, NULL, NULL);
    if (client < 0)
    {
        return;
    }

    u8 header[16];
    if (wire_receive_exactly(client, header, sizeof(header)) < 0 || wire_read_u32(header) != TCP_MAGIC)
    {
        lwip_close(client);
        report(STATUS_BENCH_FAILED, "The PC did not start a test run.", last);
        return;
    }

    u32 flags = wire_read_u32(header + 4);
    int to_disk = (flags & BENCH_FLAG_DISK) != 0;
    const char *transport = to_disk ? "TCP to HDD" : "TCP";
    u64 total = wire_read_u64(header + 8);
    u64 received = 0, started = 0, finished = 0, reported = 0;
    int status = 0;
    if (to_disk)
    {
        report(STATUS_BENCH, "TCP to HDD \xC2\xB7 making the test partition\xE2\x80\xA6", last);
        status = disk_begin(total, (flags >> BENCH_UNIT_SHIFT) * 1024u);
    }

    if (status == 0)
    {
        report(STATUS_BENCH, to_disk ? "TCP to HDD \xC2\xB7 receiving\xE2\x80\xA6" : "TCP \xC2\xB7 receiving\xE2\x80\xA6", last);
    }

    while (status == 0 && received < total)
    {
        u64 left = total - received;
        int want = left < sizeof(buffer) ? (int)left : (int)sizeof(buffer);
        // A disk run receives into the writer's buffers, so the next block
        // arrives while the previous one is written.
        u8 *target = to_disk ? disk_next_buffer() : buffer;
        int count = receive_block(client, target, want, to_disk);
        if (count <= 0)
        {
            if (to_disk)
            {
                disk_submit(target, 0);
            }

            break;
        }

        if (received == 0)
        {
            started = bench_now_microseconds();
            reported = started;
        }

        if (to_disk)
        {
            status = disk_submit(target, (u32)count);
        }

        finished = bench_now_microseconds();
        received += (u64)count;
        if (finished - reported >= REPORT_MICROSECONDS)
        {
            reported = finished;
            report_progress(report, transport, received, finished - started, last);
        }
    }

    if (to_disk)
    {
        // The run ends when the last block is on the disk, not when it arrived.
        int drained = disk_drain();
        if (received > 0)
        {
            finished = bench_now_microseconds();
        }

        if (status == 0)
        {
            status = drained;
        }

        int ended = disk_end((flags & BENCH_FLAG_KEEP) != 0);
        disk_show_free();
        if (status == 0)
        {
            status = ended;
        }
    }

    last->transport = transport;
    last->bytes = received;
    last->microseconds = finished - started;
    last->packets_received = 0;
    last->packets_sent = 0;

    u8 reply[BENCH_TCP_REPLY_BYTES];
    memset(reply, 0, sizeof(reply));
    wire_write_u64(reply, last->bytes);
    wire_write_u64(reply + 8, last->microseconds);
    wire_write_u32(reply + 16, (u32)status);
    lwip_send(client, reply, sizeof(reply), 0);
    lwip_close(client);
    if (status != 0)
    {
        char text[72];
        snprintf(text, sizeof(text), "%s stopped with error %d.", transport, status);
        report(STATUS_BENCH_FAILED, text, last);
        return;
    }

    report(STATUS_BENCH_DONE, received == total ? NULL : "The run ended early.", last);
}

struct udp_run
{
    u32 id;
    int active;
    u64 bytes;
    u64 started;
    u64 finished;
    u32 packets;
    u32 sent;
};

static void send_udp_reply(int fd, const struct sockaddr_in *to, const struct udp_run *run)
{
    u8 reply[32];
    wire_write_u32(reply, REPLY_MAGIC);
    wire_write_u32(reply + 4, run->id);
    wire_write_u32(reply + 8, run->packets);
    wire_write_u32(reply + 12, run->sent);
    wire_write_u64(reply + 16, run->bytes);
    wire_write_u64(reply + 24, run->finished - run->started);
    lwip_sendto(fd, reply, sizeof(reply), 0, (const struct sockaddr *)to, sizeof(*to));
}

static void finish_udp_run(struct udp_run *run, bench_report report, struct bench_result *last, const char *detail)
{
    run->active = 0;
    last->transport = "UDP";
    last->bytes = run->bytes;
    last->microseconds = run->finished - run->started;
    last->packets_received = run->packets;
    last->packets_sent = run->sent;
    report(STATUS_BENCH_DONE, detail, last);
}

// Receives one UDP run until its end packet arrives or the sender goes quiet.
static void serve_udp(int fd, bench_report report, struct bench_result *last, struct udp_run *run)
{
    u64 reported = 0;
    for (;;)
    {
        fd_set readable;
        FD_ZERO(&readable);
        FD_SET(fd, &readable);
        struct timeval wait = {UDP_IDLE_SECONDS, 0};
        if (lwip_select(fd + 1, &readable, NULL, NULL, &wait) <= 0)
        {
            if (run->active)
            {
                finish_udp_run(run, report, last, "The run ended without its last packet.");
            }

            return;
        }

        struct sockaddr_in from;
        socklen_t from_length = sizeof(from);
        int count = lwip_recvfrom(fd, buffer, sizeof(buffer), 0, (struct sockaddr *)&from, &from_length);
        if (count < 12 || wire_read_u32(buffer) != UDP_MAGIC)
        {
            continue;
        }

        u32 id = wire_read_u32(buffer + 4);
        u32 sequence = wire_read_u32(buffer + 8);
        if (sequence == UDP_END_SEQUENCE)
        {
            if (id == run->id && count >= 16)
            {
                run->sent = wire_read_u32(buffer + 12);
                send_udp_reply(fd, &from, run);
                if (run->active)
                {
                    finish_udp_run(run, report, last, NULL);
                }

                return;
            }

            continue;
        }

        u64 now = bench_now_microseconds();
        if (!run->active || id != run->id)
        {
            memset(run, 0, sizeof(*run));
            run->id = id;
            run->active = 1;
            run->started = now;
            reported = now;
            report(STATUS_BENCH, "UDP \xC2\xB7 receiving\xE2\x80\xA6", last);
        }

        run->finished = now;
        run->packets++;
        run->bytes += (u64)count;
        if (now - reported >= REPORT_MICROSECONDS)
        {
            reported = now;
            report_progress(report, "UDP", run->bytes, now - run->started, last);
        }
    }
}

static int tcp = -1, udp = -1;
static struct bench_result last;
static struct udp_run run;

int bench_open(void)
{
    tcp = wire_open_socket(SOCK_STREAM, BENCH_TCP_PORT);
    udp = wire_open_socket(SOCK_DGRAM, BENCH_UDP_PORT);
    return tcp >= 0 && udp >= 0 ? 0 : -1;
}

int bench_add_fds(fd_set *set)
{
    FD_SET(tcp, set);
    FD_SET(udp, set);
    return tcp > udp ? tcp : udp;
}

void bench_serve_ready(fd_set *readable, bench_report report)
{
    if (FD_ISSET(tcp, readable))
    {
        serve_tcp(tcp, report, &last);
    }

    if (FD_ISSET(udp, readable))
    {
        serve_udp(udp, report, &last, &run);
    }
}
