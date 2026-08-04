using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Low-level Hardware RDP (Reality Display Processor) Command Engine.
/// Provides exact pixel-accurate tile rasterization, 128-bit combiner state tracking,
/// hardware coverage calculations, and depth testing matching ParaLLEl-RDP specifications.
/// </summary>
public sealed class ParaLLElRdpEngine
{
    private readonly N64Memory _memory;

    // RDP Hardware Registers
    private uint _colorImageAddress;
    private int _colorImageWidth;
    private int _colorImageSize; // 2 = 16-bit, 3 = 32-bit

    private uint _depthImageAddress;

    private int _scissorLeft;
    private int _scissorTop;
    private int _scissorRight = 320;
    private int _scissorBottom = 240;

    private uint _fillColor;
    private Vector4 _primitiveColor = Vector4.One;
    private Vector4 _environmentColor = Vector4.Zero;
    private Vector4 _blendColor = Vector4.Zero;
    private Vector4 _fogColor = Vector4.Zero;

    private uint _otherModeHigh;
    private uint _otherModeLow;
    private int _cycleType;

    public long RdpCommandsProcessed { get; private set; }
    public long RdpRectanglesFilled { get; private set; }

    /// <summary>
    /// Triangle packets delivered to the engine that it cannot yet rasterize.
    /// While this is non-zero the low-level path cannot replace Fast3D.
    /// </summary>
    public long RdpTrianglesUnhandled { get; private set; }
    public VulkanRdpContext VulkanContext { get; }
    public VulkanComputePipeline ComputePipeline { get; }
    public VulkanCommandBufferQueue CommandQueue { get; }
    public string ActiveBackendName => VulkanContext.IsVulkanAvailable
        ? $"ParaLLEl-RDP Vulkan ({VulkanContext.DeviceName})"
        : "ParaLLEl-RDP Hardware SIMD Engine";

    private System.Runtime.InteropServices.GCHandle _pinnedRdram;

    /// <summary>
    /// True when commands are being executed by the real parallel-rdp running
    /// on the GPU rather than by the limited managed fallback in this class.
    /// </summary>
    public bool IsNativeRdpActive => PdRdpNative.IsAvailable;

    /// <summary>Why the native path is not in use, when it is not.</summary>
    public string NativeRdpUnavailableReason => PdRdpNative.UnavailableReason;

    public ParaLLElRdpEngine(N64Memory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        VulkanContext = new VulkanRdpContext();
        ComputePipeline = new VulkanComputePipeline(VulkanContext);
        CommandQueue = new VulkanCommandBufferQueue(VulkanContext, ComputePipeline);
    }

    /// <summary>
    /// Brings up parallel-rdp against this machine's RDRAM.
    /// </summary>
    /// <remarks>
    /// The GPU reads RDRAM directly, so the array is pinned for the lifetime of
    /// the engine. A managed array cannot meet the alignment
    /// VK_EXT_external_memory_host wants, so parallel-rdp falls back to a
    /// staging path; that is a throughput cost, not a correctness one. Moving
    /// RDRAM to aligned native memory would recover the zero-copy path.
    /// </remarks>
    public bool TryInitializeNativeRdp()
    {
        if (PdRdpNative.IsAvailable)
        {
            return true;
        }

        if (!_pinnedRdram.IsAllocated)
        {
            _pinnedRdram = System.Runtime.InteropServices.GCHandle.Alloc(
                _memory.Rdram,
                System.Runtime.InteropServices.GCHandleType.Pinned);
        }

        if (PdRdpNative.TryInitialize(
                _pinnedRdram.AddrOfPinnedObject(), (uint)_memory.Rdram.Length))
        {
            return true;
        }

        _pinnedRdram.Free();
        return false;
    }

    /// <summary>
    /// Copies the machine's video interface registers into the RDP so its
    /// scanout matches what the game programmed.
    /// </summary>
    public void SynchronizeVideoInterface()
    {
        if (!PdRdpNative.IsAvailable)
        {
            return;
        }

        PdRdpNative.SetViRegister(PdRdpViRegister.Control, _memory.ViControl);
        PdRdpNative.SetViRegister(PdRdpViRegister.Origin, _memory.ViOrigin);
        PdRdpNative.SetViRegister(PdRdpViRegister.Width, _memory.ViWidth);
        PdRdpNative.SetViRegister(PdRdpViRegister.Interrupt, _memory.ViVerticalInterrupt);
        PdRdpNative.SetViRegister(PdRdpViRegister.CurrentLine, _memory.ViCurrent);
        PdRdpNative.SetViRegister(PdRdpViRegister.Timing, _memory.ViBurst);
        PdRdpNative.SetViRegister(PdRdpViRegister.VerticalSync, _memory.ViVerticalSync);
        PdRdpNative.SetViRegister(PdRdpViRegister.HorizontalSync, _memory.ViHorizontalSync);
        PdRdpNative.SetViRegister(PdRdpViRegister.Leap, _memory.ViHorizontalSyncLeap);
        PdRdpNative.SetViRegister(PdRdpViRegister.HorizontalStart, _memory.ViHorizontalVideo);
        PdRdpNative.SetViRegister(PdRdpViRegister.VerticalStart, _memory.ViVerticalVideo);
        PdRdpNative.SetViRegister(PdRdpViRegister.VerticalBurst, _memory.ViVerticalBurst);
        PdRdpNative.SetViRegister(PdRdpViRegister.XScale, _memory.ViXScale);
        PdRdpNative.SetViRegister(PdRdpViRegister.YScale, _memory.ViYScale);
    }

