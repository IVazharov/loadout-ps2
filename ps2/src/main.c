// Loadout Bay: the PS2 side of Loadout.
//
// Brings up the network adapter, shows the console's address, and serves
// Loadout and the speed test. The address comes from loadout-bay.cfg next to
// the ELF, or from DHCP when that file is missing or says dhcp=on.

#include <elf-loader.h>
#include <iopcontrol.h>
#include <delaythread.h>
#include <iopheap.h>
#include <kernel.h>
#include <loadfile.h>
#include <netman.h>
#include <ps2ip.h>
#include <ps2sdkapi.h>
#include <sbv_patches.h>
#include <sifrpc.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

// With this defined, fopen goes through fileXio, which reaches USB drives too.
#define NEWLIB_PORT_AWARE
#include <fileXio_rpc.h>

#include "bench.h"
#include "config.h"
#include "control.h"
#include "discovery.h"
#include "disk.h"
#include "files.h"
#include "install.h"
#include "pad.h"
#include "ui.h"
#include "upgrade.h"

#define IRX(name)                    \
    extern unsigned char name##_irx[]; \
    extern unsigned int size_##name##_irx

IRX(iomanX);
IRX(fileXio);
IRX(bdm);
IRX(bdmfs_fatfs);
IRX(usbd);
IRX(usbmass_bd);
IRX(ps2dev9);
IRX(netman);
IRX(smap);
IRX(ps2atad);
IRX(loadout_hdd);
IRX(ps2fs);

#define LOAD_IRX(name) SifExecModuleBuffer(name##_irx, size_##name##_irx, 0, NULL, NULL)

#define DEVICE_WAIT_SECONDS 5
#define LINK_WAIT_SECONDS 10
#define DHCP_WAIT_SECONDS 20

// s_addr is in network byte order, so its bytes are already in display order.
static const char *format_address(char *text, int size, const struct in_addr *address)
{
    const unsigned char *bytes = (const unsigned char *)&address->s_addr;
    snprintf(text, size, "%u.%u.%u.%u", bytes[0], bytes[1], bytes[2], bytes[3]);
    return text;
}

static void fail(enum status status)
{
    ui_status(status, NULL, STATUS_NO_PROGRESS);
}

static void reset_iop(void)
{
    SifInitRpc(0);
    while (!SifIopReset("", 0))
    {
    }
    while (!SifIopSync())
    {
    }
    SifInitRpc(0);
    SifInitIopHeap();
    SifLoadFileInit();
    sbv_patch_enable_lmb();
}

static int load_modules(void)
{
    if (LOAD_IRX(iomanX) < 0 || LOAD_IRX(fileXio) < 0 || fileXioInit() < 0)
    {
        fail(STATUS_FAILED_DRIVERS);
        return -1;
    }

    // Memory cards and USB drives, so loadout-bay.cfg can be read where the ELF was started.
    if (SifLoadModule("rom0:SIO2MAN", 0, NULL) < 0
        || SifLoadModule("rom0:MCMAN", 0, NULL) < 0
        || SifLoadModule("rom0:MCSERV", 0, NULL) < 0
        || LOAD_IRX(bdm) < 0
        || LOAD_IRX(bdmfs_fatfs) < 0
        || LOAD_IRX(usbd) < 0
        || LOAD_IRX(usbmass_bd) < 0)
    {
        fail(STATUS_FAILED_DRIVERS);
        return -1;
    }

    // The controllers, on the same SIO2MAN. Without them Loadout Bay still serves the PC.
    pad_init();

    if (LOAD_IRX(ps2dev9) < 0 || LOAD_IRX(netman) < 0 || LOAD_IRX(smap) < 0)
    {
        fail(STATUS_FAILED_NETWORK);
        return -1;
    }

    // The hard disk shares the expansion bay with the network adapter, so its
    // drivers load after DEV9. Without a disk, Loadout Bay still runs.
    // The cache holds partition headers, 1 KB each. Opening a partition walks the
    // disk's chain of them, and a chain longer than the cache empties it on the way:
    // 138 partitions with a cache of 20 took 18 s to list 51 games, against 0.3 s
    // with room for them all. It comes out of the IOP's 2 MB: in PCSX2, 512 passed,
    // 640 failed writing to the disk, and at 896 the network no longer came up.
    static const char hdd_args[] = "-o\0" "4\0" "-n\0" "384";
    if (LOAD_IRX(ps2atad) < 0
        || SifExecModuleBuffer(loadout_hdd_irx, size_loadout_hdd_irx, sizeof(hdd_args), hdd_args, NULL) < 0
        || disk_init() < 0)
    {
        ui_set_disk(0);
    }
    else
    {
        // PFS, for OPL's art and game settings: 4 open files and 20 buffers, in the
        // same form as the hard disk driver's arguments. With its defaults (2 and 8)
        // it warns that it may need 12 buffers. Without it, games still install.
        static const char pfs_args[] = "-o\0" "4\0" "-n\0" "20";
        SifExecModuleBuffer(ps2fs_irx, size_ps2fs_irx, sizeof(pfs_args), pfs_args, NULL);
        ui_set_disk(disk_size_gb());
        disk_show_free();
    }

    return 0;
}

