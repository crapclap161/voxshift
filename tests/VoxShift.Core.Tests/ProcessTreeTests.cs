using VoxShift.Core.Apps;

namespace VoxShift.Core.Tests;

public class ProcessTreeTests
{
    private static ProcessTree Browser() => new(new[]
    {
        new ProcessEntry(4, 0, "System"),
        new ProcessEntry(100, 4, "explorer.exe"),
        new ProcessEntry(200, 100, "chrome.exe"),          // browser main process
        new ProcessEntry(201, 200, "chrome.exe"),          // renderer
        new ProcessEntry(202, 200, "chrome.exe"),          // audio service utility process
        new ProcessEntry(203, 202, "chrome.exe"),
        new ProcessEntry(300, 100, "Spotify.exe"),
        new ProcessEntry(301, 300, "Spotify.exe"),
        new ProcessEntry(400, 100, "launcher.exe"),
        new ProcessEntry(401, 400, "game.exe"),
    });

    [Fact]
    public void HelperProcessesResolveToBrowserRoot()
    {
        var tree = Browser();
        Assert.Equal(200, tree.GetApplicationRoot(202));
        Assert.Equal(200, tree.GetApplicationRoot(203));
        Assert.Equal(200, tree.GetApplicationRoot(200));
    }

    [Fact]
    public void DifferentExecutableStopsTheWalk()
    {
        var tree = Browser();
        Assert.Equal(401, tree.GetApplicationRoot(401));
    }

    [Fact]
    public void GroupsCandidatesPerApplication()
    {
        var groups = Browser().GroupByApplication(new[] { 202, 201, 301, 401 });
        Assert.Equal(3, groups.Count);
        Assert.Equal(new[] { 202, 201 }, groups[200]);
        Assert.Equal(new[] { 301 }, groups[300]);
    }

    [Fact]
    public void FindsRootByExecutableNameCaseInsensitive()
    {
        var tree = Browser();
        Assert.Equal(300, tree.FindRootByExeName("spotify"));
        Assert.Equal(300, tree.FindRootByExeName("SPOTIFY.EXE"));
        Assert.Null(tree.FindRootByExeName("vlc"));
    }

    [Fact]
    public void GetTreeIncludesAllDescendants()
    {
        var set = Browser().GetTree(200);
        Assert.Equal(new HashSet<int> { 200, 201, 202, 203 }, set);
    }

    [Fact]
    public void CyclicParentLinksDoNotHang()
    {
        var tree = new ProcessTree(new[]
        {
            new ProcessEntry(1, 2, "a.exe"),
            new ProcessEntry(2, 1, "a.exe"),
        });
        int root = tree.GetApplicationRoot(1);
        Assert.Contains(root, new[] { 1, 2 });
    }
}
