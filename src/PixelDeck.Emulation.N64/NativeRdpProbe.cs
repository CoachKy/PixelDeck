using System.Diagnostics;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Decides whether parallel-rdp can safely be brought up *in this process*, by
/// first bringing it up in a throwaway one.
/// </summary>
/// <remarks>
/// Vulkan instance and device creation happens inside the native library. On a
/// host with no usable device it has been observed to block forever, and the
/// block is not survivable in-process: a managed timeout around the call does
/// not help, because the stalled native initialiser holds the OS loader lock
/// and the thread waiting on the timeout cannot make progress either. The
/// affected process could not be terminated by Stop-Process or taskkill /F.
///
/// A timeout can only be enforced from outside, so the probe runs somewhere
/// expendable. If the helper exits cleanly the device is good and the caller
/// may load the library for real; if it hangs, it is abandoned and the
/// emulator carries on with the software renderer having never touched the DLL.
/// </remarks>
public static class NativeRdpProbe
{
    /// <summary>Argument that puts a host executable into probe mode.</summary>
    public const string ProbeArgument = "--probe-parallel-rdp";

    private const int DeviceUsableExitCode = 0;
    private const int DeviceUnusableExitCode = 3;

    /// <summary>
    /// Runs <paramref name="probeExecutablePath"/> with <see cref="ProbeArgument"/>
    /// and reports whether it came back saying the device is usable. Any other
    /// outcome -- timeout, crash, missing helper -- is reported as unusable,
    /// because the whole point is to be wrong in the safe direction.
    /// </summary>
    public static bool IsDeviceUsable(
        string probeExecutablePath,
        TimeSpan timeout,
        out string reason)
    {
        // Process.Start does not resolve a relative path against the current
        // directory the way File.Exists does, so settle on one absolute path
        // and use it for both.
        var hostPath = Path.GetFullPath(probeExecutablePath);
        if (!File.Exists(hostPath))
        {
            reason = $"Probe host '{hostPath}' was not found.";
            return false;
        }

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(hostPath)
            {
                Arguments = ProbeArgument,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                reason = "Probe host could not be started.";
                return false;
            }

            if (!process.WaitForExit(timeout))
            {
                reason =
                    $"parallel-rdp did not initialise within {timeout.TotalSeconds:0.#}s; " +
                    "the graphics device is not usable from this host.";
                TryAbandon(process);
                return false;
            }

            if (process.ExitCode == DeviceUsableExitCode)
            {
                reason = string.Empty;
                return true;
            }

            reason = process.ExitCode == DeviceUnusableExitCode
                ? "parallel-rdp reported no usable Vulkan device."
                : $"Probe host exited with code {process.ExitCode}.";
            return false;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            reason = $"{error.GetType().Name}: {error.Message}";
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// The probe side. Call this from a host that was started with
    /// <see cref="ProbeArgument"/> and exit with the value it returns; never
    /// call it in a process that must keep running afterwards.
    /// </summary>
    public static int RunProbe()
    {
        var rdram = new byte[N64Memory.RdramSize];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            rdram,
            System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return PdRdpNative.TryInitialize(handle.AddrOfPinnedObject(), (uint)rdram.Length)
                ? DeviceUsableExitCode
                : DeviceUnusableExitCode;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine(error.Message);
            return DeviceUnusableExitCode;
        }
        finally
        {
            handle.Free();
        }
    }

    private static void TryAbandon(Process process)
    {
        // A process wedged inside the loader may refuse to die. Nothing more
        // can be done about that from here, and it must not stop the emulator
        // from running, so the failure is swallowed deliberately.
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
        }
    }
}