// A USB drive appears a moment after its modules load. The ELF itself is on
// the start device, so once it opens, a missing config file is really missing.
static void wait_for_device(const char *elf_path)
{
    for (int second = 0; second < DEVICE_WAIT_SECONDS; second++)
    {
        FILE *file = fopen(elf_path, "rb");
        if (file != NULL)
        {
            fclose(file);
            return;
        }

        sleep(1);
    }
}

static char exit_elf[CONFIG_PATH_LENGTH];

// Where the settings screen saves loadout-bay.cfg: its path, and on the hard
// disk the partition to mount first. Empty when it cannot be saved.
static char settings_path[256];
static char settings_partition[40];

// The settings in use.
static struct net_config current;

// Says where loadout-bay.cfg is saved in the words the screen uses, such as
// "USB drive › APPS/loadout-bay.cfg"; a device Loadout Bay cannot write leaves it unsaved.
static void set_settings_place(const char *path, const char *partition)
{
    char place[96];
    const char *colon = strchr(path, ':');
    const char *rest = colon != NULL ? colon + 1 + strspn(colon + 1, "/") : path;
    if (partition[0] != '\0')
    {
        snprintf(place, sizeof(place), "Hard disk \xE2\x80\xBA %s \xE2\x80\xBA %s", partition, rest);
    }
    else if (strncmp(path, "mass", 4) == 0)
    {
        snprintf(place, sizeof(place), "USB drive \xE2\x80\xBA %s", rest);
    }
    else if (strncmp(path, "mc0:", 4) == 0 || strncmp(path, "mc1:", 4) == 0)
    {
        snprintf(place, sizeof(place), "Memory card %c \xE2\x80\xBA %s", path[2] + 1, rest);
    }
    else
    {
        return;
    }

    snprintf(settings_path, sizeof(settings_path), "%s", path);
    snprintf(settings_partition, sizeof(settings_partition), "%s", partition);
    ui_set_settings_place(place);
}

// Starts the ELF that loadout-bay.cfg names in exit=, in the forms of argv[0].
// Returns only if it cannot be started.
static void start_exit_elf(void)
{
    sbv_patch_disable_prefix_check();
    char partition[40];
    char inner[CONFIG_PATH_LENGTH];
    if (!config_split_hdd_path(exit_elf, partition, sizeof(partition), inner, sizeof(inner)))
    {
        LoadELFFromFile(exit_elf, 0, NULL);
        return;
    }

    // An OPL app on the hard disk: its partition is mounted at pfs0:, which "pfs:" names,
    // and the ELF gets the partition in its argv[0] as OPL gives it.
    if (files_mount_partition(partition, 0) == 0)
    {
        char file[CONFIG_PATH_LENGTH + 8];
        char device[48];
        snprintf(file, sizeof(file), "pfs:/%s", inner);
        snprintf(device, sizeof(device), "hdd0:%s:", partition);
        LoadELFFromFileWithPartition(file, device, 0, NULL);
        files_close();
    }
}

