// What the main screen (ui.c) and the network settings screen (netscreen.c)
// share on the UI thread.

#ifndef LOADOUT_UI_INTERNAL_H
#define LOADOUT_UI_INTERNAL_H

#include "ui.h"
#include "widgets.h"

enum save_state
{
    SAVE_IDLE,
    SAVE_PENDING,
    SAVE_DONE,
    SAVE_FAILED,
};

// What the screens show. The main thread changes it through ui.h; the UI thread draws a copy.
struct model
{
    enum status status;
    char detail[WIDGET_TEXT_LENGTH];
    int permille;
    int connected;
    enum ui_link link;
    int use_dhcp;
    enum ui_source source;
    int has_address;
    char ip[16], netmask[16], gateway[16];
    unsigned disk_gb;
    int disk_known;
    unsigned disk_free_gb;
    int disk_free_known;
    char speed[WIDGET_TEXT_LENGTH];
    char note[WIDGET_TEXT_LENGTH];
    char place[96];
    int can_save;
    enum save_state save;
    char save_message[WIDGET_TEXT_LENGTH];
};

// On the UI thread: hands settings to the main thread to save and apply.
void ui_request_save(const struct ui_settings *settings);

// The network settings screen. netscreen_input returns 1 when the screen closes.
void netscreen_open(const struct model *m, unsigned t);
int netscreen_input(unsigned pressed, const struct model *m, unsigned t);
void netscreen_draw(const struct model *m, unsigned t);
int netscreen_animating(const struct model *m);

#endif
