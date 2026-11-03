// What Loadout Bay is doing, as the status panel shows it.
//
// Each status has its own headline, colour, and icon on the screen (ui.c); the
// code that reports it adds a detail line, and a progress in thousandths when
// it knows one.

#ifndef LOADOUT_STATUS_H
#define LOADOUT_STATUS_H

enum status
{
    STATUS_STARTING,
    STATUS_LINK_WAIT,
    STATUS_LINK_DOWN,
    STATUS_DHCP_WAIT,
    STATUS_STATIC_WAIT,
    STATUS_DHCP_FAILED,
    STATUS_READY,
    STATUS_CONNECTED,
    STATUS_INSTALLING,
    STATUS_CHECKING,
    STATUS_FINISHING,
    STATUS_INSTALLED,
    STATUS_INSTALL_FAILED,
    STATUS_CHECK_FAILED,
    STATUS_BENCH,
    STATUS_BENCH_DONE,
    STATUS_BENCH_FAILED,
    STATUS_UPDATING,
    STATUS_UPDATE_FAILED,
    STATUS_FAILED_DRIVERS,
    STATUS_FAILED_NETWORK,
    STATUS_FAILED_SETTINGS,
    STATUS_FAILED_PORTS,
    STATUS_COUNT,
};

#define STATUS_NO_PROGRESS (-1)

// Reports a status with its detail line (NULL for none) and progress (0-1000, or STATUS_NO_PROGRESS).
typedef void (*status_report)(enum status status, const char *detail, int permille);

#endif
