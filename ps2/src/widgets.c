#include "widgets.h"

#include <stdio.h>
#include <string.h>

#define PILL 0xC9D3E0
#define SCRIM_ALPHA 0x50
#define ELLIPSIS "\xE2\x80\xA6"

unsigned widget_kind_color(enum kind kind)
{
    switch (kind)
    {
    case KIND_READY:
    case KIND_DONE:
        return STATE_OK;
    case KIND_WARN:
        return STATE_WARN;
    case KIND_ERROR:
        return STATE_ERROR;
    default:
        return STATE_BUSY;
    }
}

const char *widget_fit(enum font_style_id style, const char *text, int max_width, char *out, int size)
{
    if (gfx_text_width(style, text) <= max_width)
    {
        return text;
    }

    int length = snprintf(out, size, "%s", text);
    while (length > 0)
    {
        out[--length] = '\0';
        char trial[WIDGET_TEXT_LENGTH * 2];
        snprintf(trial, sizeof(trial), "%s" ELLIPSIS, out);
        if (gfx_text_width(style, trial) <= max_width)
        {
            snprintf(out, size, "%s", trial);
            break;
        }
    }

    return out;
}

const char *widget_fit_start(enum font_style_id style, const char *text, int max_width, char *out, int size)
{
    if (gfx_text_width(style, text) <= max_width)
    {
        return text;
    }

    for (const char *rest = text; *rest != '\0'; rest++)
    {
        // Only cut where a character starts, not inside one.
        if ((*rest & 0xC0) == 0x80)
        {
            continue;
        }

        snprintf(out, size, ELLIPSIS "%s", rest);
        if (gfx_text_width(style, out) <= max_width)
        {
            return out;
        }
    }

    snprintf(out, size, ELLIPSIS);
    return out;
}

// The mark: an amber tile with a chevron dropping into a tray, on a 32-unit grid.
void widget_mark(float x, float y, float s)
{
    gfx_panel(x, y, 32 * s, 32 * s, 6 * s, AMBER, GFX_OPAQUE);
    gfx_line(x + 9 * s, y + 6 * s, x + 16 * s, y + 13 * s, 4 * s, INK, GFX_OPAQUE);
    gfx_line(x + 23 * s, y + 6 * s, x + 16 * s, y + 13 * s, 4 * s, INK, GFX_OPAQUE);
    gfx_rect(x + 5 * s, y + 16 * s, 4 * s, 11 * s, INK, GFX_OPAQUE);
    gfx_rect(x + 5 * s, y + 23 * s, 22 * s, 4 * s, INK, GFX_OPAQUE);
    gfx_rect(x + 23 * s, y + 16 * s, 4 * s, 11 * s, INK, GFX_OPAQUE);
}

void widget_background(void)
{
    gfx_gradient(0, gfx_frame_top(), GFX_WIDTH, gfx_frame_height(), BG_TOP, BG_BOTTOM);
}

void widget_frame(const char *right)
{
    int top = gfx_frame_top();
    int bottom = top + gfx_frame_height();
    widget_background();
    gfx_rect(0, top, GFX_WIDTH, 84 - top, BG_BAND, GFX_OPAQUE);
    gfx_gradient_across(0, 84, 400, 4, AMBER, GFX_OPAQUE, 0);
    widget_mark(64, 46, 1);
    int width = gfx_text(FONT_WORDMARK, 108, 72, "LOADOUT", TEXT_PRIMARY, GFX_LEFT);
    width += 8 + gfx_text(FONT_WORDMARK, 108 + width + 8, 72, "BAY", AMBER, GFX_LEFT);
    gfx_text(FONT_LABEL, 108 + width + 10, 72, "FOR PS2", TEXT_LABEL, GFX_LEFT);
    gfx_text(FONT_LABEL, 576, 72, right, TEXT_LABEL, GFX_RIGHT);
    gfx_rect(0, 372, GFX_WIDTH, bottom - 372, BG_BAND, GFX_OPAQUE);
}

void widget_icon(enum kind kind, float x, float y, unsigned t)
{
    float cx = x + 14, cy = y + 14;
    unsigned c = widget_kind_color(kind);
    switch (kind)
    {
    case KIND_READY:
    {
        // A halo grows from the dot and fades, every 1.6 seconds.
        float phase = (float)(t % 1600) / 1600;
        gfx_ring(cx, cy, 6 + 8 * phase, 2, c, (int)(0x60 * (1 - phase)));
        gfx_disc(cx, cy, 6, c, GFX_OPAQUE);
        break;
    }
    case KIND_DONE:
        gfx_disc(cx, cy, 13, c, GFX_OPAQUE);
        gfx_line(x + 7, y + 14, x + 12, y + 19, 3.5f, INK, GFX_OPAQUE);
        gfx_line(x + 11, y + 19, x + 21, y + 9, 3.5f, INK, GFX_OPAQUE);
        break;
    case KIND_BUSY:
    {
        static const float dx[8] = {10, 7.07f, 0, -7.07f, -10, -7.07f, 0, 7.07f};
        static const float dy[8] = {0, 7.07f, 10, 7.07f, 0, -7.07f, -10, -7.07f};
        unsigned bright = t / SPINNER_STEP_MS % 8;
        for (unsigned i = 0; i < 8; i++)
        {
            gfx_rect(cx + dx[i] - 2, cy + dy[i] - 2, 4, 4, c, i == bright ? GFX_OPAQUE : 0x2C);
        }
        break;
    }
    case KIND_CONNECTED:
        gfx_line(x + 11, y + 6, x + 3, y + 14, 3, c, GFX_OPAQUE);
        gfx_line(x + 3, y + 14, x + 11, y + 22, 3, c, GFX_OPAQUE);
        gfx_line(x + 17, y + 6, x + 25, y + 14, 3, c, GFX_OPAQUE);
        gfx_line(x + 25, y + 14, x + 17, y + 22, 3, c, GFX_OPAQUE);
        break;
    case KIND_WARN:
        gfx_triangle(cx, y + 2, x + 27, y + 26, x + 1, y + 26, c, GFX_OPAQUE);
        gfx_rect(cx - 1.5f, y + 10, 3, 8, INK, GFX_OPAQUE);
        gfx_rect(cx - 1.5f, y + 20, 3, 3, INK, GFX_OPAQUE);
        break;
    case KIND_ERROR:
        gfx_triangle(cx, y, x + 28, cy, x, cy, c, GFX_OPAQUE);
        gfx_triangle(x, cy, x + 28, cy, cx, y + 28, c, GFX_OPAQUE);
        gfx_rect(cx - 1.5f, y + 7, 3, 9, INK, GFX_OPAQUE);
        gfx_rect(cx - 1.5f, y + 18, 3, 3, INK, GFX_OPAQUE);
        break;
    }
}

