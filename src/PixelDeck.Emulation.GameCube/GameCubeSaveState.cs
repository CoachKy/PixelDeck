using System.IO.Compression;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Serialization manager for GameCube emulator save states.
/// </summary>
public static class GameCubeSaveState
{
    private const uint Magic = 0x43554245; // 'CUBE'
    private const ushort Version = 1;

    public static void Save(GameCubeMachine machine, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(destination);

        using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new BinaryWriter(gzip);

        writer.Write(Magic);
        writer.Write(Version);

        // CPU Registers
        writer.Write(machine.Cpu.Pc);
        writer.Write(machine.Cpu.Cr);
        writer.Write(machine.Cpu.Msr);
        writer.Write(machine.Cpu.Xer);
        writer.Write(machine.Cpu.Lr);
        writer.Write(machine.Cpu.Ctr);
        writer.Write(machine.Cpu.Fpscr);

        for (var i = 0; i < 32; i++)
        {
            writer.Write(machine.Cpu.Gpr[i]);
            writer.Write(machine.Cpu.Fpr[i]);
            writer.Write(machine.Cpu.Fpr1[i]);
        }

        // Memory State
        var mainRam = machine.Memory.MainMemory;
        writer.Write(mainRam.Length);
        writer.Write(mainRam);

        var auxRam = machine.Memory.AuxiliaryMemory;
        writer.Write(auxRam.Length);
        writer.Write(auxRam);
    }

    public static void Load(GameCubeMachine machine, Stream source)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(source);

        using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(gzip);

        var magic = reader.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException($"Invalid GameCube save state magic: 0x{magic:X8}");
        }

        var version = reader.ReadUInt16();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported GameCube save state version: {version}");
        }

        machine.Cpu.Pc = reader.ReadUInt32();
        machine.Cpu.Cr = reader.ReadUInt32();
        machine.Cpu.Msr = reader.ReadUInt32();
        machine.Cpu.Xer = reader.ReadUInt32();
        machine.Cpu.Lr = reader.ReadUInt32();
        machine.Cpu.Ctr = reader.ReadUInt32();
        machine.Cpu.Fpscr = reader.ReadUInt32();

        for (var i = 0; i < 32; i++)
        {
            machine.Cpu.Gpr[i] = reader.ReadUInt32();
            machine.Cpu.Fpr[i] = reader.ReadUInt64();
            machine.Cpu.Fpr1[i] = reader.ReadUInt64();
        }

        var mainRamLength = reader.ReadInt32();
        reader.ReadBytes(mainRamLength).CopyTo(machine.Memory.MainMemory);

        var auxRamLength = reader.ReadInt32();
        reader.ReadBytes(auxRamLength).CopyTo(machine.Memory.AuxiliaryMemory);
    }
}
