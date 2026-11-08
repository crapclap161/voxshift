using VoxShift.Core.Config;
using VoxShift.Core.Soundboard;

namespace VoxShift.Core.Tests;

public class SoundboardLibraryTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"D:\Music\Soundboard" : "/music/soundboard";

    private static string P(string name) => Path.Combine(Root, name);

    private static SoundboardLibrary Create(AppSettings settings, params string[] existing)
    {
        var files = new HashSet<string>(existing.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        return new SoundboardLibrary(settings, files.Contains, _ => false, _ => Array.Empty<string>());
    }

    [Fact]
    public void ImportWithoutCollectionCreatesDefaultSoundboardAutomatically()
    {
        var settings = new AppSettings();
        var lib = Create(settings, P("airhorn.mp3"));

        var result = lib.Import([P("airhorn.mp3")]);

        Assert.True(result.CreatedCollection);
        var collection = Assert.Single(settings.Soundboards);
        Assert.Equal("Soundboard", collection.Name);
        Assert.Equal(collection.Id, settings.SelectedSoundboardId);
        var sound = Assert.Single(collection.Sounds);
        Assert.Equal("airhorn", sound.Name);
        Assert.Equal(Path.GetFullPath(P("airhorn.mp3")), sound.FilePath);
    }

    [Fact]
    public void ImportsMultipleFilesIntoSelectedCollection()
    {
        var settings = new AppSettings();
        var lib = Create(settings, P("a.wav"), P("b.mp3"), P("c.flac"));
        var second = lib.CreateCollection("Memes");

        var result = lib.Import([P("a.wav"), P("b.mp3"), P("c.flac")]);

        Assert.False(result.CreatedCollection);
        Assert.Equal(3, result.Added.Count);
        Assert.Same(second, result.Collection);
        Assert.Equal(["a", "b", "c"], second.Sounds.Select(s => s.Name));
    }

    [Fact]
    public void DuplicatesInvalidAndMissingFilesAreSkippedWithReasons()
    {
        var settings = new AppSettings();
        var lib = Create(settings, P("a.wav"), P("notes.txt"));
        lib.Import([P("a.wav")]);

        var result = lib.Import([P("a.wav"), P("A.WAV"), P("notes.txt"), P("gone.mp3"), P("clip.ogg")]);

        Assert.Empty(result.Added);
        Assert.Equal(2, result.Duplicates.Count);
        Assert.Equal(2, result.Unsupported.Count);
        Assert.Single(result.Missing);
        Assert.Single(settings.Soundboards[0].Sounds);
        Assert.Contains("Skipped", result.Summary);
    }

    [Fact]
    public void PathsWithSpacesUnicodeAndSpecialCharactersAreStoredVerbatim()
    {
        string odd = P("Sound FX (2024) – Ünïcödé & ½ #1 ♪.mp3");
        var settings = new AppSettings();
        var lib = Create(settings, odd);

        lib.Import([odd]);

        Assert.Equal(Path.GetFullPath(odd), settings.Soundboards[0].Sounds[0].FilePath);
        Assert.Equal("Sound FX (2024) – Ünïcödé & ½ #1 ♪", settings.Soundboards[0].Sounds[0].Name);
    }

    [Fact]
    public void DroppedFoldersAreExpandedToSupportedFiles()
    {
        string folder = P("pack");
        var settings = new AppSettings();
        var files = new[] { Path.Combine(folder, "b.mp3"), Path.Combine(folder, "a.wav"), Path.Combine(folder, "readme.txt") };
        var lib = new SoundboardLibrary(settings, f => files.Contains(f), d => d == folder, _ => files);

        var result = lib.Import([folder]);

        Assert.Equal(["a", "b"], result.Added.Select(a => a.Name));
    }

    [Fact]
    public void CollectionsCanBeCreatedRenamedAndDeleted()
    {
        var settings = new AppSettings();
        var lib = Create(settings);
        var first = lib.CreateCollection();
        var second = lib.CreateCollection();
        Assert.Equal("Soundboard", first.Name);
        Assert.Equal("Soundboard 2", second.Name);
        Assert.Same(second, lib.Selected);

        Assert.True(lib.RenameCollection(second.Id, "  Memes  "));
        Assert.Equal("Memes", second.Name);
        Assert.True(lib.RenameCollection(second.Id, "soundboard"));
        Assert.Equal("soundboard 2", second.Name); // names stay unique
        Assert.False(lib.RenameCollection(second.Id, "   "));

        Assert.True(lib.DeleteCollection(second.Id));
        Assert.Same(first, lib.Selected);
        Assert.True(lib.DeleteCollection(first.Id));
        Assert.Null(lib.Selected);
        Assert.Equal(string.Empty, settings.SelectedSoundboardId);
        Assert.False(lib.DeleteCollection("nope"));
    }

    [Fact]
    public void SoundsCanBeRenamedRemovedAndRelinked()
    {
        var settings = new AppSettings();
        var lib = Create(settings, P("a.wav"), P("b.wav"), P("moved/a.wav"));
        var result = lib.Import([P("a.wav"), P("b.wav")]);
        string cid = result.Collection!.Id;
        var a = result.Added[0];

        Assert.True(lib.RenameSound(cid, a.Id, "Airhorn"));
        Assert.Equal(RelinkResult.Ok, lib.Relink(cid, a.Id, P("moved/a.wav")));
        Assert.Equal("Airhorn", a.Name);
        Assert.Equal(Path.GetFullPath(P("moved/a.wav")), a.FilePath);
        Assert.Equal(RelinkResult.Duplicate, lib.Relink(cid, a.Id, P("b.wav")));
        Assert.Equal(RelinkResult.Unsupported, lib.Relink(cid, a.Id, P("x.txt")));
        Assert.Equal(RelinkResult.Missing, lib.Relink(cid, a.Id, P("missing.wav")));

        Assert.True(lib.RemoveSound(cid, a.Id));
        Assert.Single(result.Collection.Sounds);
    }

    [Fact]
    public void MissingFilesAreDetectedWithoutBreakingTheLibrary()
    {
        var settings = new AppSettings
        {
            Soundboards = [new SoundboardSet { Name = "Old", Sounds = [new SoundEntry { Name = "gone", FilePath = P("gone.wav") }] }],
        };
        var lib = Create(settings);
        Assert.False(lib.FileExists(settings.Soundboards[0].Sounds[0]));
        Assert.Same(settings.Soundboards[0], lib.Selected);
    }

    [Theory]
    [InlineData("a.WAV", true)]
    [InlineData("a.mp3", true)]
    [InlineData("a.flac", true)]
    [InlineData("a.m4a", true)]
    [InlineData("a.ogg", false)]
    [InlineData("a.exe", false)]
    [InlineData("noext", false)]
    public void RecognisesSupportedFormats(string file, bool supported) =>
        Assert.Equal(supported, SoundboardLibrary.IsSupportedFile(file));
}
