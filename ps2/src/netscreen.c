// The network settings screen: DHCP or a static address,
// edited with the controller and saved to loadout-bay.cfg.

#include <libpad.h>
#include <stdio.h>
#include <string.h>

#include "bench.h"
#include "control.h"
#include "install.h"
#include "ui_internal.h"

#define SETTLE_MS 250
#define NOTICE_MS 4000

enum row
{
    ROW_MODE,
    ROW_IP,
    ROW_NETMASK,
    ROW_GATEWAY,
    ROW_SAVE,
    ROW_COUNT,
};

enum view
{
    VIEW_LIST,
    VIEW_OCTETS,
    VIEW_DISCARD,
    VIEW_SAVING,
};

enum strip
{
    STRIP_PORTS,
    STRIP_NOTICE,
    STRIP_ERROR,
};

struct draft
{
    int use_dhcp;
    unsigned char ip[4];
    int prefix;
    unsigned char gateway[4];
};

static struct draft draft, original;
static enum view view;
static int focus;
static int discard_focus;
static int error_row = -1;
static enum strip strip;
static char strip_text[WIDGET_TEXT_LENGTH];
static unsigned strip_until;
static unsigned opened;

// The octet editor: twelve digits, three for each part of the address.
static int editing_row;
static char digits[12];
static int caret;

static void parse_dotted(const char *text, unsigned char out[4])
{
    unsigned a = 0, b = 0, c = 0, d = 0;
    if (sscanf(text, "%u.%u.%u.%u", &a, &b, &c, &d) != 4)
    {
        a = b = c = d = 0;
    }

    out[0] = (unsigned char)a;
    out[1] = (unsigned char)b;
    out[2] = (unsigned char)c;
    out[3] = (unsigned char)d;
}

static unsigned to_u32(const unsigned char b[4])
{
    return (unsigned)b[0] << 24 | (unsigned)b[1] << 16 | (unsigned)b[2] << 8 | b[3];
}

static unsigned prefix_mask(int prefix)
{
    return prefix <= 0 ? 0 : 0xFFFFFFFFu << (32 - prefix);
}

// The prefix of a netmask whose ones come first, or 24 for any other.
static int mask_prefix(const unsigned char mask[4])
{
    unsigned m = to_u32(mask);
    int prefix = 0;
    while (prefix < 32 && (m & (0x80000000u >> prefix)))
    {
        prefix++;
    }

    return prefix_mask(prefix) == m && prefix >= 8 && prefix <= 30 ? prefix : 24;
}

static void format_dotted(char *text, int size, const unsigned char b[4])
{
    snprintf(text, size, "%u.%u.%u.%u", b[0], b[1], b[2], b[3]);
}

static int dirty(void)
{
    if (draft.use_dhcp != original.use_dhcp)
    {
        return 1;
    }

    return !draft.use_dhcp && (memcmp(draft.ip, original.ip, 4) != 0 || draft.prefix != original.prefix
                               || memcmp(draft.gateway, original.gateway, 4) != 0);
}

static int row_enabled(enum row row, const struct model *m)
{
    switch (row)
    {
    case ROW_MODE:
        return 1;
    case ROW_SAVE:
        return dirty() && m->can_save;
    default:
        return !draft.use_dhcp;
    }
}

static void set_strip(enum strip kind, const char *text, unsigned until)
{
    strip = kind;
    snprintf(strip_text, sizeof(strip_text), "%s", text);
    strip_until = until;
}

// Checks a static address; returns the message for the first problem, and its row.
static const char *validate(int *row)
{
    unsigned ip = to_u32(draft.ip), mask = prefix_mask(draft.prefix), gateway = to_u32(draft.gateway);
    static char message[WIDGET_TEXT_LENGTH];
    *row = ROW_IP;
    if (ip == 0 || ip == 0xFFFFFFFFu || draft.ip[0] == 127 || draft.ip[0] >= 224)
    {
        return "That IP address can't be used.";
    }

    if ((ip & ~mask) == 0 || (ip & ~mask) == ~mask)
    {
        return draft.prefix == 24 ? ".0 and .255 are reserved here." : "That address is reserved in this network.";
    }

    *row = ROW_GATEWAY;
    if ((gateway & mask) != (ip & mask))
    {
        if (draft.prefix == 24)
        {
            snprintf(message, sizeof(message), "Gateway must be in %u.%u.%u.x.", draft.ip[0], draft.ip[1], draft.ip[2]);
            return message;
        }

        return "Gateway must be in the same network.";
    }

    if (gateway == ip)
    {
        return "Gateway can't be this console's address.";
    }

    *row = -1;
    return NULL;
}

