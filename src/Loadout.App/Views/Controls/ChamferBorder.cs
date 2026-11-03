using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Loadout.App.Controls;

// The Loadout chamfer: a border with its four corners cut at 45 degrees.
// It is the only place a control's chamfer is drawn.
public sealed class ChamferBorder : Decorator
{
    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ClipContentProperty = DependencyProperty.Register(
        nameof(ClipContent), typeof(bool), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    private Geometry? _fill;
    private Geometry? _stroke;
    private (Size Size, double Chamfer, double Thickness) _built;
    private bool _clipped;

    /// <summary>Leg length of each cut in px, measured along the edge.</summary>
    public double Chamfer
    {
        get => (double)GetValue(ChamferProperty);
        set => SetValue(ChamferProperty, value);
    }

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    /// <summary>Uniform border width: 1 or 2.</summary>
    public double BorderThickness
    {
        get => (double)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <summary>Clips the child to the inner octagon, for content that touches the edges.</summary>
    public bool ClipContent
    {
        get => (bool)GetValue(ClipContentProperty);
        set => SetValue(ClipContentProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var inset = Inset();
        var width = inset.Left + inset.Right;
        var height = inset.Top + inset.Bottom;
        if (Child is null)
        {
            return new Size(width, height);
        }

        Child.Measure(new Size(Math.Max(0, constraint.Width - width), Math.Max(0, constraint.Height - height)));
        return new Size(Child.DesiredSize.Width + width, Child.DesiredSize.Height + height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is null)
        {
            return arrangeSize;
        }

        var inset = Inset();
        var slot = new Rect(
            inset.Left,
            inset.Top,
            Math.Max(0, arrangeSize.Width - inset.Left - inset.Right),
            Math.Max(0, arrangeSize.Height - inset.Top - inset.Bottom));
        Child.Arrange(slot);

        if (ClipContent)
        {
            // The inner edge of the border, in the child's coordinates.
            var t = BorderThickness;
            var inner = new Rect(t - slot.X, t - slot.Y, Math.Max(0, arrangeSize.Width - 2 * t), Math.Max(0, arrangeSize.Height - 2 * t));
            Child.Clip = Octagon(inner, Leg(Chamfer, -t));
            _clipped = true;
        }
        else if (_clipped)
        {
            Child.Clip = null;
            _clipped = false;
        }

        return arrangeSize;
    }

    // The fill covers the outer octagon; the pen runs on the centre line of the border, so the stroke keeps
    // the same distance from the outer cut on all eight sides.
    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = RenderSize;
        var t = BorderThickness;
        var background = Background;
        var pen = t > 0 && BorderBrush is { } brush ? new Pen(brush, t) { LineJoin = PenLineJoin.Miter } : null;
        if (background is null && pen is null)
        {
            return;
        }

        if (_fill is null || _built != (size, Chamfer, t))
        {
            _fill = Octagon(new Rect(size), Chamfer);
            _stroke = Octagon(new Rect(t / 2, t / 2, Math.Max(0, size.Width - t), Math.Max(0, size.Height - t)), Leg(Chamfer, -t / 2));
            _built = (size, Chamfer, t);
        }

        // Keeps the four straight edges on whole pixels; the 45 degree cuts anti-alias by design.
        drawingContext.PushGuidelineSet(new GuidelineSet([0, t, size.Width - t, size.Width], [0, t, size.Height - t, size.Height]));
        if (background is not null)
        {
            drawingContext.DrawGeometry(background, null, _fill);
        }

        if (pen is not null)
        {
            drawingContext.DrawGeometry(null, pen, _stroke);
        }

        drawingContext.Pop();
    }

    private Thickness Inset()
    {
        var t = BorderThickness;
        var padding = Padding;
        return new Thickness(padding.Left + t, padding.Top + t, padding.Right + t, padding.Bottom + t);
    }

    // The leg of an octagon offset by d (outward positive) from one with leg c: its cuts stay parallel.
    private static double Leg(double c, double d) => Math.Max(0, c + d * (2 - Math.Sqrt(2)));

    private static StreamGeometry Octagon(Rect r, double c)
    {
        c = Math.Max(0, Math.Min(c, Math.Min(r.Width, r.Height) / 2));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(r.Left + c, r.Top), isFilled: true, isClosed: true);
            context.PolyLineTo(
                [
                    new Point(r.Right - c, r.Top),
                    new Point(r.Right, r.Top + c),
                    new Point(r.Right, r.Bottom - c),
                    new Point(r.Right - c, r.Bottom),
                    new Point(r.Left + c, r.Bottom),
                    new Point(r.Left, r.Bottom - c),
                    new Point(r.Left, r.Top + c),
                ],
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }
}
