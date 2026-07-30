using System.Numerics;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Inspectable RDP state from the most recently processed display list.
/// Compatibility traces use this to identify the exact combine and render
/// modes behind a visual regression instead of relying on screenshots alone.
/// </summary>
public readonly record struct N64RdpStateSnapshot(
    uint OtherModeHigh,
    uint OtherModeLow,
    uint CycleType,
    Vector4 PrimitiveColor,
    Vector4 EnvironmentColor,
    Vector4 FogColor,
    Vector4 BlendColor,
    bool CombinerConfigured,
    bool CombinerUsesTexture,
    uint KeyGreenBlueWord0,
    uint KeyGreenBlueWord1,
    uint KeyRedWord1,
    uint ConvertWord0,
    uint ConvertWord1,
    ushort PrimitiveDepth,
    ushort PrimitiveDeltaDepth,
    long AlphaPixelsRejected,
    long FramebufferPixelsBlended);
