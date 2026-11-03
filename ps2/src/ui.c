#include "ui.h"

#include <delaythread.h>
#include <kernel.h>
#include <libpad.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

#include "pad.h"
#include "ui_internal.h"

// Animation runs at this rate; a still screen is drawn only when it changes.
#define FRAME_MS 66
// During a transfer the screen shares the CPU with the network. On the console a speed test ran at 6.41 MB/s
// with FRAME_MS and at 8.1 with this. It also matches bench.c's progress.
#define BUSY_FRAME_MS 1000
// Even a button or a status change waits this long then, so nothing can bring the fast redraws back.
#define BUSY_FORCED_MS 200
#define IDLE_MS 16
// A button press right after a dialog opens is not taken, so a held button does not carry over.
#define SETTLE_MS 250
#define REPEAT_DELAY_MS 400
#define REPEAT_MS 80
#define TOAST_MS 3000

struct status_copy
{
    enum kind kind;
    const char *headline;
    const char *detail;
};

static const struct status_copy copy[STATUS_COUNT] = {
    [STATUS_STARTING] = {KIND_BUSY, "Starting", "Loading the drivers."},
    [STATUS_LINK_WAIT] = {KIND_BUSY, "Waiting for network", "Waiting for the cable link."},
    [STATUS_LINK_DOWN] = {KIND_WARN, "No network cable", "Check the cable. Still trying\xE2\x80\xA6"},
    [STATUS_DHCP_WAIT] = {KIND_BUSY, "Getting an address", "Asking the DHCP server."},
    [STATUS_STATIC_WAIT] = {KIND_BUSY, "Setting the address", "From loadout-bay.cfg."},
    [STATUS_DHCP_FAILED] = {KIND_ERROR, "No address from DHCP", "Check the router, or open Network."},
    [STATUS_READY] = {KIND_READY, "Ready for the PC", "Open Loadout on the PC and connect."},
    [STATUS_CONNECTED] = {KIND_CONNECTED, "Loadout connected", ""},
    [STATUS_INSTALLING] = {KIND_BUSY, "Installing a game", "Receiving the game\xE2\x80\xA6"},
    [STATUS_CHECKING] = {KIND_BUSY, "Checking the copy", ""},
    [STATUS_FINISHING] = {KIND_BUSY, "Finishing the install", "Writing the last data\xE2\x80\xA6"},
    [STATUS_INSTALLED] = {KIND_DONE, "Game installed", "Ready for the next one."},
    [STATUS_INSTALL_FAILED] = {KIND_ERROR, "Install didn't finish", "OPL won't list it. Install it again."},
    [STATUS_CHECK_FAILED] = {KIND_ERROR, "Copy check failed", "OPL won't list it. Install it again."},
    [STATUS_BENCH] = {KIND_BUSY, "Speed test", ""},
    [STATUS_BENCH_DONE] = {KIND_DONE, "Speed test done", ""},
    [STATUS_BENCH_FAILED] = {KIND_ERROR, "Speed test failed", ""},
    [STATUS_UPDATING] = {KIND_BUSY, "Updating Loadout Bay", "Starting the new version\xE2\x80\xA6"},
    [STATUS_UPDATE_FAILED] = {KIND_ERROR, "Update didn't start", "Start Loadout Bay again."},
    [STATUS_FAILED_DRIVERS] = {KIND_ERROR, "Drivers didn't load", "Exit and start Loadout Bay again."},
    [STATUS_FAILED_NETWORK] = {KIND_ERROR, "Network didn't start", "Is the network adapter fitted?"},
    [STATUS_FAILED_SETTINGS] = {KIND_ERROR, "Settings not applied", "Check them under Network."},
    [STATUS_FAILED_PORTS] = {KIND_ERROR, "Ports didn't open", "Exit and start Loadout Bay again."},
};

// The setters run on the main thread and the drawing on the UI thread; lock guards the model.
static struct model model;
static int lock = -1;
static volatile int dirty = 1;
static void (*exit_handler)(void);
static struct ui_settings requested;
static int request_pending;

enum screen
{
    SCREEN_MAIN,
    SCREEN_EXIT,
    SCREEN_NETWORK,
};

