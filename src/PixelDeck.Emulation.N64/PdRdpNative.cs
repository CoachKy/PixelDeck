using System.Runtime.InteropServices;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Interop with <c>pixeldeck_rdp</c>, the headless C shim over Themaister's
/// parallel-rdp. The native library is optional: when it is missing or no
/// Vulkan device is usable, <see cref="IsAvailable"/> stays false and the
/// emulator carries on with the software renderer.
/// </summary>
/// <remarks>
/// parallel-rdp is MIT licensed. Build the shim with
/// <c>native/pixeldeck_rdp/build.bat</c>.
/// </remarks>
internal static partial class PdRdpNative
{
    private const string Library = "pixeldeck_rdp";

    [LibraryImport(Library)]
    private static partial int pdrdp_init(IntPtr rdram, uint rdramSize);

    [LibraryImport(Library)]
    private static partial void pdrdp_shutdown();

    [LibraryImport(Library)]
    private static partial IntPtr pdrdp_device_name();

    [LibraryImport(Library)]
    private static partial void pdrdp_enqueue_command(IntPtr words, uint wordCount);

    [LibraryImport(Library)]
    private static partial void pdrdp_set_vi_register(uint index, uint value);

    [LibraryImport(Library)]
    private static partial void pdrdp_flush();

    [LibraryImport(Library)]
    private static partial uint pdrdp_scanout_rgba(
        IntPtr destination, uint destinationBytes, out uint width, out uint height);

    /// <summary>
    /// True once a Vulkan device has been created and the RDP is bound to
    /// RDRAM.
    /// </summary>
    public static bool IsAvailable { get; private set; }

    public static string DeviceName { get; private set; } = string.Empty;

    /// <summary>
    /// Reason the native path is unavailable, for diagnostics. Empty when it
    /// initialised, or when initialisation has not been attempted.
    /// </summary>
    public static string UnavailableReason { get; private set; } = string.Empty;

    /// <summary>
    /// Binds the RDP to <paramref name="pinnedRdram"/>. The pointer must stay
    /// valid and fixed for the whole session -- the GPU reads that memory
    /// directly rather than a copy of it.
    /// </summary>
    public static bool TryInitialize(IntPtr pinnedRdram, uint rdramSize)
    {
        if (IsAvailable)
        {
            return true;
        }

        try
        {
            if (pdrdp_init(pinnedRdram, rdramSize) == 0)
            {
                UnavailableReason = "parallel-rdp found no usable Vulkan device.";
                return false;
            }

            DeviceName = Marshal.PtrToStringAnsi(pdrdp_device_name()) ?? string.Empty;
            IsAvailable = true;
            UnavailableReason = string.Empty;
            return true;
        }
        catch (DllNotFoundException)
        {
            UnavailableReason =
                "pixeldeck_rdp native library not found; build native/pixeldeck_rdp.";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            UnavailableReason = $"pixeldeck_rdp is out of date: {ex.Message}";
            return false;
        }
    }

    public static void Shutdown()
    {
        if (!IsAvailable)
        {
            return;
        }

        pdrdp_shutdown();
        IsAvailable = false;
        DeviceName = string.Empty;
    }

    public static void EnqueueCommand(ReadOnlySpan<uint> words)
    {
        if (!IsAvailable || words.IsEmpty)
        {
            return;
        }

        unsafe
        {
            fixed (uint* pointer = words)
            {
                pdrdp_enqueue_command((IntPtr)pointer, (uint)words.Length);
            }
        }
    }

    public static void SetViRegister(PdRdpViRegister register, uint value)
    {
        if (IsAvailable)
        {
            pdrdp_set_vi_register((uint)register, value);
        }
    }

    public static void Flush()
    {
        if (IsAvailable)
        {
            pdrdp_flush();
        }
    }

    /// <summary>
    /// Scans the current frame out as RGBA8. Returns false when the RDP has
    /// nothing to present yet.
    /// </summary>
    public static bool TryScanout(Span<byte> destination, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!IsAvailable || destination.IsEmpty)
        {
            return false;
        }

        unsafe
        {
            fixed (byte* pointer = destination)
            {
                var written = pdrdp_scanout_rgba(
                    (IntPtr)pointer, (uint)destination.Length, out var w, out var h);
                width = (int)w;
                height = (int)h;
                return written != 0;
            }
        }
    }
}

/// <summary>
/// Mirrors <c>RDP::VIRegister</c>; the order is part of the native ABI.
/// </summary>
internal enum PdRdpViRegister
{
    Control = 0,
    Origin = 1,
    Width = 2,
    Interrupt = 3,
    CurrentLine = 4,
    Timing = 5,
    VerticalSync = 6,
    HorizontalSync = 7,
    Leap = 8,
    HorizontalStart = 9,
    VerticalStart = 10,
    VerticalBurst = 11,
    XScale = 12,
    YScale = 13
}
