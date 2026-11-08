using System.Runtime.InteropServices;

namespace VoxShift.Audio.Interop;

/// <summary>
/// Activates an <see cref="IAudioClient"/> that captures only the audio rendered by one process
/// (and optionally its child processes) via <c>ActivateAudioInterfaceAsync</c>.
/// </summary>
internal static class ProcessLoopbackActivator
{
    /// <summary>
    /// Synchronously activates a process loopback audio client. Must be called from an MTA thread.
    /// </summary>
    /// <exception cref="AudioCaptureException">Activation failed.</exception>
    public static IAudioClient Activate(int processId, bool includeProcessTree, TimeSpan timeout)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = includeProcessTree ? ProcessLoopbackMode.IncludeTargetProcessTree : ProcessLoopbackMode.ExcludeTargetProcessTree,
        };

        IntPtr paramsPtr = IntPtr.Zero;
        IntPtr propVariantPtr = IntPtr.Zero;
        try
        {
            paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var propVariant = new BlobPropVariant
            {
                Vt = BlobPropVariant.VT_BLOB,
                BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                BlobData = paramsPtr,
            };
            propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var handler = new ActivationCompletionHandler();
            int hr = NativeMethods.ActivateAudioInterfaceAsync(
                NativeMethods.VirtualAudioDeviceProcessLoopback,
                NativeMethods.IID_IAudioClient,
                propVariantPtr,
                handler,
                out var operation);
            if (hr != AudioHResults.S_OK)
            {
                throw new AudioCaptureException($"Could not start application capture: {AudioHResults.Describe(hr)}.", hr);
            }

            try
            {
                if (!handler.Completed.Wait(timeout))
                {
                    throw new AudioCaptureException("Windows did not finish activating application audio capture in time.");
                }

                if (handler.ActivateResult != AudioHResults.S_OK || handler.AudioClient is null)
                {
                    throw new AudioCaptureException(
                        $"Windows refused application audio capture: {AudioHResults.Describe(handler.ActivateResult)}.",
                        handler.ActivateResult);
                }

                return handler.AudioClient;
            }
            finally
            {
                Marshal.ReleaseComObject(operation);
            }
        }
        finally
        {
            // The activation parameters are only read during ActivateAudioInterfaceAsync / before completion.
            if (propVariantPtr != IntPtr.Zero) Marshal.FreeHGlobal(propVariantPtr);
            if (paramsPtr != IntPtr.Zero) Marshal.FreeHGlobal(paramsPtr);
        }
    }

    /// <summary>
    /// COM-visible completion callback. Implements <see cref="IAgileObject"/> because the audio
    /// service invokes it on an arbitrary worker thread.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Completed { get; } = new(false);

        public int ActivateResult { get; private set; } = AudioHResults.E_NOINTERFACE;

        public IAudioClient? AudioClient { get; private set; }

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                int hr = activateOperation.GetActivateResult(out int activateResult, out object? activated);
                ActivateResult = hr != AudioHResults.S_OK ? hr : activateResult;
                if (ActivateResult == AudioHResults.S_OK)
                {
                    AudioClient = activated as IAudioClient;
                    if (AudioClient is null) ActivateResult = AudioHResults.E_NOINTERFACE;
                }
            }
            catch (Exception ex)
            {
                ActivateResult = ex.HResult != 0 ? ex.HResult : AudioHResults.E_NOINTERFACE;
            }
            finally
            {
                Completed.Set();
            }

            return AudioHResults.S_OK;
        }
    }
}