// The UI thread's own state.
static enum screen screen;
static int focus;
static unsigned screen_opened;
static char toast[WIDGET_TEXT_LENGTH];
static unsigned toast_until;
static int leaving;

static unsigned now_ms(void)
{
    return (unsigned)((unsigned long long)clock() * 1000 / CLOCKS_PER_SEC);
}

static void lock_model(void)
{
    WaitSema(lock);
}

static void unlock_model(void)
{
    dirty = 1;
    SignalSema(lock);
}

// The work that locks the controls. Leaving or changing the network now would break it.
static int busy(enum status status)
{
    switch (status)
    {
    case STATUS_INSTALLING:
    case STATUS_CHECKING:
    case STATUS_FINISHING:
    case STATUS_BENCH:
    case STATUS_UPDATING:
        return 1;
    default:
        return 0;
    }
}

// A chip that ends at x_right: a 20-pixel icon, then a word. Returns its left edge.
// link_state is the link chip's state, or -1 for the address mode chip.
static float draw_chip(float x_right, const char *word, int link_state, int dhcp)
{
    int width = 10 + 20 + 6 + gfx_text_width(FONT_LABEL, word) + 10;
    float x = x_right - width;
    gfx_panel(x, 198, width, 28, 4, SURFACE_CHIP, GFX_OPAQUE);
    float cx = x + 20, cy = 212;
    if (link_state >= 0)
    {
        if (link_state == UI_LINK_UP)
        {
            gfx_disc(cx, cy, 6, STATE_OK, GFX_OPAQUE);
        }
        else
        {
            gfx_ring(cx, cy, 7, 2.5f, link_state == UI_LINK_DOWN ? STATE_WARN : TEXT_DISABLED, GFX_OPAQUE);
        }
    }
    else if (dhcp)
    {
        gfx_ring(cx, cy, 7, 2.5f, TEXT_PRIMARY, GFX_OPAQUE);
        gfx_rect(cx + 2, cy - 9, 7, 7, SURFACE_CHIP, GFX_OPAQUE);
        gfx_triangle(cx + 1, cy - 11, cx + 1, cy - 3, cx + 6, cy - 7, TEXT_PRIMARY, GFX_OPAQUE);
    }
    else
    {
        gfx_disc(cx, cy - 3, 4.5f, TEXT_PRIMARY, GFX_OPAQUE);
        gfx_rect(cx - 1.25f, cy, 2.5f, 9, TEXT_PRIMARY, GFX_OPAQUE);
    }

    gfx_text(FONT_LABEL, x + 36, 220, word, TEXT_PRIMARY, GFX_LEFT);
    return x;
}

static enum kind shown_kind(const struct model *m)
{
    if (m->status == STATUS_READY && m->detail[0] == '\0' && m->note[0] != '\0')
    {
        return KIND_WARN;
    }

    return copy[m->status].kind;
}

static void draw_status(const struct model *m, unsigned t)
{
    const struct status_copy *c = &copy[m->status];
    enum kind kind = shown_kind(m);
    const char *detail = m->detail[0] != '\0' ? m->detail : kind == KIND_WARN && m->status == STATUS_READY ? m->note : c->detail;

    gfx_panel(64, 96, 512, 84, 6, SURFACE_2, GFX_OPAQUE);
    gfx_rect(64, 96, 8, 84, widget_kind_color(kind), GFX_OPAQUE);
    widget_icon(kind, 88, 104, t);
    gfx_text(FONT_HEADLINE, 128, 126, c->headline, TEXT_PRIMARY, GFX_LEFT);
    char cut[WIDGET_TEXT_LENGTH * 2];
    gfx_text(FONT_BODY, 128, 154, widget_fit(FONT_BODY, detail, 424, cut, sizeof(cut)), TEXT_SECONDARY, GFX_LEFT);
    if (m->permille >= 0)
    {
        int permille = m->permille > 1000 ? 1000 : m->permille;
        gfx_rect(128, 164, 368, 8, OUTLINE, GFX_OPAQUE);
        gfx_rect(128, 164, 368.0f * permille / 1000, 8, STATE_BUSY, GFX_OPAQUE);
        char percent[8];
        snprintf(percent, sizeof(percent), "%d%%", permille / 10);
        gfx_text(FONT_LABEL, 552, 174, percent, TEXT_PRIMARY, GFX_RIGHT);
    }
}