void netscreen_open(const struct model *m, unsigned t)
{
    memset(&draft, 0, sizeof(draft));
    draft.use_dhcp = m->use_dhcp;
    unsigned char mask[4];
    parse_dotted(m->ip, draft.ip);
    parse_dotted(m->netmask, mask);
    parse_dotted(m->gateway, draft.gateway);
    draft.prefix = mask_prefix(mask);
    original = draft;
    view = VIEW_LIST;
    focus = ROW_MODE;
    error_row = -1;
    opened = t;

    // Where the settings came from, for a moment; a problem stays until it is saved over.
    if (!m->can_save)
    {
        set_strip(STRIP_NOTICE, "Can't save where Loadout Bay was started.", 0);
    }
    else if (m->source == UI_SOURCE_IGNORED)
    {
        set_strip(STRIP_ERROR, m->note, 0);
    }
    else
    {
        set_strip(STRIP_NOTICE, m->source == UI_SOURCE_FILE ? "Using loadout-bay.cfg." : "No loadout-bay.cfg: using DHCP.",
                  t + NOTICE_MS);
    }
}

int netscreen_animating(const struct model *m)
{
    return view == VIEW_SAVING && m->save == SAVE_PENDING;
}

static void move_focus(int step, const struct model *m)
{
    for (int i = 0; i < ROW_COUNT; i++)
    {
        focus = (focus + step + ROW_COUNT) % ROW_COUNT;
        if (row_enabled((enum row)focus, m))
        {
            return;
        }
    }
}

static void open_octets(int row)
{
    editing_row = row;
    const unsigned char *value = row == ROW_IP ? draft.ip : draft.gateway;
    for (int i = 0; i < 4; i++)
    {
        digits[i * 3] = (char)(value[i] / 100);
        digits[i * 3 + 1] = (char)(value[i] / 10 % 10);
        digits[i * 3 + 2] = (char)(value[i] % 10);
    }

    caret = 11;
    view = VIEW_OCTETS;
}

static void save(const struct model *m, unsigned t)
{
    if (!dirty() || !m->can_save)
    {
        return;
    }

    if (m->connected)
    {
        set_strip(STRIP_NOTICE, "Loadout is connected. Close it on the PC first.", t + NOTICE_MS);
        return;
    }

    if (!draft.use_dhcp)
    {
        int row;
        const char *problem = validate(&row);
        if (problem != NULL)
        {
            error_row = row;
            focus = row;
            set_strip(STRIP_ERROR, problem, 0);
            return;
        }
    }

    struct ui_settings settings;
    memset(&settings, 0, sizeof(settings));
    settings.use_dhcp = draft.use_dhcp;
    memcpy(settings.ip, draft.ip, 4);
    memcpy(settings.gateway, draft.gateway, 4);
    unsigned mask = prefix_mask(draft.prefix);
    for (int i = 0; i < 4; i++)
    {
        settings.netmask[i] = (unsigned char)(mask >> (24 - 8 * i));
    }

    error_row = -1;
    view = VIEW_SAVING;
    ui_request_save(&settings);
}

static int on_list(unsigned pressed, const struct model *m, unsigned t)
{
    if (pressed & PAD_UP)
    {
        move_focus(-1, m);
    }
    else if (pressed & PAD_DOWN)
    {
        move_focus(1, m);
    }
    else if (pressed & PAD_START)
    {
        save(m, t);
    }
    else if (pressed & PAD_CIRCLE)
    {
        if (!dirty())
        {
            return 1;
        }

        view = VIEW_DISCARD;
        discard_focus = 1;
        opened = t;
    }
    else if (focus == ROW_MODE && pressed & (PAD_LEFT | PAD_RIGHT | PAD_CROSS))
    {
        draft.use_dhcp = !draft.use_dhcp;
        error_row = -1;
        if (strip == STRIP_ERROR)
        {
            set_strip(STRIP_PORTS, "", 0);
        }
    }
    else if (focus == ROW_NETMASK && pressed & (PAD_LEFT | PAD_RIGHT))
    {
        draft.prefix += pressed & PAD_LEFT ? -1 : 1;
        draft.prefix = draft.prefix < 8 ? 8 : draft.prefix > 30 ? 30 : draft.prefix;
    }
    else if ((focus == ROW_IP || focus == ROW_GATEWAY) && pressed & PAD_CROSS)
    {
        open_octets(focus);
        opened = t;
    }
    else if (focus == ROW_SAVE && pressed & PAD_CROSS)
    {
        save(m, t);
    }

    // A row that just became unavailable gives up the focus.
    if (!row_enabled((enum row)focus, m))
    {
        focus = ROW_MODE;
    }

    return 0;
}

