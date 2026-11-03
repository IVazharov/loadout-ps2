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
    public void MaximizedDefaultsToFalse()
    {
        var read = WindowBounds.Parse("""{"left":1,"top":2,"width":300,"height":200}""");

        Assert.NotNull(read);
        Assert.False(read.Maximized);
    }
}