// Exit: the ELF named in exit=, or the PS2's menu (the browser, or the launcher the PS2 boots).
static void leave(void)
{
    // Nothing else runs from here on: the IOP is about to be reset under the other threads.
    ChangeThreadPriority(GetThreadId(), 0);
    pad_close();
    if (exit_elf[0] != '\0')
    {
        start_exit_elf();
    }

    // The IOP goes back to the BIOS's own modules first, as before starting a new
    // Loadout Bay: that also stops the network adapter writing into EE memory, which
    // would overwrite the menu as it loads. Starting OSDSYS without arguments gives
    // the main menu, FreeMcBoot's or Free HDBoot's on a PS2 that boots one. Exit would
    // ask for the Browser screen instead, so it is only what is left if this returns.
    SifIopReset("", 0);
    while (!SifIopSync())
    {
    }

    SifInitRpc(0);
    LoadExecPS2("rom0:OSDSYS", 0, NULL);
    Exit(0);
}

static void choose_config(int argc, char *argv[], struct net_config *config)
{
    memset(config, 0, sizeof(*config));
    config->use_dhcp = 1;
    if (argc < 1 || argv[0] == NULL || argv[0][0] == '\0')
    {
        ui_set_network(1, UI_SOURCE_DEFAULT);
        return;
    }

    char path[256];
    char partition[40];
    char inner[200];
    char note[72] = "";
    enum config_result result = CONFIG_MISSING;
    ui_status(STATUS_STARTING, "Reading " CONFIG_FILE_NAME ".", STATUS_NO_PROGRESS);
    if (config_split_hdd_path(argv[0], partition, sizeof(partition), inner, sizeof(inner)))
    {
        // Started by OPL from the hard disk, whose partition is no longer mounted.
        if (files_mount_partition(partition, 0) == 0)
        {
            char elf[sizeof(inner) + 8];
            snprintf(elf, sizeof(elf), "pfs0:%s", inner);
            config_path_beside(elf, path, sizeof(path));
            result = config_load(path, config, note, sizeof(note));
            files_close();
            set_settings_place(path, partition);
        }
    }
    else
    {
        config_path_beside(argv[0], path, sizeof(path));
        wait_for_device(argv[0]);
        result = config_load(path, config, note, sizeof(note));
        set_settings_place(path, "");
    }

    switch (result)
    {
    case CONFIG_LOADED:
        ui_set_network(config->use_dhcp, UI_SOURCE_FILE);
        break;
    case CONFIG_MISSING:
        ui_set_network(1, UI_SOURCE_DEFAULT);
        break;
    case CONFIG_INVALID:
        config->use_dhcp = 1;
        ui_set_network(1, UI_SOURCE_IGNORED);
        ui_set_note(note);
        break;
    }
}

// Shows a finished speed test as "TCP 7.85 MB/s" or "UDP 9.12 MB/s · 0.3% lost".
static void on_bench_report(enum status status, const char *detail, const struct bench_result *result)
{
    char speed[72] = "";
    if (status == STATUS_BENCH_DONE && result != NULL && result->transport != NULL)
    {
        char rate[24];
        bench_format_rate(rate, sizeof(rate), result->bytes, result->microseconds);
        int length = snprintf(speed, sizeof(speed), "%s %s", result->transport, rate);
        if (result->packets_sent > 0 && length > 0 && length < (int)sizeof(speed))
        {
            u32 lost = result->packets_sent > result->packets_received ? result->packets_sent - result->packets_received : 0;
            u32 tenths = (u32)((u64)lost * 1000u / result->packets_sent);
            snprintf(speed + length, sizeof(speed) - length, " \xC2\xB7 %u.%u%% lost", tenths / 10u, tenths % 10u);
        }

        ui_set_speed(speed);
    }

    ui_status(status, detail != NULL ? detail : speed, STATUS_NO_PROGRESS);
}

static int link_is_up(void)
{
    return NetManIoctl(NETMAN_NETIF_IOCTL_GET_LINK_STATUS, NULL, 0, NULL, 0) == NETMAN_NETIF_ETH_LINK_STATE_UP;
}

static int wait_for_link(void)
{
    for (int second = 0; second < LINK_WAIT_SECONDS; second++)
    {
        if (link_is_up())
        {
            return 0;
        }

        sleep(1);
    }

    return link_is_up() ? 0 : -1;
}