// Steps the digit at the caret up or down through the values that keep its part
// at 255 or below: the hundreds go 0-2, and after a 2 the tens go 0-5. A step
// that would pass 255 with the lower digits as they are lowers them to fit.
static void step_digit(int up)
{
    char *part = &digits[caret / 3 * 3];
    int position = caret % 3;
    static const int weight[3] = {100, 10, 1};
    for (int tries = 0; tries < 10; tries++)
    {
        part[position] = (char)((part[position] + (up ? 1 : 9)) % 10);
        int above = 0;
        for (int i = 0; i <= position; i++)
        {
            above += part[i] * weight[i];
        }

        if (above <= 255)
        {
            break;
        }
    }

    if (part[0] * 100 + part[1] * 10 + part[2] > 255)
    {
        part[0] = 2;
        part[1] = 5;
        part[2] = 5;
    }
}

static void on_octets(unsigned pressed)
{
    if (pressed & (PAD_UP | PAD_DOWN))
    {
        step_digit((pressed & PAD_UP) != 0);
    }
    else if (pressed & PAD_LEFT && caret > 0)
    {
        caret--;
    }
    else if (pressed & PAD_RIGHT && caret < 11)
    {
        caret++;
    }
    else if (pressed & PAD_L1)
    {
        caret = caret / 3 > 0 ? (caret / 3 - 1) * 3 : 0;
    }
    else if (pressed & PAD_R1)
    {
        caret = caret / 3 < 3 ? (caret / 3 + 1) * 3 : 9;
    }
    else if (pressed & PAD_CIRCLE)
    {
        view = VIEW_LIST;
    }
    else if (pressed & (PAD_CROSS | PAD_START))
    {
        unsigned char value[4];
        for (int i = 0; i < 4; i++)
        {
            value[i] = (unsigned char)(digits[i * 3] * 100 + digits[i * 3 + 1] * 10 + digits[i * 3 + 2]);
        }

        memcpy(editing_row == ROW_IP ? draft.ip : draft.gateway, value, 4);
        if (editing_row == ROW_IP)
        {
            // A gateway outside the new network becomes its .1, the usual router.
            unsigned mask = prefix_mask(draft.prefix);
            if ((to_u32(draft.gateway) & mask) != (to_u32(draft.ip) & mask))
            {
                unsigned gateway = (to_u32(draft.ip) & mask) | 1;
                for (int i = 0; i < 4; i++)
                {
                    draft.gateway[i] = (unsigned char)(gateway >> (24 - 8 * i));
                }
            }
        }

        if (error_row == editing_row)
        {
            error_row = -1;
            set_strip(STRIP_PORTS, "", 0);
        }

        view = VIEW_LIST;
    }
}

int netscreen_input(unsigned pressed, const struct model *m, unsigned t)
{
    if ((int)(t - opened) < SETTLE_MS)
    {
        return 0;
    }

    switch (view)
    {
    case VIEW_LIST:
        return on_list(pressed, m, t);
    case VIEW_OCTETS:
        on_octets(pressed);
        return 0;
    case VIEW_DISCARD:
        if (pressed & (PAD_LEFT | PAD_RIGHT))
        {
            discard_focus ^= 1;
        }
        else if (pressed & PAD_CROSS && discard_focus == 0)
        {
            return 1;
        }
        else if (pressed & (PAD_CROSS | PAD_CIRCLE))
        {
            view = VIEW_LIST;
        }

        return 0;
    case VIEW_SAVING:
        if (m->save == SAVE_DONE && pressed & (PAD_CROSS | PAD_CIRCLE))
        {
            return 1;
        }

        if (m->save == SAVE_FAILED)
        {
            if (pressed & PAD_CROSS)
            {
                save(m, t);
            }
            else if (pressed & PAD_CIRCLE)
            {
                view = VIEW_LIST;
            }
        }

        return 0;
    }

    return 0;
}

