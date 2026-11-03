namespace Loadout.Core;

public class ColumnFitTests
{
    private static ColumnSpec Column(double natural, double fitted = 0, double current = 0, double min = 48, double max = 400) =>
        new(natural, min, max, fitted, current);

    [Fact]
    public void ExactFitClampsBetweenMinimumAndAutoMaximum()
    {
        var result = ColumnFit.Compute([Column(30), Column(120), Column(900)], 0, FitMode.Exact);

        Assert.Equal([48, 120, 400], result.Widths);
        Assert.Equal(result.Fitted, result.Widths);
    }

    [Fact]
    public void ExactFitShrinksAColumnThatGrewBefore()
    {
        var result = ColumnFit.Compute([Column(100, fitted: 300, current: 300)], 0, FitMode.Exact);

        Assert.Equal([100], result.Widths);
    }

    [Fact]
    public void GrowOnlyKeepsAWiderFitAndTakesALongerCell()
    {
        var result = ColumnFit.Compute([Column(100, fitted: 300, current: 300), Column(250, fitted: 200, current: 200)], 0, FitMode.GrowOnly);

        Assert.Equal([300, 250], result.Widths);
    }

    [Fact]
    public void GrowOnlyStartsFromTheFittedWidthNotTheFilledOne()
    {
        var columns = new[] { Column(100, fitted: 100, current: 100), Column(150, fitted: 150, current: 699) with { Fill = true } };

        var result = ColumnFit.Compute(columns, 800, FitMode.GrowOnly);

        Assert.Equal(150, result.Fitted[1]);
        Assert.Equal(698, result.Widths[1]);
    }

    [Fact]
    public void FillTakesTheRestOfTheViewportLessTheRowSlack()
    {
        var columns = new[] { Column(100), Column(200) with { Fill = true }, Column(100) };

        var result = ColumnFit.Compute(columns, 1000, FitMode.Exact);

        Assert.Equal([100, 798, 100], result.Widths);
        Assert.Equal(200, result.Fitted[1]);
    }

    [Fact]
    public void FillKeepsItsFittedWidthWhenTheColumnsAreWiderThanTheViewport()
    {
        var columns = new[] { Column(300), Column(200) with { Fill = true }, Column(300) };

        var result = ColumnFit.Compute(columns, 600, FitMode.Exact);

        Assert.Equal([300, 200, 300], result.Widths);
    }

    [Fact]
    public void FillOnlyMovesNothingButTheFillColumn()
    {
        var columns = new[] { Column(500, fitted: 100, current: 100), Column(500, fitted: 200, current: 500) with { Fill = true } };

        var result = ColumnFit.Compute(columns, 450, FitMode.FillOnly);

        Assert.Equal([100, 348], result.Widths);
    }

    [Fact]
    public void UserSizedColumnsKeepTheirWidthAndStopFilling()
    {
        var columns = new[]
        {
            Column(100, fitted: 100, current: 260) with { UserSized = true },
            Column(200, fitted: 200, current: 220) with { Fill = true, UserSized = true },
        };

        var result = ColumnFit.Compute(columns, 1000, FitMode.Exact);

        Assert.Equal([260, 220], result.Widths);
    }

    [Fact]
    public void UserSizedColumnsNeverGoBelowTheirMinimum()
    {
        var result = ColumnFit.Compute([Column(100, current: 20, min: 72) with { UserSized = true }], 0, FitMode.Exact);

        Assert.Equal([72], result.Widths);
    }

    [Fact]
    public void FixedColumnsKeepTheirWidthAndCountTowardsTheFill()
    {
        var columns = new[] { Column(10, current: 56) with { Fixed = true }, Column(100) with { Fill = true } };

        var result = ColumnFit.Compute(columns, 500, FitMode.Exact);

        Assert.Equal([56, 442], result.Widths);
    }

    [Fact]
    public void AnAutoMaximumBelowTheMinimumGivesTheMinimum()
    {
        var result = ColumnFit.Compute([Column(300, min: 120, max: 80)], 0, FitMode.Exact);

        Assert.Equal([120], result.Widths);
    }

    [Fact]
    public void WithAPixelSizeWidthsLandOnThePixelGridAndTheFillRoundsDown()
    {
        const double pixel = 1 / 1.75;
        var columns = new[] { Column(100.2), Column(50) with { Fill = true }, Column(56, current: 56) with { Fixed = true } };

        var result = ColumnFit.Compute(columns, 1000, FitMode.Exact, pixel);

        Assert.Equal(Math.Ceiling(100.2 * 1.75) / 1.75, result.Widths[0], 9);
        Assert.Equal(Math.Floor((1000 - result.Widths[0] - 56 - ColumnFit.RowSlack) * 1.75) / 1.75, result.Widths[1], 9);
        Assert.True(result.Widths.Sum() <= 1000 - ColumnFit.RowSlack);
    }

    [Fact]
    public void AWidthAlreadyOnThePixelGridStaysPut()
    {
        const double pixel = 0.5;
        var result = ColumnFit.Compute([Column(120.5)], 0, FitMode.Exact, pixel);

        Assert.Equal([120.5], result.Widths);
    }
}
