#include "gfx.h"

#include <dmaKit.h>
#include <gsKit.h>
#include <malloc.h>
#include <math.h>
#include <string.h>

#define CIRCLE_SEGMENTS 32

// The canvas's content area (x 64-576, y 44-404, the design's title-safe area)
// is spread over the frame, so more fits; text keeps its size. The frame fills
// the TV's picture, 480 lines on NTSC and 576 on PAL. Across, gsKit centres the
// 640 pixels with 5% of the picture either side, and FRAME_MARGIN_X adds as much
// again, so the content sits 10% from each edge; FRAME_MARGIN_Y_SHARE gives it
// the same distance from the top and bottom.
#define DESIGN_LEFT 64.0f
#define DESIGN_TOP 44.0f
#define DESIGN_WIDTH 512.0f
#define DESIGN_HEIGHT 360.0f
#define FRAME_MARGIN_X 32.0f
#define FRAME_MARGIN_Y_SHARE 0.1f
#define SCALE_X ((GFX_WIDTH - 2 * FRAME_MARGIN_X) / DESIGN_WIDTH)
#define NTSC_LINES 480
#define PAL_LINES 576
// A PAL pixel is this much wider than it is tall; an NTSC one is about square.
#define PAL_PIXEL_ASPECT 1.2f

static GSGLOBAL *gs;
static GSTEXTURE atlas;
static float margin_y, scale_y;
static float pixel_aspect;

static float frame_x(float x)
{
    return FRAME_MARGIN_X + (x - DESIGN_LEFT) * SCALE_X;
}

static float frame_y(float y)
{
    return margin_y + (y - DESIGN_TOP) * scale_y;
}

static u64 color(unsigned rgb, int alpha)
{
    return GS_SETREG_RGBAQ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, alpha, 0);
}

// The atlas keeps four bits of alpha a pixel; the GS gets 32-bit texels whose
// colour is 0x80, so modulating by the vertex colour draws text in that colour.
static void upload_atlas(void)
{
    int pixels = font_atlas_width * font_atlas_height;
    u32 *texels = memalign(128, gsKit_texture_size_ee(font_atlas_width, font_atlas_height, GS_PSM_CT32));
    for (int i = 0; i < pixels; i++)
    {
        u32 level = (font_atlas[i / 2] >> ((i & 1) * 4)) & 0xF;
        texels[i] = ((level * GFX_OPAQUE + 7) / 15) << 24 | 0x808080;
    }

    atlas.Width = font_atlas_width;
    atlas.Height = font_atlas_height;
    atlas.PSM = GS_PSM_CT32;
    atlas.Filter = GS_FILTER_NEAREST;
    atlas.Mem = texels;
    atlas.Vram = gsKit_vram_alloc(gs, gsKit_texture_size(atlas.Width, atlas.Height, atlas.PSM), GSKIT_ALLOC_USERBUFFER);
    gsKit_texture_upload(gs, &atlas);
}

void gfx_init(void)
{
    gs = gsKit_init_global();
    // gsKit's default frame is shorter than the picture and leaves black strips above and below it.
    int pal = gs->Mode == GS_MODE_PAL;
    gs->Height = pal ? PAL_LINES : NTSC_LINES;
    margin_y = gs->Height * FRAME_MARGIN_Y_SHARE;
    scale_y = (gs->Height - 2 * margin_y) / DESIGN_HEIGHT;
    pixel_aspect = pal ? PAL_PIXEL_ASPECT : 1.0f;
    gs->PSM = GS_PSM_CT32;
    gs->ZBuffering = GS_SETTING_OFF;
    gs->DoubleBuffering = GS_SETTING_ON;
    gs->PrimAlphaEnable = GS_SETTING_ON;

    dmaKit_init(D_CTRL_RELE_OFF, D_CTRL_MFD_OFF, D_CTRL_STS_UNSPEC, D_CTRL_STD_OFF, D_CTRL_RCYC_8, 1 << DMA_CHANNEL_GIF);
    dmaKit_chan_init(DMA_CHANNEL_GIF);

    gsKit_init_screen(gs);
    gsKit_mode_switch(gs, GS_ONESHOT);
    gsKit_set_test(gs, GS_ZTEST_OFF);
    gsKit_set_primalpha(gs, GS_SETREG_ALPHA(0, 1, 0, 1, 0), 0);
    gsKit_set_clamp(gs, GS_CMODE_CLAMP);
    upload_atlas();
}