static int apply_config(const struct net_config *config)
{
    t_ip_info info;
    if (ps2ip_getconfig("sm0", &info) < 0)
    {
        return -1;
    }

    // Going over to DHCP drops the old address first, so the wait below sees the new one.
    if (config->use_dhcp && !info.dhcp_enabled && info.ipaddr.s_addr != 0)
    {
        info.ipaddr.s_addr = info.netmask.s_addr = info.gw.s_addr = 0;
        ps2ip_setconfig(&info);
    }

    info.dhcp_enabled = config->use_dhcp;
    if (!config->use_dhcp)
    {
        info.ipaddr.s_addr = config->ip.addr;
        info.netmask.s_addr = config->netmask.addr;
        info.gw.s_addr = config->gateway.addr;
    }

    return ps2ip_setconfig(&info);
}

static int wait_for_address(t_ip_info *info)
{
    for (int second = 0; second <= DHCP_WAIT_SECONDS; second++)
    {
        if (ps2ip_getconfig("sm0", info) >= 0 && info->ipaddr.s_addr != 0)
        {
            return 0;
        }

        sleep(1);
    }

    return -1;
}

// Applies the settings and waits for the address. Returns 0 once there is one.
static int bring_up_address(const struct net_config *config)
{
    ui_set_address(NULL, NULL, NULL);
    if (apply_config(config) < 0)
    {
        fail(STATUS_FAILED_SETTINGS);
        return -1;
    }

    ui_status(config->use_dhcp ? STATUS_DHCP_WAIT : STATUS_STATIC_WAIT, NULL, STATUS_NO_PROGRESS);
    t_ip_info info;
    if (wait_for_address(&info) < 0)
    {
        fail(STATUS_DHCP_FAILED);
        return -1;
    }

    char ip[16], netmask[16], gateway[16];
    ui_set_address(format_address(ip, sizeof(ip), &info.ipaddr), format_address(netmask, sizeof(netmask), &info.netmask),
                   format_address(gateway, sizeof(gateway), &info.gw));
    return 0;
}

// Writes loadout-bay.cfg where Loadout Bay was started, and reads it back. On a
// problem, message says what went wrong in the screen's words.
static int write_settings(const struct ui_settings *settings, char *message, int size)
{
    char text[64 + CONFIG_PATH_LENGTH * 2];
    int length = snprintf(text, sizeof(text), "# Loadout Bay network settings, saved on the console.\n");
    if (settings->use_dhcp)
    {
        length += snprintf(text + length, sizeof(text) - length, "dhcp=on\n");
    }
    else
    {
        const unsigned char *a = settings->ip, *m = settings->netmask, *g = settings->gateway;
        length += snprintf(text + length, sizeof(text) - length,
                           "dhcp=off\nip=%u.%u.%u.%u\nnetmask=%u.%u.%u.%u\ngateway=%u.%u.%u.%u\n", a[0], a[1], a[2], a[3],
                           m[0], m[1], m[2], m[3], g[0], g[1], g[2], g[3]);
    }

    if (exit_elf[0] != '\0')
    {
        length += snprintf(text + length, sizeof(text) - length, "exit=%s\n", exit_elf);
    }

    if (settings_partition[0] != '\0' && files_mount_partition(settings_partition, 1) != 0)
    {
        snprintf(message, size, "The hard disk partition could not be opened.");
        return -1;
    }

    int status = -1;
    FILE *file = fopen(settings_path, "w");
    if (file == NULL)
    {
        snprintf(message, size, "The drive Loadout Bay started from is not there.");
    }
    else
    {
        size_t written = fwrite(text, 1, length, file);
        if (fclose(file) != 0 || written != (size_t)length)
        {
            snprintf(message, size, "The file could not be written. Is the drive full?");
        }
        else
        {
            char back[sizeof(text)];
            file = fopen(settings_path, "r");
            size_t read = file != NULL ? fread(back, 1, sizeof(back), file) : 0;
            if (file != NULL)
            {
                fclose(file);
            }

            if (read == (size_t)length && memcmp(back, text, length) == 0)
            {
                status = 0;
            }
            else
            {
                snprintf(message, size, "The file did not read back as written.");
            }
        }
    }

    if (settings_partition[0] != '\0')
    {
        files_close();
    }

    return status;
}