static void draw_address(const struct model *m)
{
    gfx_panel(64, 192, 512, 104, 6, SURFACE_1, GFX_OPAQUE);
    gfx_text(FONT_LABEL, 88, 216, "ADDRESS", TEXT_LABEL, GFX_LEFT);

    const char *mode = m->source == UI_SOURCE_IGNORED ? "DHCP \xC2\xB7 CFG IGNORED"
                       : m->source == UI_SOURCE_FILE  ? (m->use_dhcp ? "DHCP \xC2\xB7 CFG" : "STATIC \xC2\xB7 CFG")
                                                      : "DHCP";
    float left = draw_chip(552, mode, -1, m->use_dhcp);
    draw_chip(left - 8, m->link == UI_LINK_DOWN ? "NO LINK" : m->link == UI_LINK_UP ? "LINK" : "LINK \xE2\x80\xA6", m->link, 0);

    if (!m->has_address)
    {
        for (int i = 0; i < 4; i++)
        {
            gfx_panel(88 + i * 72, 230, 56, 30, 4, OUTLINE, GFX_OPAQUE);
        }

        return;
    }

    gfx_text(FONT_HERO, 88, 262, m->ip, TEXT_PRIMARY, GFX_LEFT);
    float x = 88;
    x += gfx_text(FONT_LABEL, x, 286, "MASK", TEXT_LABEL, GFX_LEFT) + 10;
    x += gfx_text(FONT_LABEL, x, 286, m->netmask, TEXT_SECONDARY, GFX_LEFT) + 24;
    x += gfx_text(FONT_LABEL, x, 286, "GATEWAY", TEXT_LABEL, GFX_LEFT) + 10;
    gfx_text(FONT_LABEL, x, 286, m->gateway, TEXT_SECONDARY, GFX_LEFT);
}

static void format_disk(const struct model *m, char *text, size_t size)
{
    unsigned total = m->disk_gb, free = m->disk_free_gb;
    if (total >= 1000 && m->disk_free_known)
    {
        snprintf(text, size, "%u.%u of %u.%u TB", free / 1000, free % 1000 / 100, total / 1000, total % 1000 / 100);
    }
    else if (total >= 1000)
    {
        snprintf(text, size, "%u.%u TB", total / 1000, total % 1000 / 100);
    }
    else if (m->disk_free_known)
    {
        snprintf(text, size, "%u of %u GB", free, total);
    }
    else
    {
        snprintf(text, size, "%u GB", total);
    }
}

static void draw_cells(const struct model *m)
{
    gfx_panel(64, 308, 204, 52, 6, SURFACE_1, GFX_OPAQUE);
    unsigned disk = m->disk_known && m->disk_gb == 0 ? STATE_WARN : TEXT_PRIMARY;
    gfx_outlined_panel(80, 322, 20, 24, 3, 3, SURFACE_1, disk);
    gfx_rect(80, 329, 20, 3, disk, GFX_OPAQUE);
    if (m->disk_gb > 0)
    {
        gfx_disc(100, 344, 5, STATE_OK, GFX_OPAQUE);
    }

    gfx_text(FONT_LABEL, 112, 328, m->disk_gb > 0 && m->disk_free_known ? "HARD DISK FREE" : "HARD DISK", TEXT_LABEL, GFX_LEFT);
    char size[32];
    format_disk(m, size, sizeof(size));
    gfx_text(FONT_BODY, 112, 352, !m->disk_known ? "\xE2\x80\x93" : m->disk_gb == 0 ? "Not found" : size,
             !m->disk_known ? TEXT_DISABLED : disk, GFX_LEFT);

    gfx_panel(280, 308, 296, 52, 6, SURFACE_1, GFX_OPAQUE);
    gfx_text(FONT_LABEL, 296, 328, "LAST SPEED TEST", TEXT_LABEL, GFX_LEFT);
    char cut[WIDGET_TEXT_LENGTH * 2];
    gfx_text(FONT_BODY, 296, 352, m->speed[0] != '\0' ? widget_fit(FONT_BODY, m->speed, 264, cut, sizeof(cut)) : "Not run yet",
             m->speed[0] != '\0' ? TEXT_PRIMARY : TEXT_DISABLED, GFX_LEFT);
}

