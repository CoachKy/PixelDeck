using System.Runtime.InteropServices;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Managed P/Invoke wrapper over Dolphin's native Vulkan renderer bridge (<c>pixelcube_gx.dll</c>).
/// </summary>
public static unsafe class PdGxNative
{
    private const string LibraryName = "pixelcube_gx";

    public static bool IsAvailable { get; }
    public static string UnavailableReason { get; } = string.Empty;

    static PdGxNative()
    {
        try
        {
            if (NativeLibrary.TryLoad(LibraryName, typeof(PdGxNative).Assembly, null, out var handle))
            {
                IsAvailable = true;
                UnavailableReason = string.Empty;
            }
            else
            {
                IsAvailable = false;
                UnavailableReason = "pixelcube_gx.dll not found in native binary path.";
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = $"Native loader exception: {ex.Message}";
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pdgx_init(void* mainRam, uint ramSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pdgx_shutdown();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pdgx_device_name();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pdgx_process_fifo(uint startOffset, uint endOffset);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pdgx_set_vi_register(uint offset, uint value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pdgx_flush();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint pdgx_scanout_rgba(byte* outBuffer, uint outBytes, uint* width, uint* height);

    public static string GetDeviceName()
    {
        if (!IsAvailable) return "Software GX Rasterizer";
        var ptr = pdgx_device_name();
        return ptr == IntPtr.Zero ? "Vulkan GX Renderer" : Marshal.PtrToStringAnsi(ptr) ?? "Vulkan GX Renderer";
    }
}