void widget_lock(float x, float y, unsigned color)
{
    gfx_rect(x + 4, y + 11, 16, 11, color, GFX_OPAQUE);
    gfx_ring(x + 12, y + 10, 6, 3, color, GFX_OPAQUE);
    gfx_rect(x + 4, y + 10, 16, 2, color, GFX_OPAQUE);
}

static int glyph_width(enum glyph glyph)
{
    return glyph == GLYPH_START ? 52 : glyph == GLYPH_L1_R1 ? 60 : 24;
}

// A controller button, 24 pixels high, with its top left at x, y.
static void draw_glyph(enum glyph glyph, float x, float y, int dim)
{
    float cx = x + 12, cy = y + 12;
    switch (glyph)
    {
    case GLYPH_START:
    case GLYPH_L1_R1:
    {
        float w = (float)glyph_width(glyph);
        gfx_panel(x, y, w, 24, 4, dim ? TEXT_DISABLED : PILL, GFX_OPAQUE);
        gfx_text(FONT_PILL, x + w / 2, y + 18, glyph == GLYPH_START ? "START" : "L1 R1", INK, GFX_CENTER);
        return;
    }
    case GLYPH_LEFT_RIGHT:
    case GLYPH_UP_DOWN:
    {
        unsigned on = dim ? TEXT_DISABLED : TEXT_PRIMARY;
        int across = glyph == GLYPH_LEFT_RIGHT;
        gfx_rect(cx - 3, y + 2, 6, 20, across ? OUTLINE : on, GFX_OPAQUE);
        gfx_rect(x + 2, cy - 3, 20, 6, across ? on : OUTLINE, GFX_OPAQUE);
        return;
    }
    default:
        break;
    }

    static const unsigned tints[] = {0x8FB4F0, 0xF08F96, 0x6FD0AE, 0xE0A2D2};
    unsigned tint = dim ? TEXT_DISABLED : tints[glyph];
    gfx_disc(cx, cy, 12, SURFACE_CHIP, GFX_OPAQUE);
    switch (glyph)
    {
    case GLYPH_CROSS:
        gfx_line(cx - 5, cy - 5, cx + 5, cy + 5, 3, tint, GFX_OPAQUE);
        gfx_line(cx + 5, cy - 5, cx - 5, cy + 5, 3, tint, GFX_OPAQUE);
        break;
    case GLYPH_CIRCLE:
        gfx_ring(cx, cy, 6.5f, 3, tint, GFX_OPAQUE);
        break;
    case GLYPH_TRIANGLE:
        gfx_line(cx, cy - 6, cx + 6, cy + 5, 3, tint, GFX_OPAQUE);
        gfx_line(cx + 6, cy + 5, cx - 6, cy + 5, 3, tint, GFX_OPAQUE);
        gfx_line(cx - 6, cy + 5, cx, cy - 6, 3, tint, GFX_OPAQUE);
        break;
    default:
        gfx_rect(cx - 6, cy - 6, 12, 3, tint, GFX_OPAQUE);
        gfx_rect(cx - 6, cy + 3, 12, 3, tint, GFX_OPAQUE);
        gfx_rect(cx - 6, cy - 6, 3, 12, tint, GFX_OPAQUE);
        gfx_rect(cx + 3, cy - 6, 3, 12, tint, GFX_OPAQUE);
        break;
    }
}

void widget_hints(const struct hint *hints, int count, int dim)
{
    int width = 0;
    for (int i = 0; i < count; i++)
    {
        width += glyph_width(hints[i].glyph) + 8 + gfx_text_width(FONT_BODY, hints[i].text) + (i > 0 ? 24 : 0);
    }

    float x = 576 - width;
    for (int i = 0; i < count; i++)
    {
        draw_glyph(hints[i].glyph, x, 380, dim);
        x += glyph_width(hints[i].glyph) + 8;
        x += gfx_text(FONT_BODY, x, 398, hints[i].text, dim ? TEXT_DISABLED : TEXT_SECONDARY, GFX_LEFT) + 24;
    }
}

void widget_dialog(float x, float y, float w, float h)
{
    gfx_rect(0, gfx_frame_top(), GFX_WIDTH, gfx_frame_height(), 0x000000, SCRIM_ALPHA);
    gfx_outlined_panel(x, y, w, h, 8, 2, SURFACE_2, OUTLINE);
}

void widget_button(float x, float y, float w, const char *text, int focused)
{
    gfx_panel(x, y, w, 40, 4, focused ? AMBER : SURFACE_CHIP, GFX_OPAQUE);
    gfx_text(FONT_BODY, x + w / 2, y + 28, text, focused ? INK : TEXT_PRIMARY, GFX_CENTER);
}
