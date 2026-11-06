// Mounting and PFS file access have no POSIX form here, so this file calls fileXio directly.
#define NEWLIB_PORT_AWARE

#include "files.h"
#include "install.h"
#include "wire.h"

#include <ctype.h>
#include <errno.h>
#include <hdd-ioctl.h>
#include <io_common.h>
#include <iox_stat.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#include <fileXio_rpc.h>

#define MOUNT_POINT "pfs0:"
#define PARTITION_NAME_MAX 32
#define CONF_HDD_BYTES 512
#define PATH_BYTES 128

// 1 while OPL's partition is mounted, 2 while files_mount_partition's is.
static int mounted;
// Where OPL's folders are: "pfs0:" for a partition named +..., else "pfs0:OPL/".
static const char *prefix;
static u8 conf[CONF_HDD_BYTES] __attribute__((aligned(64)));

static int valid_name(const char *name)
{
    size_t length = strnlen(name, FILES_NAME_MAX + 1);
    if (length == 0 || length > FILES_NAME_MAX || strcmp(name, ".") == 0 || strcmp(name, "..") == 0)
    {
        return 0;
    }

    for (size_t i = 0; i < length; i++)
    {
        unsigned char c = (unsigned char)name[i];
        if (!isalnum(c) && c != '_' && c != '.' && c != '-')
        {
            return 0;
        }
    }

    return 1;
}

// The partition OPL keeps its files in, as hddFindOPLPartition finds it.
// A partition name could start an hdd0: password with a comma, so one with
// anything but letters, digits, '+', '_', '.' or '-' is refused with -ENOTDIR:
// using another partition would put files where OPL does not look.
static int opl_partition(char *name)
{
    strcpy(name, "+OPL");
    if (fileXioMount(MOUNT_POINT, "hdd0:__common", FIO_MT_RDONLY) < 0)
    {
        return 0;
    }

    int fd = fileXioOpen(MOUNT_POINT "OPL/conf_hdd.cfg", FIO_O_RDONLY);
    int length = fd >= 0 ? fileXioRead(fd, conf, sizeof(conf) - 1) : -1;
    if (fd >= 0)
    {
        fileXioClose(fd);
    }

    fileXioUmount(MOUNT_POINT);
    if (length <= 0)
    {
        return 0;
    }

    conf[length] = '\0';
    const char *value = strstr((const char *)conf, "hdd_partition=");
    if (value == NULL)
    {
        return 0;
    }

    value += strlen("hdd_partition=");
    size_t end = 0;
    while (end < PARTITION_NAME_MAX && (isalnum((unsigned char)value[end]) || strchr("+_.-", value[end]) != NULL)
           && value[end] != '\0')
    {
        end++;
    }

    if (end == 0 || (value[end] != '\r' && value[end] != '\n' && value[end] != '\0'))
    {
        return -ENOTDIR;
    }

    memcpy(name, value, end);
    name[end] = '\0';
    return 0;
}

static int mount_opl(void)
{
    if (mounted == 1)
    {
        return 0;
    }

    files_close();

    // An install writes through the same driver; files wait until it is over.
    if (install_busy())
    {
        return -EBUSY;
    }

    char partition[PARTITION_NAME_MAX + 1];
    int status = opl_partition(partition);
    if (status < 0)
    {
        return status;
    }

    char device[48];
    snprintf(device, sizeof(device), "hdd0:%s", partition);
    status = fileXioMount(MOUNT_POINT, device, FIO_MT_RDWR);
    if (status < 0)
    {
        return status;
    }

    prefix = partition[0] == '+' ? MOUNT_POINT : MOUNT_POINT "OPL/";
    mounted = 1;
    return 0;
}