static void draw_row(enum row row, const char *label, const struct model *m)
{
    float y = 96 + row * 44;
    int focused = focus == (int)row && view == VIEW_LIST;
    int enabled = row_enabled(row, m);
    gfx_panel(64, y, 512, 40, 4, focused ? SURFACE_FOCUS : SURFACE_1, GFX_OPAQUE);
    if (focused || error_row == (int)row)
    {
        gfx_rect(64, y, 4, 40, error_row == (int)row ? STATE_ERROR : AMBER, GFX_OPAQUE);
    }

    gfx_text(FONT_BODY, 88, y + 27, label, enabled ? TEXT_PRIMARY : TEXT_DISABLED, GFX_LEFT);
}

static void draw_address_row(enum row row, const char *label, const unsigned char value[4], const char *lease,
                             const struct model *m)
{
    draw_row(row, label, m);
    float y = 96 + row * 44;
    char text[24];
    if (draft.use_dhcp)
    {
        int width = gfx_text(FONT_HEADLINE, 552, y + 29, lease[0] != '\0' ? lease : "\xE2\x80\x93", TEXT_DISABLED, GFX_RIGHT);
        gfx_text(FONT_LABEL, 552 - width - 12, y + 28, "FROM DHCP", TEXT_DISABLED, GFX_RIGHT);
        return;
    }

    format_dotted(text, sizeof(text), value);
    gfx_text(FONT_HEADLINE, 552, y + 29, text, TEXT_PRIMARY, GFX_RIGHT);
    if (error_row == (int)row)
    {
        int width = gfx_text_width(FONT_HEADLINE, text);
        widget_icon(KIND_ERROR, 552 - width - 38, y + 6, 0);
    }
}

static void draw_list(const struct model *m, unsigned t)
{
    // Mode: a switch of two segments.
    draw_row(ROW_MODE, "Address mode", m);
    for (int i = 0; i < 2; i++)
    {
        int selected = draft.use_dhcp == (i == 0);
        float x = 360 + i * 96;
        gfx_panel(x, 102, 92, 28, 4, selected ? AMBER : SURFACE_CHIP, GFX_OPAQUE);
        gfx_text(FONT_BODY, x + 46, 124, i == 0 ? "DHCP" : "STATIC", selected ? INK : TEXT_SECONDARY, GFX_CENTER);
    }

    draw_address_row(ROW_IP, "IP address", draft.ip, m->ip, m);

    draw_row(ROW_NETMASK, "Netmask", m);
    if (draft.use_dhcp)
    {
        int width = gfx_text(FONT_HEADLINE, 552, 96 + 2 * 44 + 29, m->netmask[0] != '\0' ? m->netmask : "\xE2\x80\x93",
                             TEXT_DISABLED, GFX_RIGHT);
        gfx_text(FONT_LABEL, 552 - width - 12, 96 + 2 * 44 + 28, "FROM DHCP", TEXT_DISABLED, GFX_RIGHT);
    }
    else
    {
        float y = 96 + 2 * 44;
        unsigned mask = prefix_mask(draft.prefix);
        unsigned char bytes[4] = {(unsigned char)(mask >> 24), (unsigned char)(mask >> 16), (unsigned char)(mask >> 8),
                                  (unsigned char)mask};
        char text[24], prefix[8];
        format_dotted(text, sizeof(text), bytes);
        snprintf(prefix, sizeof(prefix), "/%d", draft.prefix);
        int focused = focus == ROW_NETMASK && view == VIEW_LIST;
        unsigned arrow = focused ? AMBER : OUTLINE;
        int prefix_width = gfx_text(FONT_LABEL, 536, y + 28, prefix, TEXT_LABEL, GFX_RIGHT);
        int width = gfx_text(FONT_HEADLINE, 536 - prefix_width - 8, y + 29, text, TEXT_PRIMARY, GFX_RIGHT);
        float left = 536 - prefix_width - 8 - width - 22;
        gfx_triangle(left + 10, y + 12, left + 10, y + 28, left, y + 20, arrow, GFX_OPAQUE);
        gfx_triangle(544, y + 12, 544, y + 28, 554, y + 20, arrow, GFX_OPAQUE);
    }

    draw_address_row(ROW_GATEWAY, "Gateway", draft.gateway, m->gateway, m);

    // Save, and where it saves.
    int save_enabled = row_enabled(ROW_SAVE, m);
    int save_focused = focus == ROW_SAVE && view == VIEW_LIST;
    if (save_focused)
    {
        gfx_panel(64, 280, 248, 40, 4, AMBER, GFX_OPAQUE);
    }
    else if (save_enabled)
    {
        gfx_outlined_panel(64, 280, 248, 40, 4, 2, SURFACE_CHIP, AMBER);
    }
    else
    {
        gfx_panel(64, 280, 248, 40, 4, SURFACE_CHIP, GFX_OPAQUE);
    }

    gfx_text(FONT_BODY, 188, 308, dirty() ? "Save and apply" : "No changes",
             save_focused ? INK : save_enabled ? TEXT_PRIMARY : TEXT_DISABLED, GFX_CENTER);
    gfx_text(FONT_LABEL, 328, 296, "SAVES TO", TEXT_LABEL, GFX_LEFT);
    char cut[WIDGET_TEXT_LENGTH * 2];
    gfx_text(FONT_LABEL, 328, 316,
             m->can_save ? widget_fit_start(FONT_LABEL, m->place, 248, cut, sizeof(cut)) : "Nowhere it can write", TEXT_SECONDARY,
             GFX_LEFT);

    // The strip: a problem, a notice, or the ports.
    if (strip != STRIP_PORTS && strip_until != 0 && (int)(t - strip_until) >= 0)
    {
        set_strip(STRIP_PORTS, "", 0);
    }

    if (strip == STRIP_PORTS)
    {
        gfx_panel(64, 328, 512, 32, 4, SURFACE_1, GFX_OPAQUE);
        char ports[WIDGET_TEXT_LENGTH];
        snprintf(ports, sizeof(ports), "PORTS  LOADOUT %d, %d \xC2\xB7 SPEED TEST %d, %d", CONTROL_PORT, INSTALL_DATA_PORT,
                 BENCH_TCP_PORT, BENCH_UDP_PORT);
        gfx_text(FONT_LABEL, 88, 350, ports, TEXT_LABEL, GFX_LEFT);
    }
    else
    {
        int error = strip == STRIP_ERROR;
        gfx_panel(64, 328, 512, 32, 4, error ? STATE_ERROR_FILL : SURFACE_CHIP, GFX_OPAQUE);
        widget_icon(error ? KIND_ERROR : KIND_WARN, 76, 330, 0);
        gfx_text(FONT_BODY, 112, 352, widget_fit(FONT_BODY, strip_text, 456, cut, sizeof(cut)), TEXT_PRIMARY, GFX_LEFT);
    }
}