static void draw_toast(void)
{
    gfx_panel(64, 316, 512, 44, 4, SURFACE_FOCUS, GFX_OPAQUE);
    widget_lock(80, 326, TEXT_SECONDARY);
    gfx_text(FONT_BODY, 112, 346, toast, TEXT_PRIMARY, GFX_LEFT);
}

static void draw_exit_dialog(const struct model *m)
{
    widget_dialog(112, 136, 416, 176);
    if (m->connected)
    {
        widget_icon(KIND_WARN, 136, 160, 0);
    }
    else
    {
        gfx_rect(140, 164, 3, 20, TEXT_PRIMARY, GFX_OPAQUE);
        gfx_rect(140, 164, 9, 3, TEXT_PRIMARY, GFX_OPAQUE);
        gfx_rect(140, 181, 9, 3, TEXT_PRIMARY, GFX_OPAQUE);
        gfx_line(148, 174, 162, 174, 3, TEXT_PRIMARY, GFX_OPAQUE);
        gfx_line(156, 168, 162, 174, 3, TEXT_PRIMARY, GFX_OPAQUE);
        gfx_line(156, 180, 162, 174, 3, TEXT_PRIMARY, GFX_OPAQUE);
    }

    gfx_text(FONT_HEADLINE, 176, 184, "Leave Loadout Bay?", TEXT_PRIMARY, GFX_LEFT);
    gfx_text(FONT_BODY, 176, 218, m->connected ? "Loadout is connected." : "The PC can't reach this PS2", TEXT_SECONDARY,
             GFX_LEFT);
    gfx_text(FONT_BODY, 176, 242, m->connected ? "Leaving ends the connection." : "until you start it again.", TEXT_SECONDARY,
             GFX_LEFT);
    widget_button(176, 256, 152, "Leave", focus == 0);
    widget_button(340, 256, 152, "Stay", focus == 1);
}

static void draw_leaving(void)
{
    gfx_begin();
    widget_background();
    widget_mark(272, 152, 3);
    gfx_text(FONT_BODY, 320, 300, "Returning to the PS2 menu\xE2\x80\xA6", TEXT_SECONDARY, GFX_CENTER);
    gfx_end();
}

static void draw_main(const struct model *m, unsigned t)
{
    widget_frame("v" LOADOUT_VERSION);
    draw_status(m, t);
    draw_address(m);
    draw_cells(m);
    if (toast[0] != '\0')
    {
        draw_toast();
    }

    if (screen == SCREEN_EXIT)
    {
        draw_exit_dialog(m);
        static const struct hint hints[] = {{GLYPH_LEFT_RIGHT, "Choose"}, {GLYPH_CROSS, "OK"}, {GLYPH_CIRCLE, "Stay"}};
        widget_hints(hints, 3, 0);
        return;
    }

    int locked = busy(m->status);
    if (locked)
    {
        widget_lock(64, 380, TEXT_LABEL);
        gfx_text(FONT_LABEL, 96, 398, "BUSY: CONTROLS WAIT", TEXT_LABEL, GFX_LEFT);
    }

    static const struct hint hints[] = {{GLYPH_TRIANGLE, "Network"}, {GLYPH_START, "Exit"}};
    widget_hints(hints, 2, locked);
}

static void show_toast(const char *text, unsigned t)
{
    snprintf(toast, sizeof(toast), "%s", text);
    toast_until = t + TOAST_MS;
    dirty = 1;
}

static void open_screen(enum screen which, int initial_focus, unsigned t)
{
    screen = which;
    focus = initial_focus;
    screen_opened = t;
    dirty = 1;
}

