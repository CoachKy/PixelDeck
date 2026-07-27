using PixelDeck.Emulation.N64;

namespace PixelDeck.App.Tests;

public sealed class N64TlbTests
{
    private const uint RefillVector = 0x80000000;

    [Fact]
    public void LoadFromUnmappedAddressRaisesTlbRefillException()
    {
        var machine = CreateMachine();
        machine.Memory.WriteUInt32(0xA4000040, 0x3C080040); // LUI t0, 0x0040
        machine.Memory.WriteUInt32(0xA4000044, 0x8D090000); // LW  t1, 0(t0)

        machine.RunInstructions(2);

        Assert.Equal(RefillVector, machine.Cpu.ProgramCounter);
        Assert.Equal(0x00400000u, machine.Cpu.ReadCoprocessor0(8)); // BadVAddr
        Assert.Equal(2u, (machine.Cpu.ReadCoprocessor0(13) >> 2) & 0x1F); // ExcCode TLBL
        Assert.Equal(0xA4000044u, machine.Cpu.ReadCoprocessor0(14)); // EPC
        Assert.NotEqual(0u, machine.Cpu.ReadCoprocessor0(12) & 2); // Status.EXL
        Assert.Equal(0x00400000u, machine.Cpu.ReadCoprocessor0(10) & 0xFFFFE000); // EntryHi.VPN2
        Assert.Equal(
            (0x00400000u >> 13) << 4,
            machine.Cpu.ReadCoprocessor0(4) & 0x007FFFF0); // Context.BadVPN2
    }

    [Fact]
    public void StoreToUnmappedAddressRaisesTlbStoreException()
    {
        var machine = CreateMachine();
        machine.Memory.WriteUInt32(0xA4000040, 0x3C080040); // LUI t0, 0x0040
        machine.Memory.WriteUInt32(0xA4000044, 0xAD090000); // SW  t1, 0(t0)

        machine.RunInstructions(2);

        Assert.Equal(RefillVector, machine.Cpu.ProgramCounter);
        Assert.Equal(3u, (machine.Cpu.ReadCoprocessor0(13) >> 2) & 0x1F); // ExcCode TLBS
        Assert.Equal(0x00400000u, machine.Cpu.ReadCoprocessor0(8));
    }

    [Fact]
    public void FetchFromUnmappedAddressRaisesTlbRefillExceptionAtTheTarget()
    {
        var machine = CreateMachine();
        machine.Memory.WriteUInt32(0xA4000040, 0x3C080040); // LUI t0, 0x0040
        machine.Memory.WriteUInt32(0xA4000044, 0x01000008); // JR  t0
        machine.Memory.WriteUInt32(0xA4000048, 0x00000000); // NOP (delay slot)

        machine.RunInstructions(4);

        Assert.Equal(RefillVector, machine.Cpu.ProgramCounter);
        Assert.Equal(0x00400000u, machine.Cpu.ReadCoprocessor0(8));
        Assert.Equal(0x00400000u, machine.Cpu.ReadCoprocessor0(14)); // EPC is the target
        Assert.Equal(0u, machine.Cpu.ReadCoprocessor0(13) >> 31); // not a delay slot
    }

    [Fact]
    public void MappedAccessSucceedsWithoutAnException()
    {
        var machine = CreateMachine();
        machine.Memory.WriteUInt32(0x803B0668, 0x12345678);
        machine.Memory.WriteTlbEntry(
            index: 0,
            pageMask: 0x001E000,
            entryHi: 0x04000000,
            entryLo0: (0x003B0000 >> 6) | 31u,
            entryLo1: (0x003C0000 >> 6) | 31u);
        machine.Memory.WriteUInt32(0xA4000040, 0x3C080400); // LUI t0, 0x0400
        machine.Memory.WriteUInt32(0xA4000044, 0x8D090668); // LW  t1, 0x668(t0)

        machine.RunInstructions(2);

        Assert.Equal(0x12345678u, (uint)machine.Cpu.Registers[9]);
        Assert.Equal(0u, machine.Cpu.ReadCoprocessor0(12) & 2); // no exception taken
    }

    private static N64Machine CreateMachine() =>
        N64Machine.Create(N64Cartridge.FromBytes(CreateCartridgeImage()));

    private static byte[] CreateCartridgeImage()
    {
        var image = new byte[0x2000];
        image[0] = 0x80;
        image[1] = 0x37;
        image[2] = 0x12;
        image[3] = 0x40;
        image[8] = 0x80;
        image[10] = 0x04;
        "PIXEL64 TLB         "u8.CopyTo(image.AsSpan(0x20, 20));
        image[0x3B] = (byte)'N';
        image[0x3C] = (byte)'P';
        image[0x3D] = (byte)'X';
        image[0x3E] = (byte)'E';
        return image;
    }
}
