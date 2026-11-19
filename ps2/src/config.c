#include "config.h"

#include <ctype.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>

#define MAX_FILE_BYTES 2048

// Parses "a.b.c.d" into an lwIP address.
static int parse_address(const char *text, struct ip4_addr *address)
{
    unsigned int a, b, c, d;
    char extra;
    if (sscanf(text, "%u.%u.%u.%u%c", &a, &b, &c, &d, &extra) != 4 || a > 255 || b > 255 || c > 255 || d > 255)
    {
        return 0;
    }

    IP4_ADDR(address, a, b, c, d);
    return 1;
}

static int parse_switch(const char *text, int *value)
{
    if (strcasecmp(text, "on") == 0 || strcasecmp(text, "yes") == 0 || strcmp(text, "1") == 0)
    {
        *value = 1;
        return 1;
    }

    if (strcasecmp(text, "off") == 0 || strcasecmp(text, "no") == 0 || strcmp(text, "0") == 0)
    {
        *value = 0;
        return 1;
    }

    return 0;
}

static char *trim(char *text)
{
    while (isspace((unsigned char)*text))
    {
        text++;
    }

    char *end = text + strlen(text);
    while (end > text && isspace((unsigned char)end[-1]))
    {
        *--end = '\0';
    }

    return text;
}

void config_path_beside(const char *elf_path, char *path, size_t size)
{
    const char *slash = strrchr(elf_path, '/');
    const char *colon = strchr(elf_path, ':');
    const char *end = slash != NULL ? slash + 1 : colon != NULL ? colon + 1 : elf_path;
    snprintf(path, size, "%.*s%s", (int)(end - elf_path), elf_path, CONFIG_FILE_NAME);
}

// The length of the mount point at the start of text: "pfs" and any digits after it, or 0 without one.
static size_t mount_point_length(const char *text, size_t size)
{
    if (size < 3 || strncmp(text, "pfs", 3) != 0)
    {
        return 0;
    }

    size_t length = 3;
    while (length < size && isdigit((unsigned char)text[length]))
    {
        length++;
    }

    return length;
}

int config_split_hdd_path(const char *elf_path, char *partition, size_t partition_size, char *inner, size_t inner_size)
{
    if (strncmp(elf_path, "hdd0:", 5) != 0)
    {
        return 0;
    }

    // A partition name with anything but these could start an hdd0: password.
    const char *name = elf_path + 5;
    size_t field = strspn(name, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+_.-");
    if (field == 0 || name[field] != ':')
    {
        return 0;
    }

    // Then the mount point the path was written for: "pfs0:" from OPL, "pfs:" in the ps2sdk form.
    size_t length = field;
    const char *rest = name + field + 1;
    size_t mount = mount_point_length(rest, strlen(rest));
    if (mount > 0 && rest[mount] == ':')
    {
        rest += mount + 1;
    }
    else
    {
        // ps2sdk's loader puts the partition and the path it was given together with nothing between, so
        // wLaunchELF, which gives it "hdd0:__common" and "pfs0:/loadout-bay.elf", starts the ELF as
        // "hdd0:__commonpfs0:/loadout-bay.elf". The mount point then ends the partition's field.
        size_t digits = 0;
        while (digits < field && isdigit((unsigned char)name[field - 1 - digits]))
        {
            digits++;
        }

        if (field < digits + 4 || mount_point_length(name + field - digits - 3, digits + 3) != digits + 3)
        {
            return 0;
        }

        length = field - digits - 3;
    }

    rest += strspn(rest, "/");
    if (length >= partition_size || *rest == '\0' || strlen(rest) >= inner_size)
    {
        return 0;
    }

    memcpy(partition, name, length);
    partition[length] = '\0';
    strcpy(inner, rest);
    return 1;
}

enum config_result config_load(const char *path, struct net_config *config, char *error, size_t error_size)
{
    static char text[MAX_FILE_BYTES + 1];
    FILE *file = fopen(path, "r");
    if (file == NULL)
    {
        return CONFIG_MISSING;
    }

    size_t length = fread(text, 1, MAX_FILE_BYTES, file);
    fclose(file);
    text[length] = '\0';

    int use_dhcp = 0;
    int have_ip = 0, have_netmask = 0, have_gateway = 0;
    struct net_config parsed;
    memset(&parsed, 0, sizeof(parsed));

    int line_number = 0;
    for (char *line = strtok(text, "\r\n"); line != NULL; line = strtok(NULL, "\r\n"))
    {
        line_number++;
        line = trim(line);
        if (*line == '\0' || *line == '#')
        {
            continue;
        }

        char *equals = strchr(line, '=');
        if (equals == NULL)
        {
            snprintf(error, error_size, CONFIG_FILE_NAME " line %d has no '='.", line_number);
            return CONFIG_INVALID;
        }

        *equals = '\0';
        char *key = trim(line);
        char *value = trim(equals + 1);
        int ok;
        if (strcasecmp(key, "dhcp") == 0)
        {
            ok = parse_switch(value, &use_dhcp);
        }
        else if (strcasecmp(key, "ip") == 0)
        {
            ok = have_ip = parse_address(value, &parsed.ip);
        }
        else if (strcasecmp(key, "netmask") == 0)
        {
            ok = have_netmask = parse_address(value, &parsed.netmask);
        }
        else if (strcasecmp(key, "gateway") == 0)
        {
            ok = have_gateway = parse_address(value, &parsed.gateway);
        }
        else if (strcasecmp(key, "exit") == 0)
        {
            // Empty means the PS2's menu; anything else names a device.
            ok = strlen(value) < sizeof(parsed.exit_elf) && (*value == '\0' || strchr(value, ':') != NULL);
            if (ok)
            {
                strcpy(parsed.exit_elf, value);
            }
        }
        else
        {
            snprintf(error, error_size, CONFIG_FILE_NAME " line %d: unknown setting '%.16s'.", line_number, key);
            return CONFIG_INVALID;
        }

        if (!ok)
        {
            snprintf(error, error_size, CONFIG_FILE_NAME " line %d: '%.16s' is not valid.", line_number, value);
            return CONFIG_INVALID;
        }
    }

    if (!use_dhcp && !(have_ip && have_netmask && have_gateway))
    {
        snprintf(error, error_size, CONFIG_FILE_NAME " needs ip, netmask, and gateway when dhcp is off.");
        return CONFIG_INVALID;
    }

    parsed.use_dhcp = use_dhcp;
    *config = parsed;
    return CONFIG_LOADED;
}
