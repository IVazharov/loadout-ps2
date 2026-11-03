namespace Loadout.Core;

public class TableLayoutTests
{
    [Fact]
    public void ALayoutSurvivesItsJson()
    {
        var layout = new TableLayout(["title", "art"], new Dictionary<string, double> { ["title"] = 340.5 });

        var read = TableLayout.Parse(layout.ToJson());

        Assert.NotNull(read);
        Assert.Equal(["title", "art"], read.Order);
        Assert.Equal(340.5, read.Widths["title"]);
    }

    [Fact]
    public void TheJsonUsesTheDocumentedNames()
    {
        var json = new TableLayout(["art"], new Dictionary<string, double> { ["art"] = 56 }).ToJson();

        Assert.Equal("""{"order":["art"],"widths":{"art":56}}""", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void TextThatIsNotALayoutGivesNull(string? json) => Assert.Null(TableLayout.Parse(json));

    [Fact]
    public void WidthsThatAreNotPositiveAreDropped()
    {
        var read = TableLayout.Parse("""{"order":[],"widths":{"a":0,"b":-4,"c":90}}""");

        Assert.NotNull(read);
        Assert.Equal(["c"], read.Widths.Keys);
    }

    [Fact]
    public void ArrangeDropsUnknownIdsAndKeepsNewOnesInTheirDefaultPlace()
    {
        var layout = new TableLayout(["size", "gone", "title", "size"], new Dictionary<string, double>());

        var order = layout.Arrange(["art", "title", "modes", "size"]);

        Assert.Equal(["size", "title", "art", "modes"], order);
    }
}
