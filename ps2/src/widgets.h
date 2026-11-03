// The pieces Loadout Bay's screens are drawn from, in the palette and shapes
// of Loadout's design: the frame, status icons, controller buttons, hints,
// dialogs, and buttons. All coordinates are on the 640x448 canvas of gfx.h.

#ifndef LOADOUT_WIDGETS_H
#define LOADOUT_WIDGETS_H

#include "gfx.h"

#define BG_TOP 0x16233A
#define BG_BOTTOM 0x0B111B
#define BG_BAND 0x0A0F18
#define SURFACE_1 0x172338
#define SURFACE_2 0x1B2A42
#define SURFACE_CHIP 0x22324D
#define SURFACE_FOCUS 0x26405F
#define OUTLINE 0x2F4566
#define TEXT_PRIMARY 0xECF0F6
#define TEXT_SECONDARY 0xB4C0D0
#define TEXT_LABEL 0x8D9DB4
#define TEXT_DISABLED 0x5E6E86
#define AMBER 0xF2963A
#define INK 0x0F1724
#define STATE_OK 0x4FCB8D
#define STATE_BUSY 0x74B2FF
#define STATE_WARN 0xF5CF5C
#define STATE_ERROR 0xFF8273
#define STATE_ERROR_FILL 0x4A1F27

#define WIDGET_TEXT_LENGTH 72

enum kind
{
    KIND_READY,
    KIND_DONE,
    KIND_BUSY,
    KIND_CONNECTED,
    KIND_WARN,
    KIND_ERROR,
};

enum glyph
{
    GLYPH_CROSS,
    GLYPH_CIRCLE,
    GLYPH_TRIANGLE,
    GLYPH_SQUARE,
    GLYPH_START,
    GLYPH_LEFT_RIGHT,
    GLYPH_UP_DOWN,
    GLYPH_L1_R1,
};

struct hint
{
    enum glyph glyph;
    const char *text;
};

unsigned widget_kind_color(enum kind kind);

// Text that fits max_width: as it is, or cut with an ellipsis at the end (or at the start).
const char *widget_fit(enum font_style_id style, const char *text, int max_width, char *out, int size);
const char *widget_fit_start(enum font_style_id style, const char *text, int max_width, char *out, int size);

void widget_mark(float x, float y, float scale);
void widget_background(void);

// The background, the header with the mark and the right-hand label, and the hint band.
void widget_frame(const char *right);

// The spinner's time per dot.
#define SPINNER_STEP_MS 100
// A 28-pixel status icon; t (in milliseconds) animates the ready beacon and the spinner.
void widget_icon(enum kind kind, float x, float y, unsigned t);

void widget_lock(float x, float y, unsigned color);

// Button hints, right-aligned in the hint band; dim greys them out while the controls wait.
void widget_hints(const struct hint *hints, int count, int dim);

// The dimmed screen behind a dialog, and the dialog's panel.
void widget_dialog(float x, float y, float w, float h);

void widget_button(float x, float y, float w, const char *text, int focused);

#endif