// Saves settings from the settings screen and makes them current. Returns 0 when they were saved.
static int take_settings(const struct ui_settings *settings)
{
    char message[72];
    if (write_settings(settings, message, sizeof(message)) < 0)
    {
        ui_settings_saved(0, message);
        return -1;
    }

    current.use_dhcp = settings->use_dhcp;
    const unsigned char *a = settings->ip, *m = settings->netmask, *g = settings->gateway;
    IP4_ADDR(&current.ip, a[0], a[1], a[2], a[3]);
    IP4_ADDR(&current.netmask, m[0], m[1], m[2], m[3]);
    IP4_ADDR(&current.gateway, g[0], g[1], g[2], g[3]);
    ui_set_network(current.use_dhcp, UI_SOURCE_FILE);
    ui_set_note("");
    ui_settings_saved(1, NULL);
    return 0;
}

// Brings up an address with the current settings, and with any the user saves
// on the settings screen until one works.
static void bring_up_until_address(void)
{
    while (bring_up_address(&current) < 0)
    {
        struct ui_settings settings;
        while (!ui_take_settings(&settings) || take_settings(&settings) < 0)
        {
            DelayThread(100 * 1000);
        }
    }
}

// The C library's startup would otherwise initialize pthreads, which Loadout Bay
// does not use, and that would link the pthread library into the ELF.
PS2_DISABLE_AUTOSTART_PTHREAD();

int main(int argc, char *argv[])
{
    upgrade_init(argc, argv);
    reset_iop();
    ui_init();
    ui_set_exit_handler(leave);
    ui_status(STATUS_STARTING, NULL, STATUS_NO_PROGRESS);

    if (load_modules() < 0)
    {
        SleepThread();
    }

    struct net_config config;
    choose_config(argc, argv, &config);
    snprintf(exit_elf, sizeof(exit_elf), "%s", config.exit_elf);
    current = config;

    NetManInit();
    struct ip4_addr none;
    IP4_ADDR(&none, 0, 0, 0, 0);
    ps2ipInit(config.use_dhcp ? &none : &config.ip,
              config.use_dhcp ? &none : &config.netmask,
              config.use_dhcp ? &none : &config.gateway);

    ui_status(STATUS_LINK_WAIT, NULL, STATUS_NO_PROGRESS);
    while (wait_for_link() < 0)
    {
        ui_set_link(UI_LINK_DOWN);
        ui_status(STATUS_LINK_DOWN, NULL, STATUS_NO_PROGRESS);
    }

    ui_set_link(UI_LINK_UP);
    bring_up_until_address();
    if (control_open() < 0 || install_open() < 0 || bench_open() < 0 || discovery_open() < 0)
    {
        fail(STATUS_FAILED_PORTS);
        SleepThread();
    }

    // One loop serves Loadout, the speed test and discovery, one connection at a time.
    ui_status(STATUS_READY, NULL, STATUS_NO_PROGRESS);
    for (;;)
    {
        fd_set readable;
        FD_ZERO(&readable);
        int highest = control_add_fd(&readable);
        int bench = bench_add_fds(&readable);
        int discovery = discovery_add_fd(&readable);
        highest = bench > highest ? bench : highest;
        highest = discovery > highest ? discovery : highest;
        // A short wait, so settings saved on the console are taken soon.
        struct timeval wait = {0, 100 * 1000};
        int ready = lwip_select(highest + 1, &readable, NULL, NULL, &wait);
        struct ui_settings settings;
        if (ui_take_settings(&settings))
        {
            if (take_settings(&settings) == 0)
            {
                bring_up_until_address();
                ui_status(STATUS_READY, "The new network settings are in use.", STATUS_NO_PROGRESS);
            }

            continue;
        }

        if (ready <= 0)
        {
            continue;
        }

        control_serve_ready(&readable, ui_status);
        bench_serve_ready(&readable, on_bench_report);
        discovery_serve_ready(&readable);
    }

    return 0;
}
