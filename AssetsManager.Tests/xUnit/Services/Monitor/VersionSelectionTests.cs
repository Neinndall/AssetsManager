using System.Linq;
using System.Windows.Input;
using AssetsManager.Views.Models.Versions;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor;

public sealed class VersionSelectionTests
{
    [Theory]
    [InlineData(false, 1, 3)]
    [InlineData(false, 3, 1)]
    [InlineData(true, 1, 3)]
    [InlineData(true, 3, 1)]
    public void ShiftSelectsTheVisibleRangeInBothClients(bool isGame, int anchor, int target)
    {
        var model = new ManageVersions(null, null);
        var files = Enumerable.Range(0, 5).Select(index => new VersionFileInfo { FileName = index.ToString() }).ToList();
        (isGame ? model.AllLoLGameClientVersions : model.AllLeagueClientVersions).AddRange(files);

        model.SelectVersion(files, files[anchor], isGame, ModifierKeys.None);
        model.SelectVersion(files, files[target], isGame, ModifierKeys.Shift);

        Assert.Equal(new[] { "1", "2", "3" }, files.Where(file => file.IsSelected).Select(file => file.FileName));
    }

    [Fact]
    public void CtrlShiftAddsARangeAndKeepsTheAnchor()
    {
        var model = new ManageVersions(null, null);
        var files = Enumerable.Range(0, 5).Select(index => new VersionFileInfo { FileName = index.ToString() }).ToList();
        model.AllLeagueClientVersions.AddRange(files);
        model.SelectVersion(files, files[0], false, ModifierKeys.None);
        model.SelectVersion(files, files[2], false, ModifierKeys.Control);
        model.SelectVersion(files, files[4], false, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal(new[] { "0", "2", "3", "4" }, files.Where(file => file.IsSelected).Select(file => file.FileName));

        model.SelectVersion(files, files[3], false, ModifierKeys.Shift);
        Assert.Equal(new[] { "2", "3" }, files.Where(file => file.IsSelected).Select(file => file.FileName));
    }

    [Fact]
    public void ShiftAfterChangingPageUsesTheNewPageAndClearsHiddenSelections()
    {
        var model = new ManageVersions(null, null);
        var files = Enumerable.Range(0, 6).Select(index => new VersionFileInfo { FileName = index.ToString() }).ToList();
        model.AllLeagueClientVersions.AddRange(files);
        var firstPage = files.Take(3).ToList();
        var secondPage = files.Skip(3).ToList();
        model.SelectVersion(firstPage, files[0], false, ModifierKeys.None);

        model.SelectVersion(secondPage, files[4], false, ModifierKeys.Shift);
        Assert.Equal(new[] { "4" }, files.Where(file => file.IsSelected).Select(file => file.FileName));
        model.SelectVersion(secondPage, files[5], false, ModifierKeys.Shift);
        Assert.Equal(new[] { "4", "5" }, files.Where(file => file.IsSelected).Select(file => file.FileName));
    }

    [Fact]
    public void EachClientKeepsItsOwnRangeAnchor()
    {
        var model = new ManageVersions(null, null);
        var client = Enumerable.Range(0, 4).Select(index => new VersionFileInfo { FileName = "client" + index }).ToList();
        var game = Enumerable.Range(0, 4).Select(index => new VersionFileInfo { FileName = "game" + index }).ToList();
        model.AllLeagueClientVersions.AddRange(client);
        model.AllLoLGameClientVersions.AddRange(game);
        model.SelectVersion(client, client[1], false, ModifierKeys.None);
        model.SelectVersion(game, game[0], true, ModifierKeys.Control);

        model.SelectVersion(client, client[3], false, ModifierKeys.Shift);
        Assert.Equal(new[] { "client1", "client2", "client3" }, client.Where(file => file.IsSelected).Select(file => file.FileName));
        Assert.DoesNotContain(game, file => file.IsSelected);
    }

    [Fact]
    public void PlainClickOnSelectedVersionStillDeselectsBothClients()
    {
        var model = new ManageVersions(null, null);
        var client = new VersionFileInfo();
        var game = new VersionFileInfo();
        model.AllLeagueClientVersions.Add(client);
        model.AllLoLGameClientVersions.Add(game);
        model.SelectVersion(model.AllLeagueClientVersions, client, false, ModifierKeys.None);
        model.SelectVersion(model.AllLoLGameClientVersions, game, true, ModifierKeys.Control);

        model.SelectVersion(model.AllLeagueClientVersions, client, false, ModifierKeys.None);

        Assert.False(client.IsSelected);
        Assert.False(game.IsSelected);
    }

    [Fact]
    public void CtrlClickStillTogglesOneVersion()
    {
        var model = new ManageVersions(null, null);
        var files = new[] { new VersionFileInfo(), new VersionFileInfo() };
        model.AllLeagueClientVersions.AddRange(files);
        model.SelectVersion(files, files[0], false, ModifierKeys.None);
        model.SelectVersion(files, files[1], false, ModifierKeys.Control);
        Assert.All(files, file => Assert.True(file.IsSelected));

        model.SelectVersion(files, files[1], false, ModifierKeys.Control);
        Assert.True(files[0].IsSelected);
        Assert.False(files[1].IsSelected);
    }
}
