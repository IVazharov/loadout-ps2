#include "layout.h"

// Partition sizes in 2048-byte sectors, and their names in APA paths.
static const struct
{
    unsigned int sectors;
    const char *name;
} sizes[LAYOUT_SIZES] = {
    {65536, "128M"}, {131072, "256M"}, {262144, "512M"}, {524288, "1G"}, {1048576, "2G"}, {2097152, "4G"},
};

// Reserved 2048-byte sectors: 4 MB in a main partition. A sub-partition's
// header takes two 512-byte sectors, rounded up to one whole 2048-byte sector.
static unsigned int reserved(int part)
{
    return part == 0 ? 0x800 : 1;
}

int layout_choose(unsigned int sectors_left, unsigned int max_part_sectors, int part)
{
    // Start from the largest size the disk allows.
    unsigned int limit = max_part_sectors / 4;
    int size = LAYOUT_SIZES - 1;
    while (size > 0 && sizes[size].sectors > limit)
    {
        size--;
    }

    // This part can hold no more than that, whatever is left of the game.
    unsigned int wanted = layout_capacity(size, part);
    if (sectors_left < wanted)
    {
        wanted = sectors_left;
    }

    // Come down to the smallest size that still holds it.
    while (size > 0 && layout_capacity(size - 1, part) >= wanted)
    {
        size--;
    }

    // A size that would leave 128 MB or more unused gives way to the next one
    // down, and the rest of the game goes to the next part.
    if (size > 0 && sizes[size].sectors - wanted >= sizes[0].sectors)
    {
        size--;
    }

    return size;
}

const char *layout_size_name(int size)
{
    return sizes[size].name;
}

unsigned int layout_capacity(int size, int part)
{
    return sizes[size].sectors - reserved(part);
}

unsigned int layout_reserved_sectors(int part)
{
    return part == 0 ? 0x2000 : 4;
}
