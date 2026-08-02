using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public sealed class GekkoDisassemblerTests
{
    /// <summary>
    /// The first instructions of Super Mario Sunshine's <c>__start</c>, read
    /// from the real disc. Anchoring on a retail entry point rather than on
    /// encodings written by hand means the test fails if the decoder drifts
    /// away from what a real game contains.
    /// </summary>
    [Theory]
    [InlineData(0x8000_522Cu, 0x4800_0139u, "bl 0x80005364")]
    [InlineData(0x8000_5234u, 0x3800_FFFFu, "li r0, -1")]
    [InlineData(0x8000_5238u, 0x9421_FFF8u, "stwu r1, -8(r1)")]
    [InlineData(0x8000_523Cu, 0x9001_0004u, "stw r0, 4(r1)")]
    [InlineData(0x8000_524Cu, 0x3CC0_8000u, "lis r6, 0x8000")]
    [InlineData(0x8000_5250u, 0x38C6_0044u, "addi r6, r6, 68")]
    [InlineData(0x8000_5260u, 0x80C6_0000u, "lwz r6, 0(r6)")]
    [InlineData(0x8000_5264u, 0x2806_0000u, "cmplwi cr0, r6, 0x0")]
    [InlineData(0x8000_5268u, 0x4182_000Cu, "beq 0x80005274")]
    [InlineData(0x8000_5270u, 0x4800_0024u, "b 0x80005294")]
    public void DescribesTheEntryPointOfARetailTitle(
        uint address,
        uint instruction,
        string expected) =>
        Assert.Equal(expected, GekkoDisassembler.Describe(instruction, address));

    [Theory]
    [InlineData(0x6000_0000u, "nop")]
    [InlineData(0x4E80_0020u, "blr")]
    [InlineData(0x4E80_0420u, "bctr")]
    [InlineData(0x4C00_0064u, "rfi")]
    [InlineData(0x7C08_02A6u, "mflr r0")]
    [InlineData(0x7C08_03A6u, "mtlr r0")]
    [InlineData(0x7C00_04ACu, "sync")]
    public void NamesTheInstructionsThatAppearEverywhere(uint instruction, string expected) =>
        Assert.Equal(expected, GekkoDisassembler.Describe(instruction));

    [Fact]
    public void NamesTheFloatingPointAndPairedSingleInstructions()
    {
        // The disassembler covers instructions whether or not the interpreter
        // runs them, so a run that stops still names what stopped it. These
        // all execute now; they did not when the decoder was written.
        Assert.Equal("mtfsb1 29", DescribeExtended(63, 38, 29));
        Assert.StartsWith("fmr", GekkoDisassembler.Describe((63u << 26) | (72u << 1)), StringComparison.Ordinal);
        Assert.StartsWith("psq_l", GekkoDisassembler.Describe(56u << 26), StringComparison.Ordinal);
        Assert.StartsWith("lfd", GekkoDisassembler.Describe(50u << 26), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEncodingsAreShownAsData()
    {
        // Never guess. An invented mnemonic in a trace is worse than a hex
        // word, because it sends the reader after an instruction that is not
        // there.
        Assert.Equal(".word 0x1", GekkoDisassembler.Describe(1));
        Assert.StartsWith(".word", GekkoDisassembler.Describe(0x0400_0000), StringComparison.Ordinal);
    }

    private static string DescribeExtended(uint primary, uint extended, uint destination) =>
        GekkoDisassembler.Describe((primary << 26) | (destination << 21) | (extended << 1));
}