// Mounts OPL's partition and fills path with the folder, or the file in it when name is given.
static int prepare(u32 folder, const char *name, const char *suffix, char *path)
{
    static const char *const folders[] = {"ART", "CFG", "VMC", "APPS/Loadout-Bay"};
    if (folder != FILES_FOLDER_ART && folder != FILES_FOLDER_CFG && folder != FILES_FOLDER_VMC
        && folder != FILES_FOLDER_APP)
    {
        return -EINVAL;
    }

    size_t length = name != NULL ? strlen(name) : 0;
    if (name != NULL
        && (!valid_name(name) || (folder == FILES_FOLDER_VMC && (length < 5 || strcmp(name + length - 4, ".bin") != 0))))
    {
        return -EPERM;
    }

    if (install_busy())
    {
        return -EBUSY;
    }

    int status = mount_opl();
    if (status < 0)
    {
        return status;
    }

    snprintf(path, PATH_BYTES, "%s%s%s%s%s", prefix, folders[folder], name != NULL ? "/" : "", name != NULL ? name : "",
             suffix);
    return 0;
}

int files_list(u32 folder, u32 first, u8 *out, u32 *total, u32 *count)
{
    *total = 0;
    *count = 0;
    char path[PATH_BYTES];
    int status = prepare(folder, NULL, "", path);
    if (status < 0)
    {
        return status;
    }

    int dir = fileXioDopen(path);
    if (dir == -ENOENT)
    {
        // OPL makes its folders when it first runs; until then there are no files.
        return 0;
    }

    if (dir < 0)
    {
        return dir;
    }

    iox_dirent_t entry;
    while (fileXioDread(dir, &entry) > 0)
    {
        if (!FIO_S_ISREG(entry.stat.mode) || !valid_name(entry.name))
        {
            continue;
        }

        if (*total >= first && *count < FILES_LIST_PAGE)
        {
            u8 *slot = out + *count * FILES_ENTRY_BYTES;
            memset(slot, 0, FILES_ENTRY_BYTES);
            memcpy(slot, entry.name, strlen(entry.name));
            wire_write_u32(slot + FILES_NAME_BYTES, entry.stat.size);
            (*count)++;
        }

        (*total)++;
    }

    fileXioDclose(dir);
    return 0;
}

int files_read(u32 folder, const char *name, u32 offset, u32 length, u8 *out)
{
    if (length > FILES_CHUNK_MAX)
    {
        return -EINVAL;
    }

    char path[PATH_BYTES];
    int status = prepare(folder, name, "", path);
    if (status < 0)
    {
        return status;
    }

    int fd = fileXioOpen(path, FIO_O_RDONLY);
    if (fd < 0)
    {
        return fd;
    }

    status = fileXioLseek(fd, (int)offset, SEEK_SET);
    if (status >= 0)
    {
        status = fileXioRead(fd, out, (int)length);
    }

    fileXioClose(fd);
    return status;
}

int files_write(u32 folder, const char *name, u32 offset, const u8 *data, u32 length)
{
    if (length == 0 || length > FILES_CHUNK_MAX)
    {
        return -EINVAL;
    }

    char path[PATH_BYTES];
    int status = prepare(folder, name, ".part", path);
    if (status < 0)
    {
        return status;
    }

    // A card may hold saves, so a new one is refused at its first chunk when the name is taken.
    char target[PATH_BYTES];
    iox_stat_t stat;
    if (folder == FILES_FOLDER_VMC && offset == 0 && prepare(folder, name, "", target) == 0
        && fileXioGetStat(target, &stat) >= 0)
    {
        return -EEXIST;
    }

    // OPL makes its folders when it first runs; a new OPL partition may not have them yet.
    char dir[PATH_BYTES];
    prepare(folder, NULL, "", dir);
    if (strcmp(prefix, MOUNT_POINT) != 0)
    {
        fileXioMkdir(MOUNT_POINT "OPL", 0777);
    }

    if (folder == FILES_FOLDER_APP)
    {
        char apps[PATH_BYTES];
        snprintf(apps, sizeof(apps), "%sAPPS", prefix);
        fileXioMkdir(apps, 0777);
    }

    fileXioMkdir(dir, 0777);

    int fd = fileXioOpen(path, FIO_O_WRONLY | FIO_O_CREAT | (offset == 0 ? FIO_O_TRUNC : 0), 0666);
    if (fd < 0)
    {
        return fd;
    }

    // Chunks come in order, so a gap or an overlap means one went missing.
    int size = fileXioLseek(fd, 0, SEEK_END);
    status = size < 0 ? size : (u32)size != offset ? -EINVAL : fileXioWrite(fd, (void *)data, (int)length);
    fileXioClose(fd);
    return status < 0 ? status : (u32)status == length ? 0 : -EIO;
}

