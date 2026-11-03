// Discovery: answers Loadout's UDP probe, so the PC can find Loadout Bay on the
// network without knowing its address. docs/bay-protocol.md describes the format.

#ifndef LOADOUT_DISCOVERY_H
#define LOADOUT_DISCOVERY_H

#include <ps2ip.h>

#define DISCOVERY_PORT 45060

int discovery_open(void);
int discovery_add_fd(fd_set *set);
void discovery_serve_ready(fd_set *readable);

#endif
