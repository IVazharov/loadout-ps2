// Stands in for ps2sdk's ps2ip.h when the tests build config.c on a PC:
// config.c uses only lwIP's IPv4 address type and the macro that fills one.

#ifndef LOADOUT_TEST_PS2IP_H
#define LOADOUT_TEST_PS2IP_H

#include <stdint.h>

struct ip4_addr
{
    uint32_t addr;
};

#define IP4_ADDR(address, a, b, c, d) \
    ((address)->addr = ((uint32_t)(a) << 24) | ((uint32_t)(b) << 16) | ((uint32_t)(c) << 8) | (uint32_t)(d))

#endif
