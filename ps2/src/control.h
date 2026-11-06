// The control protocol: Loadout reads the PS2's game library, installs games, and deletes and edits them over it.
// docs/bay-protocol.md describes the wire format.

#ifndef LOADOUT_CONTROL_H
#define LOADOUT_CONTROL_H

#include <ps2ip.h>

#include "status.h"

#define CONTROL_PORT 45060
#define CONTROL_PROTOCOL_VERSION 8

// Opens the control port. Returns 0, or -1 if it cannot be opened.
int control_open(void);

// Adds the control socket to a select set and returns it.
int control_add_fd(fd_set *set);

// When select found the control socket readable, accepts Loadout and serves its
// requests until it disconnects or stays idle too long.
void control_serve_ready(fd_set *readable, status_report report);

#endif