// One newly pressed button, with the D-pad repeating while it is held.
static unsigned next_press(unsigned t)
{
    static unsigned held, repeat_at;
    unsigned buttons = pad_buttons();
    unsigned pressed = buttons & ~held;
    unsigned dpad = buttons & (PAD_UP | PAD_DOWN | PAD_LEFT | PAD_RIGHT);
    if (pressed & dpad)
    {
        repeat_at = t + REPEAT_DELAY_MS;
    }
    else if (dpad != 0 && (dpad & held) == dpad && (int)(t - repeat_at) >= 0)
    {
        pressed |= dpad;
        repeat_at = t + REPEAT_MS;
    }

    held = buttons;
    return pressed;
}

static int toast_if_busy(const struct model *m, unsigned t)
{
    switch (m->status)
    {
    case STATUS_INSTALLING:
    case STATUS_CHECKING:
    case STATUS_FINISHING:
        show_toast("A game is installing. Try again when it's done.", t);
        return 1;
    case STATUS_BENCH:
        show_toast("A speed test is running. Try again when it's done.", t);
        return 1;
    case STATUS_UPDATING:
        show_toast("Loadout Bay is updating.", t);
        return 1;
    default:
        return 0;
    }
}

static void on_main(unsigned pressed, const struct model *m, unsigned t)
{
    if (!(pressed & (PAD_START | PAD_TRIANGLE)) || toast_if_busy(m, t))
    {
        return;
    }

    if (pressed & PAD_TRIANGLE)
    {
        netscreen_open(m, t);
        open_screen(SCREEN_NETWORK, 0, t);
        return;
    }

    // Leaving is the quick answer, unless Loadout is connected.
    open_screen(SCREEN_EXIT, m->connected ? 1 : 0, t);
}

static void on_exit_dialog(unsigned pressed, unsigned t)
{
    if (pressed & (PAD_LEFT | PAD_RIGHT))
    {
        focus ^= 1;
    }
    else if (pressed & PAD_CIRCLE || (pressed & PAD_CROSS && focus == 1))
    {
        open_screen(SCREEN_MAIN, 0, t);
    }
    else if (pressed & PAD_CROSS)
    {
        leaving = 1;
    }
}

static void run(void *argument)
{
    (void)argument;
    unsigned drawn = 0;
    unsigned busy_frames = 0;
    for (;;)
    {
        unsigned t = now_ms();
        struct model m;
        lock_model();
        m = model;
        SignalSema(lock);

        unsigned pressed = next_press(t);
        // Work that locks the controls closes Exit; the network screen stays, but cannot save then.
        if (busy(m.status) && screen == SCREEN_EXIT)
        {
            open_screen(SCREEN_MAIN, 0, t);
        }

        if (pressed != 0 && (int)(t - screen_opened) >= SETTLE_MS)
        {
            dirty = 1;
            switch (screen)
            {
            case SCREEN_EXIT:
                on_exit_dialog(pressed, t);
                break;
            case SCREEN_NETWORK:
                if (netscreen_input(pressed, &m, t))
                {
                    open_screen(SCREEN_MAIN, 0, t);
                }
                break;
            default:
                on_main(pressed, &m, t);
                break;
            }
        }

        if (leaving)
        {
            draw_leaving();
            if (exit_handler != NULL)
            {
                exit_handler();
            }

            SleepThread();
        }

        if (toast[0] != '\0' && (int)(t - toast_until) >= 0)
        {
            toast[0] = '\0';
            dirty = 1;
        }

        // The network screen's notices time out, so it redraws twice a second as well.
        enum kind kind = shown_kind(&m);
        unsigned interval = FRAME_MS;
        int animating = kind == KIND_READY || kind == KIND_BUSY;
        if (screen == SCREEN_NETWORK)
        {
            animating = 1;
            interval = netscreen_animating(&m) ? FRAME_MS : 500;
        }

        if (busy(m.status))
        {
            animating = 1;
            interval = BUSY_FRAME_MS;
        }

        int forced = dirty && (!busy(m.status) || t - drawn >= BUSY_FORCED_MS);
        if (forced || (animating && t - drawn >= interval))
        {
            dirty = 0;
            drawn = t;
            gfx_begin();
            if (screen == SCREEN_NETWORK)
            {
                netscreen_draw(&m, t);
            }
            else
            {
                // During a transfer the spinner moves one dot per redraw; at its own pace it would skip dots.
                draw_main(&m, busy(m.status) ? busy_frames++ * SPINNER_STEP_MS : t);
            }

            gfx_end();
        }
        else
        {
            DelayThread(IDLE_MS * 1000);
        }
    }
}

