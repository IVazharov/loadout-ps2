using System.Net;
using Loadout.Images;
using Loadout.Persistence;
using Loadout.Library;

namespace Loadout.Core;

public class GameListSortTests
{
    [Fact]
    public void DefaultSortIsTitleAToZ()
    {
        var sorted = GameListSort.ByTitle(["Zelda", "ape", "Mario"], title => title);
        Assert.Equal(["ape", "Mario", "Zelda"], sorted);
    }

    [Fact]
    public void InstalledGamesDefaultToTitleAToZ()
    {
        InstalledGameRow[] rows =
        [
            Row("Zelda"),
            Row("ape"),
            Row("Mario"),
        ];

        var ordered = GameListSort.ByTitle(rows, game => game.Title);

        Assert.Equal(["ape", "Mario", "Zelda"], ordered.Select(game => game.Title).ToArray());
    }

    private static InstalledGameRow Row(string title)
        => new("PP." + title, title, "SLUS-20011", 0, 0x40, 4, 0x14, 10);
}
