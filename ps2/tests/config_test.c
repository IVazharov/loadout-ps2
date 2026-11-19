// Tests of config.c built for the PC: `make -C ps2 test`.

#include "config.h"

#include <stdio.h>
#include <string.h>

static int failures;

static void expect_split(const char *path, const char *partition, const char *inner)
{
    char got_partition[40];
    char got_inner[200];
    if (!config_split_hdd_path(path, got_partition, sizeof(got_partition), got_inner, sizeof(got_inner)))
    {
        printf("FAIL %s: not split, expected \"%s\" and \"%s\"\n", path, partition, inner);
        failures++;
    }
    else if (strcmp(got_partition, partition) != 0 || strcmp(got_inner, inner) != 0)
    {
        printf("FAIL %s: \"%s\" and \"%s\", expected \"%s\" and \"%s\"\n", path, got_partition, got_inner, partition, inner);
        failures++;
    }
}

static void expect_no_split(const char *path, size_t partition_size, size_t inner_size)
{
    char partition[64];
    char inner[200];
    if (config_split_hdd_path(path, partition, partition_size, inner, inner_size))
    {
        printf("FAIL %s: split into \"%s\" and \"%s\", expected no split\n", path, partition, inner);
        failures++;
    }
}

static void expect_beside(const char *elf_path, const char *expected)
{
    char path[128];
    config_path_beside(elf_path, path, sizeof(path));
    if (strcmp(path, expected) != 0)
    {
        printf("FAIL beside %s: \"%s\", expected \"%s\"\n", elf_path, path, expected);
        failures++;
    }
}

int main(void)
{
    // OPL's Apps menu.
    expect_split("hdd0:+OPL:pfs0:APPS/Loadout-Bay/loadout-bay.elf", "+OPL", "APPS/Loadout-Bay/loadout-bay.elf");
    // ps2sdk's form, as in exit= lines.
    expect_split("hdd0:+OPL:pfs:/APPS/OPL/OPNPS2LD.ELF", "+OPL", "APPS/OPL/OPNPS2LD.ELF");
    // wLaunchELF: ps2sdk's loader joins "hdd0:<partition>" and "pfs0:/<path>".
    expect_split("hdd0:__commonpfs0:/loadout-bay.elf", "__common", "loadout-bay.elf");
    expect_split("hdd0:+OPLpfs0:/APPS/Loadout-Bay/loadout-bay.elf", "+OPL", "APPS/Loadout-Bay/loadout-bay.elf");
    expect_split("hdd0:+OPLpfs:/APPS/loadout-bay.elf", "+OPL", "APPS/loadout-bay.elf");
    // The partition's own digits stay with it: only "pfs" and the digits after it are the mount point.
    expect_split("hdd0:+OPL2pfs0:/APPS/Loadout-Bay/loadout-bay.elf", "+OPL2", "APPS/Loadout-Bay/loadout-bay.elf");
    expect_split("hdd0:+OPL2:pfs0:APPS/Loadout-Bay/loadout-bay.elf", "+OPL2", "APPS/Loadout-Bay/loadout-bay.elf");
    // A partition whose own name ends in "pfs" keeps it when a mount point follows.
    expect_split("hdd0:mypfs:pfs0:loadout-bay.elf", "mypfs", "loadout-bay.elf");
    expect_split("hdd0:mypfspfs0:/loadout-bay.elf", "mypfs", "loadout-bay.elf");

    // Not the hard disk.
    expect_no_split("mass:/APPS/loadout-bay.elf", 40, 200);
    expect_no_split("mc0:/APPS/loadout-bay.elf", 40, 200);
    expect_no_split("hdd1:+OPL:pfs0:loadout-bay.elf", 40, 200);
    // No partition, no mount point, or no file.
    expect_no_split("hdd0:", 40, 200);
    expect_no_split("hdd0:pfs0:/loadout-bay.elf", 40, 200);
    expect_no_split("hdd0:+OPL:/APPS/loadout-bay.elf", 40, 200);
    expect_no_split("hdd0:+OPL:APPS/loadout-bay.elf", 40, 200);
    expect_no_split("hdd0:+OPL:pfs0:", 40, 200);
    expect_no_split("hdd0:+OPL:pfs0:/", 40, 200);
    expect_no_split("hdd0:__commonpfs0:/", 40, 200);
    expect_no_split("hdd0:+OPL:pfsx:loadout-bay.elf", 40, 200);
    // A comma would start the partition's password.
    expect_no_split("hdd0:+OPL,secret:pfs0:loadout-bay.elf", 40, 200);
    expect_no_split("hdd0:+OPL,secretpfs0:/loadout-bay.elf", 40, 200);

    // The partition and the path must fit.
    expect_split("hdd0:abcdefghijklmnopqrstuvwxyz0123456789pfs0:/x.elf", "abcdefghijklmnopqrstuvwxyz0123456789", "x.elf");
    expect_no_split("hdd0:abcdefghij:pfs0:x.elf", 10, 200);
    expect_no_split("hdd0:abcdefghijpfs0:/x.elf", 10, 200);
    expect_no_split("hdd0:+OPL:pfs0:abcdefghij", 40, 10);

    expect_beside("mass:/APPS/loadout-bay.elf", "mass:/APPS/loadout-bay.cfg");
    expect_beside("mass:loadout-bay.elf", "mass:loadout-bay.cfg");
    expect_beside("pfs0:APPS/Loadout-Bay/loadout-bay.elf", "pfs0:APPS/Loadout-Bay/loadout-bay.cfg");

    if (failures != 0)
    {
        printf("%d failed\n", failures);
        return 1;
    }

    printf("config: all passed\n");
    return 0;
}
