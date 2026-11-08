using VoxShift.Core.Audio;
using VoxShift.Core.Devices;
using VoxShift.Core.Input;

namespace VoxShift.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+M", HotkeyModifiers.Control | HotkeyModifiers.Alt, "M")]
    [InlineData("shift + f9", HotkeyModifiers.Shift, "f9")]
    [InlineData("Ctrl+1", HotkeyModifiers.Control, "D1")]
    [InlineData("Win+Ctrl+Space", HotkeyModifiers.Windows | HotkeyModifiers.Control, "Space")]
    public void ParsesValidGestures(string text, HotkeyModifiers mods, string key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M")]           // no modifier
    [InlineData("Ctrl+Alt")]    // no key
    [InlineData("Ctrl+A+B")]    // two keys
    public void RejectsInvalidGestures(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void FormatsCanonically()
    {
        Assert.Equal("Ctrl+Alt+1", new HotkeyGesture(HotkeyModifiers.Alt | HotkeyModifiers.Control, "D1").ToString());
    }
}

public class VirtualCableDetectorTests
{
    [Fact]
    public void PrefersVbCable()
    {
        var devices = new[]
        {
            new AudioDeviceInfo("1", "Speakers (Realtek(R) Audio)"),
            new AudioDeviceInfo("2", "CABLE-A Input (VB-Audio Cable A)"),
            new AudioDeviceInfo("3", "CABLE Input (VB-Audio Virtual Cable)"),
        };
        Assert.Equal("3", VirtualCableDetector.FindPreferred(devices)!.Id);
    }

    [Fact]
    public void ReturnsNullWhenNotInstalled()
    {
        Assert.Null(VirtualCableDetector.FindPreferred(new[] { new AudioDeviceInfo("1", "Headphones (USB)") }));
    }

    [Theory]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", true)]
    [InlineData("Microphone (Blue Yeti)", false)]
    [InlineData("Headset Microphone (Arctis 7)", false)]
    public void RecognisesCableCaptureEndpoints(string name, bool expected) =>
        Assert.Equal(expected, VirtualCableDetector.IsVirtualCableCapture(name));

    [Fact]
    public void GivesVrchatHint() =>
        Assert.Equal("CABLE Output (VB-Audio Virtual Cable)", VirtualCableDetector.GetVrchatInputHint("CABLE Input (VB-Audio Virtual Cable)"));
}

public class VolumeMathTests
{
    [Theory]
    [InlineData(0, 0f)]
    [InlineData(100, 1f)]
    [InlineData(200, 2f)]
    [InlineData(500, 2f)]
    [InlineData(-10, 0f)]
    public void PercentToGain(double percent, float gain) => Assert.Equal(gain, VolumeMath.PercentToGain(percent), 5);

    [Fact]
    public void MeterPositionUsesDecibelScale()
    {
        Assert.Equal(1.0, VolumeMath.ToMeterPosition(1.0), 5);
        Assert.Equal(0.5, VolumeMath.ToMeterPosition(Math.Pow(10, -30 / 20.0)), 3);
        Assert.Equal(0.0, VolumeMath.ToMeterPosition(0));
    }
}
