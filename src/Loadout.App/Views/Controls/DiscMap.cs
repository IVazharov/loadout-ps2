using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Loadout.Discs;

namespace Loadout.App.Controls;

/// <summary>
/// The disc map's colours, the same in both themes except "not read yet", which follows the theme's track. The trouble
/// shades run from a true yellow to a dark red, each lighter than the next and as far apart as five such steps can be on
/// the dark surface (OKLab distance at least 12.7 between neighbours, 9.6 for colour-blind sight). The markers are not
/// amber, which these shades would hide.
/// </summary>
public static class DiscMapBrushes
{
    private static readonly Brush[] Fixed =
    [
        Frozen(0x5B, 0x9B, 0xD5), // read normally: soft blue
        Frozen(0xFE, 0xDD, 0x0E), // read on a later pass: yellow
        Frozen(0xFE, 0xAF, 0x55), // read in 32 KB pieces: light orange
        Frozen(0xED, 0x75, 0x03), // read sector by sector: orange
        Frozen(0xDB, 0x2F, 0x05), // read only after cleaning: red-orange
        Brushes.Transparent,      // not read yet: the theme's
        Frozen(0xA7, 0x04, 0x21), // could not be read: dark red
    ];

    /// <summary>The legend's order: how hard each part was to read, then what is still to read.</summary>
    public static IReadOnlyList<DiscMapShade> LegendOrder { get; } =
    [
        DiscMapShade.ReadNormally,
        DiscMapShade.ReadLater,
        DiscMapShade.ReadInPieces,
        DiscMapShade.ReadBySector,
        DiscMapShade.ReadAfterCleaning,
        DiscMapShade.Failed,
        DiscMapShade.NotRead,
    ];

    public static Brush For(DiscMapShade shade, Brush notRead) => shade == DiscMapShade.NotRead ? notRead : Fixed[(int)shade];

