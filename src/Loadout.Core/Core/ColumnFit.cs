namespace Loadout.Core;

/// <summary>How far a fit may move the columns that are not user-sized.</summary>
public enum FitMode
{
    /// <summary>Every such column takes its fitted width, growing or shrinking.</summary>
    Exact,

    /// <summary>Columns only grow, so they don't twitch while a long task updates rows.</summary>
    GrowOnly,

    /// <summary>Only the fill column moves, after the viewport changed width.</summary>
    FillOnly,
}

/// <summary>
/// One table column as the fit sees it. <see cref="Natural"/> is the widest of the header and the
/// cells, padding included; <see cref="Fitted"/> is the width the last fit gave it, before any fill.
/// </summary>
public sealed record ColumnSpec(
    double Natural,
    double MinWidth,
    double MaxAutoWidth,
    double Fitted,
    double Current,
    bool UserSized = false,
    bool Fixed = false,
    bool Fill = false);

/// <summary>The fit's answer: each column's fitted width, and the width to show, which adds the fill.</summary>
public sealed record ColumnFitResult(double[] Fitted, double[] Widths);

/// <summary>The arithmetic of the tables' column sizing, kept free of WPF so it can be tested.</summary>
public static class ColumnFit
{
    /// <summary>WPF's GridViewRowPresenter asks for 2 DIPs past its last column.</summary>
    public const double RowSlack = 2;

    /// <summary>
    /// Fits each column that is neither fixed nor user-sized between its minimum and its auto maximum,
    /// then widens the fill column to take the rest of <paramref name="viewport"/> (less
    /// <see cref="RowSlack"/>) when the columns are narrower than it. A user-sized column keeps its width, but never goes below its minimum.
    /// With <paramref name="pixel"/> (a device pixel in DIPs), widths land on the pixel grid, the fill
    /// rounded down: layout rounding would otherwise push the sum past the viewport.
    /// </summary>
    public static ColumnFitResult Compute(IReadOnlyList<ColumnSpec> columns, double viewport, FitMode mode, double pixel = 0)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var fitted = new double[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            fitted[i] = column switch
            {
                { Fixed: true } => column.Current,
                { UserSized: true } => Math.Max(column.Current, column.MinWidth),
                _ => mode switch
                {
                    FitMode.Exact => Clamp(column),
                    FitMode.GrowOnly => Math.Max(column.Fitted, Clamp(column)),
                    _ => column.Fitted,
                },
            };
            if (!column.Fixed)
            {
                fitted[i] = Up(fitted[i], pixel);
            }
        }

        var widths = (double[])fitted.Clone();
        var fill = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i] is { Fill: true, UserSized: false, Fixed: false })
            {
                fill = i;
            }
        }

        if (fill >= 0 && viewport > 0)
        {
            var total = fitted.Sum();
            if (total < viewport)
            {
                widths[fill] = Down(viewport - (total - fitted[fill]) - RowSlack, pixel);
            }
        }

        return new ColumnFitResult(fitted, widths);
    }

    // The small allowance keeps a width already on the grid from moving a pixel by float error.
    private static double Up(double width, double pixel) => pixel > 0 ? Math.Ceiling((width / pixel) - 1e-6) * pixel : width;

    private static double Down(double width, double pixel) => pixel > 0 ? Math.Floor((width / pixel) + 1e-6) * pixel : width;

    private static double Clamp(ColumnSpec column) =>
        Math.Max(column.MinWidth, Math.Min(column.Natural, Math.Max(column.MaxAutoWidth, column.MinWidth)));
}