int gfx_frame_height(void)
{
    return (int)(gs->Height / scale_y) + 2;
}

int gfx_frame_top(void)
{
    return (int)floorf(DESIGN_TOP - margin_y / scale_y) - 1;
}

void gfx_begin(void)
{
    gsKit_clear(gs, color(0x000000, GFX_OPAQUE));
}

void gfx_end(void)
{
    gsKit_queue_exec(gs);
    gsKit_sync_flip(gs);
}

void gfx_rect(float x, float y, float w, float h, unsigned rgb, int alpha)
{
    gsKit_prim_sprite(gs, frame_x(x), frame_y(y), frame_x(x + w), frame_y(y + h), 1, color(rgb, alpha));
}

void gfx_panel(float x, float y, float w, float h, float chamfer, unsigned rgb, int alpha)
{
    float c = chamfer;
    float l = frame_x(x), r = frame_x(x + w), t = frame_y(y), b = frame_y(y + h);
    float cx = c * SCALE_X, cy = c * scale_y;
    float fan[16] = {
        l + cx, t, r - cx, t, r, t + cy, r, b - cy,
        r - cx, b, l + cx, b, l, b - cy, l, t + cy,
    };
    gsKit_prim_triangle_fan(gs, fan, 8, 1, color(rgb, alpha));
}

void gfx_outlined_panel(float x, float y, float w, float h, float chamfer, float thickness, unsigned fill, unsigned line)
{
    gfx_panel(x, y, w, h, chamfer, line, GFX_OPAQUE);
    // The inner cut sits the same distance from the outer one all round.
    float inner = chamfer - thickness * 0.4142f;
    gfx_panel(x + thickness, y + thickness, w - 2 * thickness, h - 2 * thickness, inner > 0 ? inner : 0, fill, GFX_OPAQUE);
}

void gfx_gradient(float x, float y, float w, float h, unsigned top, unsigned bottom)
{
    float l = frame_x(x), r = frame_x(x + w), t = frame_y(y), b = frame_y(y + h);
    gsKit_prim_quad_gouraud(gs, l, t, r, t, l, b, r, b, 1,
                            color(top, GFX_OPAQUE), color(top, GFX_OPAQUE),
                            color(bottom, GFX_OPAQUE), color(bottom, GFX_OPAQUE));
}

void gfx_gradient_across(float x, float y, float w, float h, unsigned rgb, int alpha_left, int alpha_right)
{
    float l = frame_x(x), r = frame_x(x + w), t = frame_y(y), b = frame_y(y + h);
    gsKit_prim_quad_gouraud(gs, l, t, r, t, l, b, r, b, 1,
                            color(rgb, alpha_left), color(rgb, alpha_right),
                            color(rgb, alpha_left), color(rgb, alpha_right));
}

void gfx_triangle(float x1, float y1, float x2, float y2, float x3, float y3, unsigned rgb, int alpha)
{
    // As a fan, which draws every small triangle; gsKit's triangle call left some as a dot.
    float fan[6] = {frame_x(x1), frame_y(y1), frame_x(x2), frame_y(y2), frame_x(x3), frame_y(y3)};
    gsKit_prim_triangle_fan(gs, fan, 3, 1, color(rgb, alpha));
}

void gfx_line(float x1, float y1, float x2, float y2, float width, unsigned rgb, int alpha)
{
    float ax = frame_x(x1), ay = frame_y(y1), bx = frame_x(x2), by = frame_y(y2);
    float dx = bx - ax, dy = by - ay;
    float length = sqrtf(dx * dx + dy * dy);
    if (length <= 0)
    {
        return;
    }

    float half = width * scale_y / 2;
    float nx = -dy / length * half, ny = dx / length * half;
    gsKit_prim_quad(gs, ax + nx, ay + ny, ax - nx, ay - ny, bx + nx, by + ny, bx - nx, by - ny, 1, color(rgb, alpha));
}

