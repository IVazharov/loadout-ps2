using System.Text;

namespace Loadout.Core;

public class TitleTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyInputStaysEmpty(string? text)
    {
        Assert.Equal(string.Empty, TitleText.FitFullTitle(text));
    }

    [Fact]
    public void FullTitleKeepsEightyCharactersAndDropsTheRest()
    {
        var text = new string('A', 81);
        var fitted = TitleText.FitFullTitle(text);
        Assert.Equal(80, fitted.Length);
        Assert.Equal(new string('A', 80), fitted);
    }

    [Fact]
    public void FitKeepsTheCharacterLimit()
    {
        var fitted = TitleText.Fit(new string('B', 20), maxCharacters: 16, maxUtf8Bytes: 64);
        Assert.Equal(16, fitted.Length);
    }

    [Fact]
    public void HiraganaCountsAsOneCharacterAndIsNotSplit()
    {
        var sixteen = string.Concat(Enumerable.Repeat("あ", 16));
        var seventeen = sixteen + "あ";
        var fitted = TitleText.Fit(seventeen, maxCharacters: 16, maxUtf8Bytes: 64);
        Assert.Equal(sixteen, fitted);
        Assert.Equal(48, Encoding.UTF8.GetByteCount(fitted));
    }

    [Fact]
    public void FourByteCharacterIsDroppedWhenTheByteBudgetCannotHoldIt()
    {
        const string emoji = "😀";
        Assert.Equal(4, Encoding.UTF8.GetByteCount(emoji));
        Assert.Equal(emoji, TitleText.Fit(emoji, maxCharacters: 1, maxUtf8Bytes: 4));
        Assert.Equal(string.Empty, TitleText.Fit(emoji, maxCharacters: 1, maxUtf8Bytes: 3));
        Assert.Equal("A", TitleText.Fit("A😀", maxCharacters: 16, maxUtf8Bytes: 4));
    }

    [Fact]
    public void ByteBudgetStopsBeforeACharacterThatWouldOverflow()
    {
        var fitted = TitleText.Fit("あああ", maxCharacters: 16, maxUtf8Bytes: 4);
        Assert.Equal("あ", fitted);
        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= 4);
    }

    [Fact]
    public void LoneSurrogateIsNotEmitted()
    {
        var text = "AB\uD800C";
        Assert.Equal("AB", TitleText.FitFullTitle(text));
    }

    [Fact]
    public void ZeroBudgetReturnsEmpty()
    {
        Assert.Equal(string.Empty, TitleText.Fit("Title", maxCharacters: 0, maxUtf8Bytes: 160));
        Assert.Equal(string.Empty, TitleText.Fit("Title", maxCharacters: 80, maxUtf8Bytes: 0));
    }

    [Fact]
    public void NegativeBudgetIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TitleText.Fit("Title", -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => TitleText.Fit("Title", 10, -1));
    }

    [Fact]
    public void QueueTitleIsCappedAtEightyCharacters()
    {
        Assert.Equal("Hello World Extra", TitleText.FitQueueTitle("Hello World Extra", "SLES-53886"));
        Assert.Equal(new string('A', 80), TitleText.FitQueueTitle(new string('A', 90), null));
    }

    [Fact]
    public void QueueTitleDoesNotSplitAMultibyteCharacter()
    {
        var forty = string.Concat(Enumerable.Repeat("😀", 40));

        Assert.Equal(forty, TitleText.FitQueueTitle(forty + "😀", null));
    }

    [Fact]
    public void BlankQueueTitleUsesTheFallbackThenUnknown()
    {
        Assert.Equal("SLES-53886", TitleText.FitQueueTitle("   ", "SLES-53886"));
        Assert.Equal("Unknown", TitleText.FitQueueTitle("   ", null));
    }
}
