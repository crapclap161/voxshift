using System.Runtime.InteropServices;

namespace VoxShift.Audio.Interop;

// Hand-written COM interop for the parts of WASAPI that NAudio does not expose for *process loopback*
// capture. Process loopback (Windows 10 build 20348+ / Windows 11) is obtained through
// ActivateAudioInterfaceAsync with the virtual device "VAD\Process_Loopback" and an
// AUDIOCLIENT_ACTIVATION_PARAMS blob, rather than through an IMMDevice. The resulting IAudioClient
// must be initialised with an explicit format because GetMixFormat is not implemented for it.
//
// All methods use PreserveSig so that HRESULTs can be mapped to meaningful error messages.

internal static class AudioHResults
{
    public const int S_OK = 0;
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
    public const int AUDCLNT_E_BUFFER_ERROR = unchecked((int)0x88890018);

    public static string Describe(int hr) => hr switch
    {
        S_OK => "OK",
        E_ACCESSDENIED => "access denied (E_ACCESSDENIED)",
        E_INVALIDARG => "invalid argument (E_INVALIDARG) – process loopback capture requires Windows 10 21H2 / Windows 11 or newer",
        E_NOTIMPL => "not implemented by this version of Windows (E_NOTIMPL)",
        E_NOINTERFACE => "interface not supported (E_NOINTERFACE)",
        AUDCLNT_E_UNSUPPORTED_FORMAT => "audio format not supported (AUDCLNT_E_UNSUPPORTED_FORMAT)",
        AUDCLNT_E_DEVICE_INVALIDATED => "audio device was removed or reconfigured (AUDCLNT_E_DEVICE_INVALIDATED)",
        AUDCLNT_E_SERVICE_NOT_RUNNING => "the Windows Audio service is not running (AUDCLNT_E_SERVICE_NOT_RUNNING)",
        _ => $"HRESULT 0x{hr:X8}",
    };
}

[Flags]
internal enum AudioClientStreamFlags : uint
{
    None = 0,
    Loopback = 0x00020000,             // AUDCLNT_STREAMFLAGS_LOOPBACK
    EventCallback = 0x00040000,        // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
    NoPersist = 0x00080000,            // AUDCLNT_STREAMFLAGS_NOPERSIST
    SrcDefaultQuality = 0x08000000,    // AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY
    AutoConvertPcm = 0x80000000,       // AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
}

[Flags]
internal enum AudioClientBufferFlags : int
{
    None = 0,
    DataDiscontinuity = 0x1,
    Silent = 0x2,
    TimestampError = 0x4,
}

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

internal enum ProcessLoopbackMode
{
    IncludeTargetProcessTree = 0,
    ExcludeTargetProcessTree = 1,
}

/// <summary>AUDIOCLIENT_ACTIVATION_PARAMS with the AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS union member.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

/// <summary>A PROPVARIANT holding a VT_BLOB (layout valid for x64: vt@0, cbSize@8, pBlobData@16).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BlobPropVariant
{
    public const ushort VT_BLOB = 65;

    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public uint BlobSize;
    public IntPtr BlobData;
}

/// <summary>WAVEFORMATEX (packed, 18 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public const ushort WAVE_FORMAT_PCM = 1;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;

    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;

    public static WaveFormatEx Create(ushort tag, int sampleRate, int channels, int bits)
    {
        ushort blockAlign = (ushort)(channels * bits / 8);
        return new WaveFormatEx
        {
            FormatTag = tag,
            Channels = (ushort)channels,
            SamplesPerSec = (uint)sampleRate,
            AvgBytesPerSec = (uint)(sampleRate * blockAlign),
            BlockAlign = blockAlign,
            BitsPerSample = (ushort)bits,
            Size = 0,
        };
    }
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, AudioClientStreamFlags streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint numFramesToRead, out AudioClientBufferFlags flags, out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint numFramesInNextPacket);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>
/// Marker interface: ActivateAudioInterfaceAsync requires the completion handler to be agile
/// (callable from any apartment), otherwise activation fails with E_ILLEGAL_METHOD_CALL.
/// </summary>
[ComImport]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

internal static class NativeMethods
{
    /// <summary>VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK.</summary>
    public const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public const int AUDCLNT_SHAREMODE_SHARED = 0;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
}