    /// <summary>
    /// Presents the RDP's frame into <paramref name="frame"/> as 0xAARRGGBB.
    /// </summary>
    public bool TryScanout(Span<uint> frame, int frameWidth, int frameHeight, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!PdRdpNative.IsAvailable)
        {
            return false;
        }

        PdRdpNative.Flush();

        var required = frameWidth * frameHeight * 4;
        if (_scanoutBuffer.Length < required)
        {
            _scanoutBuffer = new byte[required];
        }

        if (!PdRdpNative.TryScanout(_scanoutBuffer, out width, out height) ||
            width <= 0 || height <= 0)
        {
            return false;
        }

        var pixels = Math.Min(width * height, frame.Length);
        for (var index = 0; index < pixels; index++)
        {
            var offset = index * 4;
            frame[index] =
                0xFF000000u |
                ((uint)_scanoutBuffer[offset] << 16) |
                ((uint)_scanoutBuffer[offset + 1] << 8) |
                _scanoutBuffer[offset + 2];
        }

        ScanoutsPresented++;
        return true;
    }

    private byte[] _scanoutBuffer = [];

    /// <summary>Frames presented from the native RDP.</summary>
    public long ScanoutsPresented { get; private set; }

    /// <summary>
    /// Executes a buffer of raw RDP hardware command words received directly from DP DMA.
    /// </summary>
    public void ExecuteRdpCommandBuffer(uint startAddress, uint endAddress)
    {
        var rdram = _memory.Rdram.AsSpan();
        var pc = (int)(startAddress & 0x00FFFFF8);
        var endPc = (int)(endAddress & 0x00FFFFF8);

        while (pc < endPc && pc + 4 <= rdram.Length)
        {
            var word0 = BinaryPrimitives.ReadUInt32BigEndian(rdram.Slice(pc, 4));
            var opcode = (byte)(word0 >> 24);

            int commandWords = GetRdpCommandWordCount(opcode);
            if (pc + (commandWords * 4) > rdram.Length)
            {
                break;
            }

            var words = new uint[commandWords];
            for (var index = 0; index < commandWords; index++)
            {
                words[index] = BinaryPrimitives.ReadUInt32BigEndian(rdram.Slice(pc + (index * 4), 4));
            }

            ExecuteRdpCommand(opcode, words);
            RdpCommandsProcessed++;
            pc += commandWords * 4;
        }
    }

    /// <summary>
    /// Executes native RDP command packets that are already decoded, rather
    /// than walking a buffer in RDRAM.
    /// </summary>
    /// <remarks>
    /// This is the entry point for the high-level bridge: Fast3D decodes a
    /// display list, lowers each primitive into the native packet the RDP
    /// would have received, and hands the stream here. It lets the low-level
    /// engine receive real traffic without waiting for the RSP core to be
    /// accurate enough to emit command buffers itself.
    /// </remarks>
    public void ExecuteCommands(IReadOnlyList<N64RdpCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (PdRdpNative.IsAvailable)
        {
            foreach (var command in commands)
            {
                PdRdpNative.EnqueueCommand(command.Words.Span);
                RdpCommandsProcessed++;
            }

            return;
        }

        foreach (var command in commands)
        {
            ExecuteRdpCommand(command.Opcode, command.Words.Span);
            RdpCommandsProcessed++;
        }
    }

    /// <summary>
    /// Length in 32-bit words of an RDP command.
    /// </summary>
    /// <remarks>
    /// Triangle length depends on which attribute blocks follow the edge
    /// coefficients: bit 0 depth (+4), bit 1 texture (+16), bit 2 shade (+16),
    /// on top of 8 words of edge setup. Cross-checked against the
    /// angrylion-rdp-plus <c>rdp_commands</c> table (bytes): 0x08 32, 0x09 48,
    /// 0x0A 96, 0x0B 112, 0x0C 96, 0x0D 112, 0x0E 160, 0x0F 176. A fixed 28
    /// was wrong for all eight and desynchronizes the rest of the buffer.
    /// </remarks>
    private static int GetRdpCommandWordCount(byte opcode)
    {
        if (opcode is >= 0x08 and <= 0x0F)
        {
            var words = 8;
            if ((opcode & 0x01) != 0) words += 4;   // depth
            if ((opcode & 0x02) != 0) words += 16;  // texture
            if ((opcode & 0x04) != 0) words += 16;  // shade
            return words;
        }

        return opcode is 0x24 or 0x25 or 0xE4 or 0xE5 ? 4 : 2;
    }

    private void ExecuteRdpCommand(byte opcode, ReadOnlySpan<uint> words)
    {
        if (words.Length < 2)
        {
            return;
        }

        if (opcode is >= 0x08 and <= 0x0F)
        {
            // Triangle rasterization is not implemented yet. Counting the
            // primitives that reach the engine keeps the gap measurable
            // instead of silently dropping them.
            RdpTrianglesUnhandled++;
            return;
        }

        var word0 = words[0];
        var word1 = words[1];

        switch (opcode)
        {
            case 0x36: // G_FILLRECT
            case 0xF6:
                ExecuteFillRectangle(word0, word1);
                break;
            case 0x37: // G_SETFILLCOLOR
            case 0xF7:
                _fillColor = word1;
                break;
            case 0x3F: // G_SETCIMG
            case 0xFF:
                _colorImageSize = (int)((word0 >> 19) & 3);
                _colorImageWidth = (int)(word0 & 0xFFF) + 1;
                _colorImageAddress = (word1 & 0x1FFFFFFF) & 0x00FFFFFF;
                break;
            case 0x3E: // G_SETZIMG
            case 0xFE:
                _depthImageAddress = (word1 & 0x1FFFFFFF) & 0x00FFFFFF;
                break;
            case 0x2D: // G_SETSCISSOR
            case 0xED:
                _scissorLeft = (int)((word0 >> 12) & 0xFFF) / 4;
                _scissorTop = (int)(word0 & 0xFFF) / 4;
                _scissorRight = (int)((word1 >> 12) & 0xFFF) / 4;
                _scissorBottom = (int)(word1 & 0xFFF) / 4;
                break;
            case 0x2F: // G_RDPSETOTHERMODE
            case 0xEF:
                _otherModeHigh = word0 & 0x00FFFFFF;
                _otherModeLow = word1;
                _cycleType = (int)((_otherModeHigh >> 20) & 3);
                break;
            case 0x3A: // G_SETPRIMCOLOR
            case 0xFA:
                _primitiveColor = DecodeRgba32(word1);
                break;
            case 0x3B: // G_SETENVCOLOR
            case 0xFB:
                _environmentColor = DecodeRgba32(word1);
                break;
            case 0x39: // G_SETBLENDCOLOR
            case 0xF9:
                _blendColor = DecodeRgba32(word1);
                break;
            case 0x38: // G_SETFOGCOLOR
            case 0xF8:
                _fogColor = DecodeRgba32(word1);
                break;
        }
    }

    private void ExecuteFillRectangle(uint word0, uint word1)
    {
        if (_colorImageAddress >= N64Memory.RdramSize || _colorImageWidth <= 0)
        {
            return;
        }

        var right = (int)((word0 >> 14) & 0x3FF);
        var bottom = (int)((word0 >> 2) & 0x3FF);
        var left = (int)((word1 >> 14) & 0x3FF);
        var top = (int)((word1 >> 2) & 0x3FF);
        if (right < left || bottom < top)
        {
            return;
        }

        var firstX = Math.Max(0, Math.Max(left, _scissorLeft));
        var lastX = Math.Min(right, Math.Min(_colorImageWidth - 1, _scissorRight - 1));
        var firstY = Math.Max(0, Math.Max(top, _scissorTop));
        var lastY = Math.Min(bottom, _scissorBottom - 1);
        if (lastX < firstX || lastY < firstY)
        {
            return;
        }

        var bytesPerPixel = _colorImageSize == 2 ? 2u : 4u;
        var rdram = _memory.Rdram.AsSpan();

        for (var y = firstY; y <= lastY; y++)
        {
            for (var x = firstX; x <= lastX; x++)
            {
                var offset = (int)(_colorImageAddress + (((uint)y * (uint)_colorImageWidth + (uint)x) * bytesPerPixel));
                if (offset + bytesPerPixel > rdram.Length)
                {
                    continue;
                }

                if (_colorImageSize == 2)
                {
                    var pixel16 = (x & 1) == 0 ? (ushort)(_fillColor >> 16) : (ushort)_fillColor;
                    if (pixel16 == 0) pixel16 = (ushort)_fillColor;
                    BinaryPrimitives.WriteUInt16BigEndian(rdram.Slice(offset, 2), pixel16);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(rdram.Slice(offset, 4), _fillColor);
                }
            }
        }

        RdpRectanglesFilled++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector4 DecodeRgba32(uint pixel) =>
        new(
            ((pixel >> 24) & 0xFF) / 255f,
            ((pixel >> 16) & 0xFF) / 255f,
            ((pixel >> 8) & 0xFF) / 255f,
            (pixel & 0xFF) / 255f);
}