    public static string Label(DiscMapShade shade) => shade switch
    {
        DiscMapShade.ReadNormally => "Read normally",
        DiscMapShade.ReadLater => "Read on a later try",
        DiscMapShade.ReadInPieces => "Read in 32 KB pieces",
        DiscMapShade.ReadBySector => "Read sector by sector",
        DiscMapShade.ReadAfterCleaning => "Read after cleaning",
        DiscMapShade.NotRead => "Not read yet",
        _ => "Could not be read",
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>What the disc map draws: a copy's stretches, the disc's shape, and where the drive reads now.</summary>
public sealed record DiscMapView(IReadOnlyList<DiscMapRange> Ranges, long Sectors, DiscMedia Media, long Layer0Sectors, long CurrentSector)
{
    /// <summary>Where the disc was cleaned (mm from the centre): a small cloth is drawn on each ring.</summary>
    public IReadOnlyList<double> CleanedRingsMm { get; init; } = [];

    /// <summary>How far an install on the PS2 that follows the copy has got, as a sector; -1 when none follows it.</summary>
    public long InstallSector { get; init; } = -1;

    public static DiscMapView Empty { get; } = new([], 0, DiscMedia.Dvd, 0, -1);
}

/// <summary>Shared by the bar and the picture: the map to draw, the theme's brushes, and the hover text.</summary>
public abstract class DiscMapElement : FrameworkElement
{
    public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(
        nameof(View), typeof(DiscMapView), typeof(DiscMapElement),
        new FrameworkPropertyMetadata(DiscMapView.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NotReadBrushProperty = DependencyProperty.Register(
        nameof(NotReadBrush), typeof(Brush), typeof(DiscMapElement),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(DiscMapElement),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerBrushProperty = DependencyProperty.Register(
        nameof(MarkerBrush), typeof(Brush), typeof(DiscMapElement),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DescribeProperty = DependencyProperty.Register(
        nameof(Describe), typeof(Func<long, long, string>), typeof(DiscMapElement), new PropertyMetadata(null));

    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    protected DiscMapElement()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 200);
        _tip.Opened += (_, _) => UpdateTip(Mouse.GetPosition(this));
    }

    public DiscMapView View
    {
        get => (DiscMapView)GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    public Brush NotReadBrush
    {
        get => (Brush)GetValue(NotReadBrushProperty);
        set => SetValue(NotReadBrushProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush MarkerBrush
    {
        get => (Brush)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    /// <summary>The hover text for the sectors [from, to): where they are, how they were read, which file.</summary>
    public Func<long, long, string>? Describe
    {
        get => (Func<long, long, string>?)GetValue(DescribeProperty);
        set => SetValue(DescribeProperty, value);
    }

    /// <summary>The sectors under a point, or null where there is no data.</summary>
    protected abstract (long From, long To)? SectorsAt(Point point);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateTip(e.GetPosition(this));
    }

    protected FormattedText Text(string text, double size) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface((TryFindResource("FontUi") as FontFamily) ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        size,
        Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void UpdateTip(Point point)
    {
        var sectors = SectorsAt(point);
        var text = sectors is { } s && Describe is { } describe ? describe(s.From, s.To) : null;
        _tip.Content = text;
        _tip.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>
/// The whole disc as one bar, centre at the left and edge at the right: each pixel shows the worst state of its sectors.
/// Size marks every half GB under it, "Centre" and "Edge" at its ends, and a marker where the drive reads now.
/// </summary>
public sealed class DiscMapBar : DiscMapElement
{
    private const double BarTop = 10;
    private const double BarHeight = 24;

    // Under the bar: room for the PS2's marker, which the size labels sit below.
    private const double InstallBand = 10;

    public static readonly DependencyProperty InstallBrushProperty = DependencyProperty.Register(
        nameof(InstallBrush), typeof(Brush), typeof(DiscMapBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public DiscMapBar()
    {
        Height = BarTop + BarHeight + InstallBand + 17;
        SnapsToDevicePixels = true;
    }

    /// <summary>The marker under the bar for how far an install on the PS2 that follows the copy has got.</summary>
    public Brush InstallBrush
    {
        get => (Brush)GetValue(InstallBrushProperty);
        set => SetValue(InstallBrushProperty, value);
    }

    protected override (long From, long To)? SectorsAt(Point point)
    {
        var view = View;
        if (view.Sectors <= 0 || ActualWidth <= 0 || point.Y < BarTop - 4 || point.Y > BarTop + BarHeight + 4)
        {
            return null;
        }

        var column = Math.Clamp((int)point.X, 0, (int)ActualWidth - 1);
        var from = (long)((double)column * view.Sectors / ActualWidth);
        var to = Math.Max(from + 1, (long)((double)(column + 1) * view.Sectors / ActualWidth));
        return (from, Math.Min(to, view.Sectors));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var view = View;
        var width = (int)Math.Floor(ActualWidth);
        if (width <= 0)
        {
            return;
        }

        var bar = new Rect(0, BarTop, width, BarHeight);
        drawingContext.DrawRectangle(NotReadBrush, null, bar);
        if (view.Sectors > 0)
        {
            // Runs of one shade as one rectangle each.
            var columns = DiscMapShades.Columns(view.Ranges, view.Sectors, width);
            var start = 0;
            for (var x = 1; x <= width; x++)
            {
                if (x == width || columns[x] != columns[start])
                {
                    if (columns[start] != DiscMapShade.NotRead)
                    {
                        drawingContext.DrawRectangle(DiscMapBrushes.For(columns[start], NotReadBrush), null, new Rect(start, BarTop, x - start, BarHeight));
                    }

                    start = x;
                }
            }

            // Size marks every half GB, or every 100 MB on a CD.
            var markPen = new Pen(Foreground, 1);
            markPen.Freeze();
            var bytes = view.Sectors * DiscSectors.Size;
            var step = bytes < 1_500_000_000 ? 100_000_000L : 500_000_000L;
            for (var at = 0L; at <= bytes; at += step)
            {
                var x = Math.Round((double)at / bytes * (width - 1)) + 0.5;
                drawingContext.DrawLine(markPen, new Point(x, BarTop + BarHeight), new Point(x, BarTop + BarHeight + 4));
                var label = Text(
                    step < 500_000_000
                        ? (at / 1_000_000).ToString(CultureInfo.CurrentCulture) + " MB"
                        : (at / 1_000_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + " GB",
                    11);
                var left = Math.Clamp(x - (label.Width / 2), 0, width - label.Width);
                drawingContext.DrawText(label, new Point(left, BarTop + BarHeight + InstallBand));
            }

            // Where the drive reads now: a small amber triangle over the bar.
            if (view.CurrentSector >= 0 && view.CurrentSector < view.Sectors)
            {
                var x = (double)view.CurrentSector / view.Sectors * width;
                var marker = new StreamGeometry();
                using (var context = marker.Open())
                {
                    context.BeginFigure(new Point(x - 5, 0), isFilled: true, isClosed: true);
                    context.LineTo(new Point(x + 5, 0), isStroked: false, isSmoothJoin: false);
                    context.LineTo(new Point(x, BarTop - 1), isStroked: false, isSmoothJoin: false);
                }

                marker.Freeze();
                drawingContext.DrawGeometry(MarkerBrush, null, marker);
            }

            // How far the install on the PS2 has got: a small triangle under the bar, pointing up at it.
            if (view.InstallSector >= 0 && view.InstallSector <= view.Sectors)
            {
                var x = Math.Min((double)view.InstallSector / view.Sectors * width, width - 1);
                var marker = new StreamGeometry();
                using (var context = marker.Open())
                {
                    context.BeginFigure(new Point(x, BarTop + BarHeight + 1), isFilled: true, isClosed: true);
                    context.LineTo(new Point(x + 5, BarTop + BarHeight + InstallBand), isStroked: false, isSmoothJoin: false);
                    context.LineTo(new Point(x - 5, BarTop + BarHeight + InstallBand), isStroked: false, isSmoothJoin: false);
                }

                marker.Freeze();
                drawingContext.DrawGeometry(InstallBrush, null, marker);
            }
        }
    }
}

/// <summary>
/// The disc as seen from its readable side: its data area drawn as thin rings, each in the worst state of the sectors at
/// that distance from the centre. A sector's angle can't be known (the track is one spiral with an unknown start), so a
/// scratch from the centre outwards shows as several rings.
/// </summary>
public sealed class DiscPicture : DiscMapElement
{
    private const double DiscMm = 60;
    private const double HoleMm = 7.5;
    private const double ClampMm = 22;

    public static readonly DependencyProperty DiscBrushProperty = DependencyProperty.Register(
        nameof(DiscBrush), typeof(Brush), typeof(DiscPicture),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoleBrushProperty = DependencyProperty.Register(
        nameof(HoleBrush), typeof(Brush), typeof(DiscPicture),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkedRingsProperty = DependencyProperty.Register(
        nameof(MarkedRings), typeof(IReadOnlyList<double>), typeof(DiscPicture),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The disc's colour outside its data.</summary>
    public Brush DiscBrush
    {
        get => (Brush)GetValue(DiscBrushProperty);
        set => SetValue(DiscBrushProperty, value);
    }

    /// <summary>The colour of the centre hole: the page behind the disc.</summary>
    public Brush HoleBrush
    {
        get => (Brush)GetValue(HoleBrushProperty);
        set => SetValue(HoleBrushProperty, value);
    }

    /// <summary>Distances from the centre in mm to circle in the marker colour, such as the trouble spots to clean.</summary>
    public IReadOnlyList<double>? MarkedRings
    {
        get => (IReadOnlyList<double>?)GetValue(MarkedRingsProperty);
        set => SetValue(MarkedRingsProperty, value);
    }

    private double Scale => (Math.Min(ActualWidth, ActualHeight) / 2) - 1;

    private Point Centre => new(ActualWidth / 2, ActualHeight / 2);

    protected override (long From, long To)? SectorsAt(Point point)
    {
        var view = View;
        var scale = Scale;
        if (view.Sectors <= 0 || scale <= 0)
        {
            return null;
        }

        var mm = (point - Centre).Length / scale * DiscMm;
        var stretches = DiscMapShades.SectorsAt(mm - 0.25, mm + 0.25, view.Media, view.Sectors, view.Layer0Sectors);
        return stretches.Count == 0 ? null : (stretches[0].From, stretches[^1].To);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var scale = Scale;
        if (scale <= 4)
        {
            return;
        }

        var view = View;
        var centre = Centre;
        var px = scale / DiscMm;
        drawingContext.DrawEllipse(DiscBrush, null, centre, scale, scale);
        var edge = new Pen(Foreground, 1);
        edge.Freeze();
        drawingContext.DrawEllipse(null, edge, centre, scale, scale);
        drawingContext.DrawEllipse(null, edge, centre, ClampMm * px, ClampMm * px);
        if (view.Sectors > 0)
        {
            var start = view.Media == DiscMedia.Cd ? DiscGeometry.CdDataStartMm : DiscGeometry.DvdDataStartMm;
            var lastSector = view.Layer0Sectors > 0 ? view.Layer0Sectors - 1 : view.Sectors - 1;
            var end = DiscGeometry.RadiusMm(lastSector, view.Media);

            // One ring per pixel of radius; runs of one shade as one thicker ring.
            var rings = (int)Math.Ceiling((end - start) * px);
            DiscMapShade? runShade = null;
            var runFrom = 0;
            for (var i = 0; i <= rings; i++)
            {
                DiscMapShade? shade = null;
                if (i < rings)
                {
                    var from = start + (i / px);
                    foreach (var (a, b) in DiscMapShades.SectorsAt(from, Math.Min(end, from + (1 / px)), view.Media, view.Sectors, view.Layer0Sectors))
                    {
                        var worst = DiscMapShades.Worst(view.Ranges, a, b);
                        if (worst is { } w)
                        {
                            shade = shade is { } s ? DiscMapShades.Worse(s, w) : w;
                        }
                    }

                    shade ??= DiscMapShade.NotRead;
                }

                if (i == rings || shade != runShade)
                {
                    if (runShade is { } done && i > runFrom)
                    {
                        // A ring from runFrom to i pixels out, drawn as a stroke on its middle radius.
                        var inner = (start * px) + runFrom;
                        var thickness = i - runFrom;
                        var pen = new Pen(DiscMapBrushes.For(done, NotReadBrush), thickness + 0.6);
                        pen.Freeze();
                        var radius = inner + (thickness / 2.0);
                        drawingContext.DrawEllipse(null, pen, centre, radius, radius);
                    }

                    runShade = shade;
                    runFrom = i;
                }
            }

            foreach (var mm in MarkedRings ?? [])
            {
                var marker = new Pen(MarkerBrush, 2);
                marker.Freeze();
                drawingContext.DrawEllipse(null, marker, centre, mm * px, mm * px);
            }

            foreach (var mm in view.CleanedRingsMm)
            {
                DrawCloth(drawingContext, centre, mm * px);
            }

            if (view.CurrentSector >= 0 && view.CurrentSector < view.Sectors)
            {
                var at = DiscGeometry.RadiusMm(view.CurrentSector, view.Media, view.Layer0Sectors) * px;
                drawingContext.DrawEllipse(MarkerBrush, null, new Point(centre.X + at, centre.Y), 3, 3);
            }
        }

        drawingContext.DrawEllipse(HoleBrush, edge, centre, HoleMm * px, HoleMm * px);
    }

    private static readonly Brush ClothFill = Frozen(Color.FromRgb(0xF4, 0xE7, 0xCF));
    private static readonly Pen ClothEdge = FrozenPen(Color.FromRgb(0x8A, 0x6D, 0x45), 1);

    // A tiny folded cloth on a ring, at the upper right: where on the ring is unknown, how far out is not.
    private static void DrawCloth(DrawingContext drawingContext, Point centre, double radius)
    {
        var at = new Point(centre.X + (radius * Math.Cos(-Math.PI / 4)), centre.Y + (radius * Math.Sin(-Math.PI / 4)));
        drawingContext.PushTransform(new RotateTransform(-15, at.X, at.Y));
        drawingContext.DrawRoundedRectangle(ClothFill, ClothEdge, new Rect(at.X - 6, at.Y - 4.5, 12, 9), 2, 2);
        drawingContext.DrawLine(ClothEdge, new Point(at.X - 3, at.Y - 4.5), new Point(at.X - 3, at.Y + 4.5));
        drawingContext.DrawLine(ClothEdge, new Point(at.X + 1, at.Y - 4.5), new Point(at.X + 1, at.Y + 4.5));
        drawingContext.Pop();
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
