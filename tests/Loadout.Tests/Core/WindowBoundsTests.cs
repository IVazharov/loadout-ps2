namespace Loadout.Core;

public class WindowBoundsTests
{
    [Fact]
    public void BoundsSurviveTheirJson()
    {
        var bounds = new WindowBounds(-1900, 40, 2065, 1575, Maximized: true);

        Assert.Equal(bounds, WindowBounds.Parse(bounds.ToJson()));
    }

    [Fact]
    public void TheJsonUsesTheDocumentedNames()
    {
        var json = new WindowBounds(120, 80, 2065, 1575, Maximized: false).ToJson();

        Assert.Equal("""{"left":120,"top":80,"width":2065,"height":1575,"maximized":false}""", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"left":1,"top":2,"width":300}""")]
    [InlineData("""{"left":1,"top":2,"width":0,"height":200}""")]
    [InlineData("""{"left":1,"top":2,"width":300,"height":-5}""")]
    public void TextThatIsNotBoundsGivesNull(string? json) => Assert.Null(WindowBounds.Parse(json));

    [Fact]
    public void AWindowInsideTheAreaStaysAsItIs()
    {
        var bounds = new WindowBounds(100, 50, 800, 600, Maximized: false);

        Assert.Equal(bounds, bounds.FitWithin(0, 0, 1920, 1040));
    }

    [Fact]
    public void AWindowTallerAndWiderThanTheAreaFillsIt()
    {
        var bounds = new WindowBounds(-78, -86, 1180, 900, Maximized: false);

        Assert.Equal(new WindowBounds(0, 0, 1024, 728, false), bounds.FitWithin(0, 0, 1024, 728));
    }

    [Fact]
    public void AWindowThatOnlyOverhangsMovesWithoutShrinking()
    {
        var bounds = new WindowBounds(1500, 900, 800, 600, Maximized: false);

        Assert.Equal(new WindowBounds(1120, 440, 800, 600, false), bounds.FitWithin(0, 0, 1920, 1040));
    }

    [Fact]
    public void FittingWorksOnAMonitorLeftOfThePrimary()
    {
        var bounds = new WindowBounds(-3000, 100, 1180, 900, Maximized: true);

        Assert.Equal(new WindowBounds(-1920, 100, 1180, 900, true), bounds.FitWithin(-1920, 40, 1920, 1000));
    }

    [Fact]
    public void MaximizedDefaultsToFalse()
    {
        var read = WindowBounds.Parse("""{"left":1,"top":2,"width":300,"height":200}""");

        Assert.NotNull(read);
        Assert.False(read.Maximized);
    }
}