static void draw_octets(void)
{
    widget_dialog(72, 120, 496, 200);
    gfx_text(FONT_LABEL, 96, 150, editing_row == ROW_IP ? "IP ADDRESS" : "GATEWAY", TEXT_LABEL, GFX_LEFT);
    gfx_text(FONT_LABEL, 544, 150, "0 TO 255 EACH", TEXT_LABEL, GFX_RIGHT);
    for (int i = 0; i < 4; i++)
    {
        float x = 96 + i * 116;
        if (caret / 3 == i)
        {
            gfx_outlined_panel(x, 176, 100, 72, 4, 2, SURFACE_1, AMBER);
        }
        else
        {
            gfx_panel(x, 176, 100, 72, 4, SURFACE_1, GFX_OPAQUE);
        }

        for (int j = 0; j < 3; j++)
        {
            float cx = x + 8 + j * 28;
            if (caret == i * 3 + j)
            {
                gfx_rect(cx, 184, 28, 56, SURFACE_FOCUS, GFX_OPAQUE);
                gfx_triangle(cx + 7, 168, cx + 21, 168, cx + 14, 160, AMBER, GFX_OPAQUE);
                gfx_triangle(cx + 7, 256, cx + 21, 256, cx + 14, 264, AMBER, GFX_OPAQUE);
            }

            char digit[2] = {(char)('0' + digits[i * 3 + j]), '\0'};
            gfx_text(FONT_HERO, cx + 14, 230, digit, TEXT_PRIMARY, GFX_CENTER);
        }

        if (i < 3)
        {
            gfx_rect(x + 105, 233, 6, 6, TEXT_SECONDARY, GFX_OPAQUE);
        }
    }

    gfx_text(FONT_BODY, 96, 298, "Set the digits, then press Done.", TEXT_SECONDARY, GFX_LEFT);
}

static void draw_discard(void)
{
    widget_dialog(112, 136, 416, 176);
    widget_icon(KIND_WARN, 136, 160, 0);
    gfx_text(FONT_HEADLINE, 176, 184, "Discard changes?", TEXT_PRIMARY, GFX_LEFT);
    gfx_text(FONT_BODY, 176, 218, "The settings stay as they", TEXT_SECONDARY, GFX_LEFT);
    gfx_text(FONT_BODY, 176, 242, "were before.", TEXT_SECONDARY, GFX_LEFT);
    widget_button(176, 256, 152, "Discard", discard_focus == 0);
    widget_button(340, 256, 152, "Keep editing", discard_focus == 1);
}

