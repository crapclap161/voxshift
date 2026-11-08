using System.Security.Cryptography;
using VoxShift.Core.Audio;
using VoxShift.Core.Config;
using VoxShift.Core.Soundboard;

namespace VoxShift.Core.Tests;

/// <summary>Real files on disk: import → decode → resample → router output, original file untouched.</summary>
public sealed class SoundboardFilePipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voxshift pipeline ✓ " + Guid.NewGuid().ToString("N")[..8]);

    public SoundboardFilePipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void WriteWav(string path, int rate, float amplitude, double seconds)
    {
        int frames = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + frames * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(frames * 2);
        for (int i = 0; i < frames; i++) w.Write((short)(amplitude * 32767 * Math.Sin(2 * Math.PI * 300 * i / rate)));
    }

    [Fact]
    public void ImportedFileIsPlayedInPlaceThroughTheRouterAndNeverModified()
    {
        string file = Path.Combine(_dir, "Air horn (loud) #2.wav");
        WriteWav(file, 44100, 0.5f, 0.5);
        byte[] before = SHA256.HashData(File.ReadAllBytes(file));
        var stamp = File.GetLastWriteTimeUtc(file);

        var settings = new AppSettings();
        var library = new SoundboardLibrary(settings);
        var result = library.Import([file]);
        var entry = Assert.Single(result.Added);
        Assert.Equal(Path.GetFullPath(file), entry.FilePath);
        Assert.Single(Directory.GetFiles(_dir)); // nothing copied next to it

        var clip = WavDecoder.TryDecode(File.ReadAllBytes(entry.FilePath))!.ResampleTo(48000);
        var router = new AudioRouter(48000, AudioSource.Microphone);
        router.Soundboard.Volume = 1f;
        router.Soundboard.Play(entry.Id, clip);
        float peak = 0;
        for (int i = 0; i < 20; i++)
        {
            var o = new float[960];
            router.Render(o, 480);
            peak = Math.Max(peak, TestSignals.Peak(o));
        }

        Assert.InRange(peak, 0.45f, 0.55f);

        // Removing the entry and deleting the whole collection never touches the file.
        library.RemoveSound(result.Collection!.Id, entry.Id);
        library.DeleteCollection(result.Collection.Id);
        Assert.True(File.Exists(file));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(file)));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
    }
}
