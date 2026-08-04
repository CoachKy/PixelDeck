namespace PixelDeck.Emulation.N64;

/// <summary>
/// Information snapshot of a single captured RDP graphic primitive.
/// </summary>
public sealed record RdpPrimitiveSnapshot(
    uint Opcode,
    string OpcodeName,
    uint Word0,
    uint Word1,
    int VertexCount,
    bool Is3dTriangle);

/// <summary>
/// Inspector and debugger engine for real-time N64 RDP graphics frame inspection,
/// primitive stepping, wireframe mode toggling, and buffer snapshots.
/// </summary>
public sealed class N64FrameInspector
{
    private readonly List<RdpPrimitiveSnapshot> _currentFramePrimitives = new(1024);
    private readonly List<RdpPrimitiveSnapshot> _lastFrameSnapshot = new(1024);

    /// <summary>
    /// Enables or disables wireframe rendering mode for 3D primitives.
    /// </summary>
    public bool WireframeModeEnabled { get; set; }

    /// <summary>
    /// Enables or disables RDP command primitive capture.
    /// </summary>
    public bool PrimitiveCaptureEnabled { get; set; } = true;

    /// <summary>
    /// Total number of primitives captured in the current active frame.
    /// </summary>
    public int CapturedPrimitiveCount => _currentFramePrimitives.Count;

    /// <summary>
    /// Gets a read-only list of primitives captured in the last completed frame.
    /// </summary>
    public IReadOnlyList<RdpPrimitiveSnapshot> LastFramePrimitives => _lastFrameSnapshot;

    /// <summary>
    /// Records a captured RDP command primitive into the active frame snapshot.
    /// </summary>
    public void RecordPrimitive(uint word0, uint word1, int vertexCount = 0, bool is3dTriangle = false)
    {
        if (!PrimitiveCaptureEnabled)
        {
            return;
        }

        var opcode = word0 >> 24;
        var opcodeName = GetOpcodeName((byte)opcode);
        _currentFramePrimitives.Add(new RdpPrimitiveSnapshot((byte)opcode, opcodeName, word0, word1, vertexCount, is3dTriangle));
    }

    /// <summary>
    /// End-of-frame notification triggered by VI scanout flip. Swaps frame buffers.
    /// </summary>
    public void OnFrameEnd()
    {
        _lastFrameSnapshot.Clear();
        _lastFrameSnapshot.AddRange(_currentFramePrimitives);
        _currentFramePrimitives.Clear();
    }

    // Hardware RDP opcode names. These must stay aligned with the decode table
    // in Fast3dRenderer.RdpTrace.cs; both were previously shifted one slot high
    // across the 0x2A-0x30 block.
    private static string GetOpcodeName(byte opcode) => opcode switch
    {
        0x00 => "NOOP",
        >= 0x08 and <= 0x0F => "TRIANGLE",
        0x24 => "TEX_RECT",
        0x25 => "TEX_RECT_FLIP",
        0x26 => "SYNC_LOAD",
        0x27 => "SYNC_PIPE",
        0x28 => "SYNC_TILE",
        0x29 => "SYNC_FULL",
        0x2A => "SET_KEY_GB",
        0x2B => "SET_KEY_R",
        0x2C => "SET_CONVERT",
        0x2D => "SET_SCISSOR",
        0x2E => "SET_PRIM_DEPTH",
        0x2F => "SET_OTHER_MODES",
        0x30 => "LOAD_TLUT",
        0x32 => "SET_TILE_SIZE",
        0x33 => "LOAD_BLOCK",
        0x34 => "LOAD_TILE",
        0x35 => "SET_TILE",
        0x36 => "FILL_RECT",
        0x37 => "SET_FILL_COLOR",
        0x38 => "SET_FOG_COLOR",
        0x39 => "SET_BLEND_COLOR",
        0x3A => "SET_PRIM_COLOR",
        0x3B => "SET_ENV_COLOR",
        0x3C => "SET_COMBINE",
        0x3D => "SET_TEX_IMAGE",
        0x3E => "SET_DEPTH_IMAGE",
        0x3F => "SET_COLOR_IMAGE",
        _ => $"RDP_0x{opcode:X2}"
    };
}