static void draw_saving(const struct model *m, unsigned t)
{
    widget_dialog(72, 120, 496, 200);
    char cut[WIDGET_TEXT_LENGTH * 2];
    switch (m->save)
    {
    case SAVE_DONE:
        widget_icon(KIND_DONE, 96, 136, t);
        gfx_text(FONT_HEADLINE, 136, 162, "Saved", TEXT_PRIMARY, GFX_LEFT);
        gfx_text(FONT_LABEL, 96, 190, "FILE", TEXT_LABEL, GFX_LEFT);
        gfx_text(FONT_LABEL, 140, 190, widget_fit_start(FONT_LABEL, m->place, 400, cut, sizeof(cut)), TEXT_SECONDARY, GFX_LEFT);
        gfx_text(FONT_LABEL, 96, 222, m->use_dhcp ? "ADDRESS FROM DHCP" : "NEW ADDRESS", TEXT_LABEL, GFX_LEFT);
        gfx_text(FONT_HERO, 96, 268, m->has_address ? m->ip : "\xE2\x80\x93", AMBER, GFX_LEFT);
        gfx_text(FONT_BODY, 96, 300, "Connect Loadout to this address.", TEXT_SECONDARY, GFX_LEFT);
        break;
    case SAVE_FAILED:
        widget_icon(KIND_ERROR, 96, 136, t);
        gfx_text(FONT_HEADLINE, 136, 162, "Couldn't save", TEXT_PRIMARY, GFX_LEFT);
        gfx_text(FONT_BODY, 96, 204, widget_fit(FONT_BODY, m->save_message, 448, cut, sizeof(cut)), TEXT_SECONDARY, GFX_LEFT);
        gfx_text(FONT_LABEL, 96, 240, widget_fit_start(FONT_LABEL, m->place, 448, cut, sizeof(cut)), TEXT_LABEL, GFX_LEFT);
        break;
    default:
        widget_icon(KIND_BUSY, 96, 136, t);
        gfx_text(FONT_HEADLINE, 136, 162, "Saving settings", TEXT_PRIMARY, GFX_LEFT);
        gfx_text(FONT_BODY, 96, 204, draft.use_dhcp ? "Then asking the DHCP server." : "Then setting the address.",
                 TEXT_SECONDARY, GFX_LEFT);
        break;
    }
}

void netscreen_draw(const struct model *m, unsigned t)
{
    widget_frame("NETWORK SETTINGS");
    draw_list(m, t);
    switch (view)
    {
    case VIEW_OCTETS:
    {
        draw_octets();
        static const struct hint hints[] = {
            {GLYPH_UP_DOWN, "Change"}, {GLYPH_LEFT_RIGHT, "Move"}, {GLYPH_CROSS, "Done"}, {GLYPH_CIRCLE, "Cancel"}};
        widget_hints(hints, 4, 0);
        break;
    }
    case VIEW_DISCARD:
    {
        draw_discard();
        static const struct hint hints[] = {{GLYPH_LEFT_RIGHT, "Choose"}, {GLYPH_CROSS, "OK"}, {GLYPH_CIRCLE, "Keep editing"}};
        widget_hints(hints, 3, 0);
        break;
    }
    case VIEW_SAVING:
    {
        draw_saving(m, t);
        static const struct hint done[] = {{GLYPH_CROSS, "Done"}};
        static const struct hint failed[] = {{GLYPH_CROSS, "Try again"}, {GLYPH_CIRCLE, "Back"}};
        if (m->save == SAVE_DONE)
        {
            widget_hints(done, 1, 0);
        }
        else if (m->save == SAVE_FAILED)
        {
            widget_hints(failed, 2, 0);
        }
        break;
    }
    default:
    {
        int change = focus == ROW_MODE || focus == ROW_NETMASK;
        struct hint hints[] = {{GLYPH_UP_DOWN, "Choose"},
                               {change ? GLYPH_LEFT_RIGHT : GLYPH_CROSS, change ? "Change" : "Edit"},
                               {GLYPH_START, "Save"},
                               {GLYPH_CIRCLE, "Back"}};
        widget_hints(hints, 4, 0);
        break;
    }
    }
}