int files_commit(u32 folder, const char *name, u32 size)
{
    char part[PATH_BYTES];
    char path[PATH_BYTES];
    int status = prepare(folder, name, ".part", part);
    if (status == 0)
    {
        status = prepare(folder, name, "", path);
    }

    if (status < 0)
    {
        return status;
    }

    iox_stat_t stat;
    status = fileXioGetStat(part, &stat);
    if (status < 0)
    {
        return status;
    }

    if (stat.size != size)
    {
        return -EPIPE;
    }

    // A card may hold saves: replacing one takes a FILE_DELETE first.
    if (folder == FILES_FOLDER_VMC && fileXioGetStat(path, &stat) >= 0)
    {
        fileXioRemove(part);
        fileXioSync(MOUNT_POINT, FXIO_WAIT);
        return -EEXIST;
    }

    // PFS does not rename over a file, so the old one goes first. Until then it stays whole.
    status = fileXioRemove(path);
    if (status < 0 && status != -ENOENT)
    {
        return status;
    }

    status = fileXioRename(part, path);
    fileXioSync(MOUNT_POINT, FXIO_WAIT);
    return status;
}

int files_delete(u32 folder, const char *name)
{
    char path[PATH_BYTES];
    int status = prepare(folder, name, "", path);
    if (status < 0)
    {
        return status;
    }

    status = fileXioRemove(path);
    fileXioSync(MOUNT_POINT, FXIO_WAIT);
    return status;
}

// OPL's hddGetFileBlockInfo reads the inode's count of block runs from the
// disk; ps2sdk's PFS driver hands the same count out in the stat's private_3.
int files_runs(u32 folder, const char *name, u32 *runs)
{
    char path[PATH_BYTES];
    int status = prepare(folder, name, "", path);
    if (status < 0)
    {
        return status;
    }

    iox_stat_t stat;
    status = fileXioGetStat(path, &stat);
    if (status < 0)
    {
        return status;
    }

    *runs = stat.private_3;
    return 0;
}

// PFS counts its space in zones: the free ones times the zone size, as wLaunchELF reads it.
int files_space(u64 *free_bytes)
{
    *free_bytes = 0;
    if (install_busy())
    {
        return -EBUSY;
    }

    int status = mount_opl();
    if (status < 0)
    {
        return status;
    }

    int zone = fileXioDevctl(MOUNT_POINT, PDIOC_ZONESZ, NULL, 0, NULL, 0);
    int zones = fileXioDevctl(MOUNT_POINT, PDIOC_ZONEFREE, NULL, 0, NULL, 0);
    if (zone < 0 || zones < 0)
    {
        return zone < 0 ? zone : zones;
    }

    *free_bytes = (u64)zone * (u64)zones;
    return 0;
}

void files_close(void)
{
    if (mounted)
    {
        fileXioSync(MOUNT_POINT, FXIO_WAIT);
        fileXioUmount(MOUNT_POINT);
        mounted = 0;
    }
}

int files_mount_partition(const char *partition, int writable)
{
    if (install_busy())
    {
        return -EBUSY;
    }

    files_close();
    char device[48];
    snprintf(device, sizeof(device), "hdd0:%s", partition);
    int status = fileXioMount(MOUNT_POINT, device, writable ? FIO_MT_RDWR : FIO_MT_RDONLY);
    if (status < 0)
    {
        return status;
    }

    // 2 marks a partition other than OPL's, so the next file command mounts OPL's again.
    mounted = 2;
    return 0;
}
