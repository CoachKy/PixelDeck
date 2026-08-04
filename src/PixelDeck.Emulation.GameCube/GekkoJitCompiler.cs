using System.Reflection.Emit;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Dynamic Method (ILGenerator) JIT recompiler for IBM Gekko PowerPC instructions.
/// </summary>
public sealed class GekkoJitCompiler
{
    private readonly Dictionary<uint, Func<GekkoCpu, uint>> _blockCache = [];

    public int CompiledBlockCount => _blockCache.Count;

    public bool TryExecuteBlock(GekkoCpu cpu, uint pc, out uint nextPc)
    {
        if (_blockCache.TryGetValue(pc, out var block))
        {
            nextPc = block(cpu);
            return true;
        }

        nextPc = pc;
        return false;
    }

    public static uint ExecuteAddi(GekkoCpu cpu, int d, int a, int simm)
    {
        var baseVal = a == 0 ? 0u : cpu.GetGpr(a);
        cpu.SetGpr(d, (uint)((int)baseVal + simm));
        return 0;
    }

    public Func<GekkoCpu, uint>? CompileBlock(GekkoCpu cpu, uint startPc, int maxInstructions = 128)
    {
        if (_blockCache.TryGetValue(startPc, out var existing))
        {
            return existing;
        }

        var dynamicMethod = new DynamicMethod(
            $"GekkoBlock_{startPc:X8}",
            typeof(uint),
            [typeof(GekkoCpu)],
            typeof(GekkoCpu));

        var il = dynamicMethod.GetILGenerator();

        var addiHelper = typeof(GekkoJitCompiler).GetMethod(nameof(ExecuteAddi))!;

        var pc = startPc;
        var instructionCount = 0;
        var ended = false;

        while (instructionCount < maxInstructions && !ended)
        {
            if (!cpu.Memory.TryReadInstruction(pc, out var instruction))
            {
                break;
            }

            var primary = instruction >> 26;
            var d = (int)((instruction >> 21) & 0x1F);
            var a = (int)((instruction >> 16) & 0x1F);

            switch (primary)
            {
                case 14: // addi rD, rA, SIMM
                {
                    var simm = (int)(short)(instruction & 0xFFFF);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, d);
                    il.Emit(OpCodes.Ldc_I4, a);
                    il.Emit(OpCodes.Ldc_I4, simm);
                    il.Emit(OpCodes.Call, addiHelper);
                    il.Emit(OpCodes.Pop);

                    pc += 4;
                    instructionCount++;
                    break;
                }

                case 18: // B / BL / BA / BLA
                {
                    var target = instruction & 0x03FF_FFFCu;
                    if ((instruction & 0x0200_0000u) != 0) target |= 0xFC00_0000u;
                    var absolute = (instruction & 2) != 0;
                    var nextPc = absolute ? target : pc + target;

                    il.Emit(OpCodes.Ldc_I4, (int)nextPc);
                    il.Emit(OpCodes.Ret);
                    ended = true;
                    break;
                }

                default:
                    il.Emit(OpCodes.Ldc_I4, (int)pc);
                    il.Emit(OpCodes.Ret);
                    ended = true;
                    break;
            }
        }

        if (!ended)
        {
            il.Emit(OpCodes.Ldc_I4, (int)pc);
            il.Emit(OpCodes.Ret);
        }

        var compiled = (Func<GekkoCpu, uint>)dynamicMethod.CreateDelegate(typeof(Func<GekkoCpu, uint>));
        _blockCache[startPc] = compiled;
        return compiled;
    }

    public void ClearCache()
    {
        _blockCache.Clear();
    }
}
