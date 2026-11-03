// Loadout Bay network settings from loadout-bay.cfg.
//
// The file sits next to the ELF and holds key=value lines:
//
//   dhcp=off
//   ip=192.168.1.50
//   netmask=255.255.255.0
//   gateway=192.168.1.1
//   exit=mc0:/APPS/OPNPS2LD.ELF
//
// A missing file means DHCP. With dhcp=on the addresses are ignored. Without a
// dhcp line, the file means a static address. Blank lines and lines starting
// with # are skipped. exit names an ELF that Exit starts instead of returning
// to the PS2's menu, in the forms of argv[0] (an OPL app on the hard disk is
// "hdd0:+OPL:pfs:/APPS/...").

#ifndef LOADOUT_CONFIG_H
#define LOADOUT_CONFIG_H

#include <ps2ip.h>
#include <stddef.h>

#define CONFIG_FILE_NAME "loadout-bay.cfg"
#define CONFIG_PATH_LENGTH 128

enum config_result
{
    CONFIG_LOADED,
    CONFIG_MISSING,
    CONFIG_INVALID,
};

struct net_config
{
    int use_dhcp;
    struct ip4_addr ip;
    struct ip4_addr netmask;
    struct ip4_addr gateway;
    // Empty for the PS2's menu.
    char exit_elf[CONFIG_PATH_LENGTH];
};

// Writes the path of loadout-bay.cfg in the folder of elf_path, for example
// "mass:/APPS/loadout-bay.elf" gives "mass:/APPS/loadout-bay.cfg".
void config_path_beside(const char *elf_path, char *path, size_t size);

// An ELF that OPL starts from the hard disk gets a path such as
// "hdd0:+OPL:pfs0:APPS/Loadout-Bay/loadout-bay.elf": the partition, then the
// path on it while it was mounted. Splits that into "+OPL" and
// "APPS/Loadout-Bay/loadout-bay.elf" and returns 1; returns 0 for any other path.
int config_split_hdd_path(const char *elf_path, char *partition, size_t partition_size, char *inner, size_t inner_size);

// Reads the file at path. On CONFIG_INVALID, error says what is wrong in one line.
enum config_result config_load(const char *path, struct net_config *config, char *error, size_t error_size);

#endif
