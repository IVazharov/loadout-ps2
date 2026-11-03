// Drawing for Loadout Bay's screen, on gsKit.
//
// Everything is placed on the 640x448 canvas. Its
// content area (x 64-576, y 44-404) is spread over the frame, 32 pixels from
// the sides and a tenth of the height from the top and bottom, so there is more
// room; text keeps its size, and widths come back in canvas units. The frame is
// interlaced and fills the picture: 480 lines on NTSC, 576 on PAL, all in one
// buffer. Colours are 0xRRGGBB; alpha is the GS's 0-128, where 128 is opaque.

#ifndef LOADOUT_GFX_H
#define LOADOUT_GFX_H

#include "font.h"

#define GFX_WIDTH 640
#define GFX_HEIGHT 448
#define GFX_OPAQUE 128

void gfx_init(void);

// Starts a frame on the buffer that is not shown.
void gfx_begin(void);

// Waits for the vertical blank and shows the frame.
void gfx_end(void);

// The full height of the frame in canvas lines (448, or 512 on PAL) and the
// canvas line at its top (0, or -32), for backgrounds that reach the edges.
int gfx_frame_height(void);
int gfx_frame_top(void);

void gfx_rect(float x, float y, float w, float h, unsigned rgb, int alpha);

// A rectangle with its corners cut at 45 degrees, chamfer pixels in.
void gfx_panel(float x, float y, float w, float h, float chamfer, unsigned rgb, int alpha);

// The same, as a frame of the given thickness around a fill.
void gfx_outlined_panel(float x, float y, float w, float h, float chamfer, float thickness, unsigned fill, unsigned line);

void gfx_gradient(float x, float y, float w, float h, unsigned top, unsigned bottom);

// Left to right, from one colour and alpha to another.
void gfx_gradient_across(float x, float y, float w, float h, unsigned rgb, int alpha_left, int alpha_right);

void gfx_triangle(float x1, float y1, float x2, float y2, float x3, float y3, unsigned rgb, int alpha);

// A straight stroke of the given width from one point to the other.
void gfx_line(float x1, float y1, float x2, float y2, float width, unsigned rgb, int alpha);

void gfx_disc(float cx, float cy, float radius, unsigned rgb, int alpha);
void gfx_ring(float cx, float cy, float radius, float width, unsigned rgb, int alpha);

enum gfx_align
{
    GFX_LEFT,
    GFX_CENTER,
    GFX_RIGHT,
};

// Draws UTF-8 text with its baseline at y and returns its width in pixels.
int gfx_text(enum font_style_id style, float x, float y, const char *text, unsigned rgb, enum gfx_align align);

int gfx_text_width(enum font_style_id style, const char *text);

#endif