void ui_init(void)
{
    gfx_init();
    model.status = STATUS_STARTING;
    model.permille = STATUS_NO_PROGRESS;
    model.use_dhcp = 1;

    ee_sema_t sema = {.init_count = 1, .max_count = 1};
    lock = CreateSema(&sema);

    // The same priority as the main thread, which waits on the network and the
    // disk often enough for the controller to answer even during a transfer.
    static u8 stack[32 * 1024] __attribute__((aligned(16)));
    extern void *_gp;
    ee_thread_status_t self;
    ReferThreadStatus(GetThreadId(), &self);
    ee_thread_t thread = {
        .func = run,
        .stack = stack,
        .stack_size = sizeof(stack),
        .gp_reg = &_gp,
        .initial_priority = self.current_priority,
    };
    StartThread(CreateThread(&thread), NULL);
}

void ui_set_exit_handler(void (*handler)(void))
{
    exit_handler = handler;
}

void ui_status(enum status status, const char *detail, int permille)
{
    lock_model();
    // Progress within a transfer waits for the next busy frame; an install reports every MB.
    int progress_only = status == model.status && busy(status);
    model.status = status;
    snprintf(model.detail, sizeof(model.detail), "%s", detail != NULL ? detail : "");
    model.permille = permille;
    if (status == STATUS_CONNECTED)
    {
        model.connected = 1;
    }
    else if (status == STATUS_READY)
    {
        model.connected = 0;
    }

    if (progress_only)
    {
        SignalSema(lock);
        return;
    }

    unlock_model();
}

void ui_set_link(enum ui_link link)
{
    lock_model();
    model.link = link;
    unlock_model();
}

void ui_set_network(int use_dhcp, enum ui_source source)
{
    lock_model();
    model.use_dhcp = use_dhcp;
    model.source = source;
    unlock_model();
}

void ui_set_address(const char *ip, const char *netmask, const char *gateway)
{
    lock_model();
    model.has_address = ip != NULL;
    snprintf(model.ip, sizeof(model.ip), "%s", ip != NULL ? ip : "");
    snprintf(model.netmask, sizeof(model.netmask), "%s", netmask != NULL ? netmask : "");
    snprintf(model.gateway, sizeof(model.gateway), "%s", gateway != NULL ? gateway : "");
    unlock_model();
}

void ui_set_disk(unsigned size_gb)
{
    lock_model();
    model.disk_gb = size_gb;
    model.disk_known = 1;
    unlock_model();
}

void ui_set_disk_free(unsigned free_gb)
{
    lock_model();
    model.disk_free_gb = free_gb;
    model.disk_free_known = 1;
    unlock_model();
}

void ui_set_speed(const char *result)
{
    lock_model();
    snprintf(model.speed, sizeof(model.speed), "%s", result);
    unlock_model();
}

void ui_set_note(const char *note)
{
    lock_model();
    snprintf(model.note, sizeof(model.note), "%s", note);
    unlock_model();
}

void ui_set_settings_place(const char *place)
{
    lock_model();
    model.can_save = place != NULL;
    snprintf(model.place, sizeof(model.place), "%s", place != NULL ? place : "");
    unlock_model();
}

void ui_request_save(const struct ui_settings *settings)
{
    lock_model();
    requested = *settings;
    request_pending = 1;
    model.save = SAVE_PENDING;
    unlock_model();
}

int ui_take_settings(struct ui_settings *settings)
{
    lock_model();
    int pending = request_pending;
    if (pending)
    {
        *settings = requested;
        request_pending = 0;
    }

    SignalSema(lock);
    return pending;
}

void ui_settings_saved(int ok, const char *message)
{
    lock_model();
    model.save = ok ? SAVE_DONE : SAVE_FAILED;
    snprintf(model.save_message, sizeof(model.save_message), "%s", message != NULL ? message : "");
    unlock_model();
}
