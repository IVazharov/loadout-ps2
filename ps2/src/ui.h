// Loadout Bay's screens: the main screen with the status panel, the address
// card, the hard disk and the last speed test, and the network settings.
//
// The setters change what the screen shows, from any thread; the UI thread
// draws it and answers the controller.

#ifndef LOADOUT_UI_H
#define LOADOUT_UI_H

#include "status.h"

enum ui_link
{
    UI_LINK_UNKNOWN,
    UI_LINK_UP,
    UI_LINK_DOWN,
};

// Where the network settings came from.
enum ui_source
{
    UI_SOURCE_DEFAULT, // no loadout-bay.cfg, or the start folder is unknown: DHCP
    UI_SOURCE_FILE,    // loadout-bay.cfg
    UI_SOURCE_IGNORED, // loadout-bay.cfg has a mistake: DHCP
};

// Network settings as the settings screen saves them: DHCP, or a static
// address, its netmask, and the gateway, each in display order.
struct ui_settings
{
    int use_dhcp;
    unsigned char ip[4];
    unsigned char netmask[4];
    unsigned char gateway[4];
};

// Starts the UI thread, which reads the controller and draws the screen.
void ui_init(void);

// Called on the UI thread when the user leaves Loadout Bay; it does not return.
void ui_set_exit_handler(void (*handler)(void));

// Sets the status, its detail line (NULL for the status's own), and progress.
void ui_status(enum status status, const char *detail, int permille);

void ui_set_link(enum ui_link link);
void ui_set_network(int use_dhcp, enum ui_source source);

// Dotted addresses, or NULL while there is none yet.
void ui_set_address(const char *ip, const char *netmask, const char *gateway);

// The hard disk's size in GB, or 0 when there is none.
void ui_set_disk(unsigned size_gb);

// The part of it still free, in GB.
void ui_set_disk_free(unsigned free_gb);

void ui_set_speed(const char *result);

// A problem with loadout-bay.cfg, in one line, shown while Loadout Bay is ready.
void ui_set_note(const char *note);

// Where saving writes loadout-bay.cfg, in words such as "USB drive › APPS/loadout-bay.cfg",
// or NULL when Loadout Bay cannot save where it was started from.
void ui_set_settings_place(const char *place);

// On the main thread: takes the settings the user chose to save, once. Returns 1 when there are some.
int ui_take_settings(struct ui_settings *settings);

// On the main thread: how saving and applying them went; message says why it failed.
void ui_settings_saved(int ok, const char *message);

#endif