void gfx_disc(float cx, float cy, float radius, unsigned rgb, int alpha)
{
    float fan[2 * (CIRCLE_SEGMENTS + 2)];
    float x = frame_x(cx), y = frame_y(cy), r = radius * scale_y;
    fan[0] = x;
    fan[1] = y;
    for (int i = 0; i <= CIRCLE_SEGMENTS; i++)
    {
        float angle = 2 * (float)M_PI * i / CIRCLE_SEGMENTS;
        fan[2 + 2 * i] = x + r / pixel_aspect * cosf(angle);
        fan[3 + 2 * i] = y + r * sinf(angle);
    }

    gsKit_prim_triangle_fan(gs, fan, CIRCLE_SEGMENTS + 2, 1, color(rgb, alpha));
}

void gfx_ring(float cx, float cy, float radius, float width, unsigned rgb, int alpha)
{
    float x = frame_x(cx), y = frame_y(cy), outer = radius * scale_y, inner = (radius - width) * scale_y;
    float outer_x = outer / pixel_aspect, inner_x = inner / pixel_aspect;
    for (int i = 0; i < CIRCLE_SEGMENTS; i++)
    {
        float a = 2 * (float)M_PI * i / CIRCLE_SEGMENTS, b = 2 * (float)M_PI * (i + 1) / CIRCLE_SEGMENTS;
        gsKit_prim_quad(gs, x + outer_x * cosf(a), y + outer * sinf(a), x + inner_x * cosf(a), y + inner * sinf(a),
                        x + outer_x * cosf(b), y + outer * sinf(b), x + inner_x * cosf(b), y + inner * sinf(b), 1,
                        color(rgb, alpha));
    }
}

// Reads one character of UTF-8 and returns its glyph code in font.h, '?' for one the fonts lack.
static int next_code(const char **text)
{
    const unsigned char *s = (const unsigned char *)*text;
    unsigned code = s[0];
    int length = 1;
    if (code >= 0xE0 && s[1] != 0 && s[2] != 0)
    {
        code = ((code & 0x0F) << 12) | ((s[1] & 0x3F) << 6) | (s[2] & 0x3F);
        length = 3;
    }
    else if (code >= 0xC0 && s[1] != 0)
    {
        code = ((code & 0x1F) << 6) | (s[1] & 0x3F);
        length = 2;
    }

    *text += length;
    switch (code)
    {
    case 0x00B7:
        return FONT_MIDDLE_DOT;
    case 0x2013:
        return FONT_EN_DASH;
    case 0x2026:
        return FONT_ELLIPSIS;
    case 0x203A:
        return FONT_ANGLE;
    default:
        return code >= FONT_FIRST && code < 0x7F ? (int)code : '?';
    }
}

// The width in pixels on the frame.
static int frame_width(enum font_style_id style, const char *text)
{
    const struct font_style *font = &font_styles[style];
    int width = 0;
    while (*text != '\0')
    {
        width += font->glyphs[next_code(&text) - FONT_FIRST].advance;
        if (*text != '\0')
        {
            width += font->tracking;
        }
    }

    return width;
}

int gfx_text_width(enum font_style_id style, const char *text)
{
    return (int)ceilf(frame_width(style, text) / SCALE_X);
}

int gfx_text(enum font_style_id style, float x, float y, const char *text, unsigned rgb, enum gfx_align align)
{
    const struct font_style *font = &font_styles[style];
    int width = frame_width(style, text);
    // Glyphs sit on whole pixels, so the text stays sharp.
    int pen = (int)lroundf(frame_x(x)) - (align == GFX_RIGHT ? width : align == GFX_CENTER ? width / 2 : 0);
    int baseline = (int)lroundf(frame_y(y));
    u64 tint = color(rgb, GFX_OPAQUE);
    while (*text != '\0')
    {
        const struct font_glyph *glyph = &font->glyphs[next_code(&text) - FONT_FIRST];
        if (glyph->w > 0)
        {
            float left = (float)(pen + glyph->left), top = (float)(baseline + glyph->top);
            gsKit_prim_sprite_texture(gs, &atlas, left, top, glyph->x, glyph->y, left + glyph->w, top + glyph->h,
                                      glyph->x + glyph->w, glyph->y + glyph->h, 1, tint);
        }

        pen += glyph->advance + font->tracking;
    }

    return (int)ceilf(width / SCALE_X);
}
