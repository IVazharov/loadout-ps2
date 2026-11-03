// Loadout Bay's text styles, baked from Barlow by ps2/tools/bake_font.py.
//
// Each style has a glyph for every character from ' ' to '~', then the four
// characters below. Text is UTF-8, and gfx_text turns those four into these
// codes; a glyph with no advance is not in the style.

#ifndef LOADOUT_FONT_H
#define LOADOUT_FONT_H

#define FONT_FIRST 0x20
#define FONT_MIDDLE_DOT 0x7F // U+00B7
#define FONT_EN_DASH 0x80    // U+2013
#define FONT_ELLIPSIS 0x81   // U+2026
#define FONT_ANGLE 0x82      // U+203A
#define FONT_GLYPHS (FONT_ANGLE - FONT_FIRST + 1)

enum font_style_id
{
    FONT_HERO,     // Barlow Bold 48: the address. Digits, '.', '-', '%' only.
    FONT_WORDMARK, // Barlow Condensed Bold 30: LOADOUT BAY. Capitals only.
    FONT_HEADLINE, // Barlow Bold 28
    FONT_BODY,     // Barlow SemiBold 24
    FONT_LABEL,    // Barlow Condensed SemiBold 22, for capital labels
    FONT_PILL,     // Barlow Condensed Bold 16, inside START and SELECT. Capitals and digits only.
    FONT_STYLE_COUNT,
};

// x, y, w, h: the glyph's place in the atlas. left, top: where that image sits
// from the pen on the baseline (top is negative above it).
struct font_glyph
{
    unsigned short x, y;
    unsigned char w, h;
    signed char left, top;
    unsigned char advance;
};

struct font_style
{
    unsigned char size;
    unsigned char tracking;
    struct font_glyph glyphs[FONT_GLYPHS];
};

extern const int font_atlas_width;
extern const int font_atlas_height;
extern const unsigned char font_atlas[];
extern const struct font_style font_styles[FONT_STYLE_COUNT];

#endif
