using System.Globalization;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Turns a Gekko instruction word into readable text.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="GekkoCpu"/> on purpose. The interpreter's job is
/// to be fast and to know only what it can execute; this one's is to name
/// everything, including instructions PixelCube cannot run yet — which is
/// exactly when a name is worth most, because the trace line that stops a run
/// should say <c>psq_l f1, 0(r3)</c> rather than a hex word.
///
/// It also recognises the common simplified mnemonics. Real PowerPC listings
/// are written in them, so a trace that says <c>li r3, 0</c> can be compared
/// against a disassembly from anywhere else, and one that says
/// <c>addi r3, r0, 0</c> cannot.
/// </remarks>
public static class GekkoDisassembler
{
    /// <summary>
    /// Describes <paramref name="instruction"/>, resolving branch targets
    /// against <paramref name="address"/>.
    /// </summary>
    public static string Describe(uint instruction, uint address = 0)
    {
        var primary = instruction >> 26;
        var d = (instruction >> 21) & 0x1F;
        var a = (instruction >> 16) & 0x1F;
        var b = (instruction >> 11) & 0x1F;
        var simm = (short)(instruction & 0xFFFF);
        var uimm = instruction & 0xFFFF;
        var rc = (instruction & 1) != 0 ? "." : string.Empty;

        return primary switch
        {
            7 => $"mulli r{d}, r{a}, {simm}",
            8 => $"subfic r{d}, r{a}, {simm}",
            10 => $"cmplwi cr{(instruction >> 23) & 7}, r{a}, {Hex(uimm)}",
            11 => $"cmpwi cr{(instruction >> 23) & 7}, r{a}, {simm}",
            12 => $"addic r{d}, r{a}, {simm}",
            13 => $"addic. r{d}, r{a}, {simm}",
            14 => a == 0 ? $"li r{d}, {simm}" : $"addi r{d}, r{a}, {simm}",
            15 => a == 0 ? $"lis r{d}, {Hex(uimm)}" : $"addis r{d}, r{a}, {Hex(uimm)}",
            16 => DescribeConditionalBranch(instruction, address),
            18 => DescribeBranch(instruction, address),
            19 => DescribeBranchGroup(instruction),
            20 => $"rlwimi{rc} r{a}, r{d}, {b}, {(instruction >> 6) & 0x1F}, {(instruction >> 1) & 0x1F}",
            21 => DescribeRotateImmediate(instruction, d, a, b, rc),
            23 => $"rlwnm{rc} r{a}, r{d}, r{b}, {(instruction >> 6) & 0x1F}, {(instruction >> 1) & 0x1F}",
            24 => instruction == 0x6000_0000 ? "nop" : $"ori r{a}, r{d}, {Hex(uimm)}",
            25 => $"oris r{a}, r{d}, {Hex(uimm)}",
            26 => $"xori r{a}, r{d}, {Hex(uimm)}",
            27 => $"xoris r{a}, r{d}, {Hex(uimm)}",
            28 => $"andi. r{a}, r{d}, {Hex(uimm)}",
            29 => $"andis. r{a}, r{d}, {Hex(uimm)}",
            31 => DescribeIntegerGroup(instruction, d, a, b, rc),
            32 => $"lwz r{d}, {simm}(r{a})",
            33 => $"lwzu r{d}, {simm}(r{a})",
            34 => $"lbz r{d}, {simm}(r{a})",
            35 => $"lbzu r{d}, {simm}(r{a})",
            36 => $"stw r{d}, {simm}(r{a})",
            37 => $"stwu r{d}, {simm}(r{a})",
            38 => $"stb r{d}, {simm}(r{a})",
            39 => $"stbu r{d}, {simm}(r{a})",
            40 => $"lhz r{d}, {simm}(r{a})",
            41 => $"lhzu r{d}, {simm}(r{a})",
            42 => $"lha r{d}, {simm}(r{a})",
            43 => $"lhau r{d}, {simm}(r{a})",
            44 => $"sth r{d}, {simm}(r{a})",
            45 => $"sthu r{d}, {simm}(r{a})",
            46 => $"lmw r{d}, {simm}(r{a})",
            47 => $"stmw r{d}, {simm}(r{a})",
            48 => $"lfs f{d}, {simm}(r{a})",
            49 => $"lfsu f{d}, {simm}(r{a})",
            50 => $"lfd f{d}, {simm}(r{a})",
            51 => $"lfdu f{d}, {simm}(r{a})",
            52 => $"stfs f{d}, {simm}(r{a})",
            53 => $"stfsu f{d}, {simm}(r{a})",
            54 => $"stfd f{d}, {simm}(r{a})",
            55 => $"stfdu f{d}, {simm}(r{a})",
            56 => $"psq_l f{d}, {(instruction & 0xFFF) - ((instruction & 0x800) != 0 ? 0x1000 : 0)}(r{a})",
            57 => $"psq_lu f{d}, {(instruction & 0xFFF)}(r{a})",
            59 => DescribeSingleFloat(instruction, d, a, b, rc),
            60 => $"psq_st f{d}, {(instruction & 0xFFF) - ((instruction & 0x800) != 0 ? 0x1000 : 0)}(r{a})",
            61 => $"psq_stu f{d}, {(instruction & 0xFFF)}(r{a})",
            63 => DescribeDoubleFloat(instruction, d, a, b, rc),
            17 => "sc",
            _ => $".word {Hex(instruction)}"
        };
    }

    private static string DescribeRotateImmediate(uint instruction, uint d, uint a, uint b, string rc)
    {
        var begin = (instruction >> 6) & 0x1F;
        var end = (instruction >> 1) & 0x1F;

        // The three rotate-and-mask spellings that appear constantly in
        // compiled code, named the way a listing would name them.
        if (begin == 0 && end == 31 - b)
        {
            return $"slwi{rc} r{a}, r{d}, {b}";
        }

        if (end == 31 && begin == 32 - b && b != 0)
        {
            return $"srwi{rc} r{a}, r{d}, {32 - b}";
        }

        if (b == 0 && begin == 0)
        {
            return $"clrrwi{rc} r{a}, r{d}, {31 - end}";
        }

        return $"rlwinm{rc} r{a}, r{d}, {b}, {begin}, {end}";
    }

    private static string DescribeBranch(uint instruction, uint address)
    {
        var offset = (int)SignExtend26(instruction & 0x03FF_FFFC);
        var absolute = (instruction & 2) != 0;
        var link = (instruction & 1) != 0;
        var target = absolute ? (uint)offset : (uint)(address + offset);
        return $"b{(link ? "l" : string.Empty)}{(absolute ? "a" : string.Empty)} {Hex(target)}";
    }

    private static string DescribeConditionalBranch(uint instruction, uint address)
    {
        var bo = (instruction >> 21) & 0x1F;
        var bi = (instruction >> 16) & 0x1F;
        var offset = (short)(instruction & 0xFFFC);
        var absolute = (instruction & 2) != 0;
        var link = (instruction & 1) != 0;
        var target = absolute ? (uint)offset : (uint)(address + offset);
        var suffix = (link ? "l" : string.Empty) + (absolute ? "a" : string.Empty);

        var simplified = SimplifiedConditionName(bo, bi);
        return simplified is null
            ? $"bc{suffix} {bo}, {bi}, {Hex(target)}"
            : $"b{simplified}{suffix} {ConditionRegister(bi)}{Hex(target)}";
    }

    private static string DescribeBranchGroup(uint instruction)
    {
        var extended = (instruction >> 1) & 0x3FF;
        var bo = (instruction >> 21) & 0x1F;
        var bi = (instruction >> 16) & 0x1F;
        var link = (instruction & 1) != 0 ? "l" : string.Empty;

        switch (extended)
        {
            case 16:
            case 528:
            {
                var through = extended == 16 ? "lr" : "ctr";
                if (bo == 20)
                {
                    return $"b{through}{link}";
                }

                var simplified = SimplifiedConditionName(bo, bi);
                return simplified is null
                    ? $"bc{through}{link} {bo}, {bi}"
                    : $"b{simplified}{through}{link} {ConditionRegister(bi)}".TrimEnd(',', ' ');
            }

            case 0: return $"mcrf cr{(instruction >> 23) & 7}, cr{(instruction >> 18) & 7}";
            case 50: return "rfi";
            case 150: return "isync";
            case 33: return $"crnor {bo}, {bi}, {(instruction >> 11) & 0x1F}";
            case 129: return $"crandc {bo}, {bi}, {(instruction >> 11) & 0x1F}";
            case 193: return bo == bi && bi == ((instruction >> 11) & 0x1F)
                ? $"crclr {bo}"
                : $"crxor {bo}, {bi}, {(instruction >> 11) & 0x1F}";
            case 225: return $"crnand {bo}, {bi}, {(instruction >> 11) & 0x1F}";
            case 257: return $"crand {bo}, {bi}, {(instruction >> 11) & 0x1F}";
            case 289: return $"creqv {bo}, {bi}, {(instruction >> 11) & 0x1F}";
            case 417: return $"crorc {bo}, {bi}, {(instruction >> 11) & 0x1F}";
            case 449: return bo == bi && bi == ((instruction >> 11) & 0x1F)
                ? $"mr cr, {bo}"
                : $"cror {bo}, {bi}, {(instruction >> 11) & 0x1F}";
            default: return $".word {Hex(instruction)}";
        }
    }

    private static string DescribeIntegerGroup(uint instruction, uint d, uint a, uint b, string rc)
    {
        var extended = (instruction >> 1) & 0x3FF;
        var oe = (instruction & 0x400) != 0 ? "o" : string.Empty;

        return extended switch
        {
            0 => $"cmpw cr{(instruction >> 23) & 7}, r{a}, r{b}",
            32 => $"cmplw cr{(instruction >> 23) & 7}, r{a}, r{b}",

            266 => $"add{oe}{rc} r{d}, r{a}, r{b}",
            10 => $"addc{oe}{rc} r{d}, r{a}, r{b}",
            138 => $"adde{oe}{rc} r{d}, r{a}, r{b}",
            202 => $"addze{oe}{rc} r{d}, r{a}",
            234 => $"addme{oe}{rc} r{d}, r{a}",
            40 => $"subf{oe}{rc} r{d}, r{a}, r{b}",
            8 => $"subfc{oe}{rc} r{d}, r{a}, r{b}",
            136 => $"subfe{oe}{rc} r{d}, r{a}, r{b}",
            200 => $"subfze{oe}{rc} r{d}, r{a}",
            232 => $"subfme{oe}{rc} r{d}, r{a}",
            104 => $"neg{oe}{rc} r{d}, r{a}",
            235 => $"mullw{oe}{rc} r{d}, r{a}, r{b}",
            75 => $"mulhw{rc} r{d}, r{a}, r{b}",
            11 => $"mulhwu{rc} r{d}, r{a}, r{b}",
            491 => $"divw{oe}{rc} r{d}, r{a}, r{b}",
            459 => $"divwu{oe}{rc} r{d}, r{a}, r{b}",

            28 => $"and{rc} r{a}, r{d}, r{b}",
            60 => $"andc{rc} r{a}, r{d}, r{b}",
            444 => d == b ? $"mr{rc} r{a}, r{d}" : $"or{rc} r{a}, r{d}, r{b}",
            412 => $"orc{rc} r{a}, r{d}, r{b}",
            316 => $"xor{rc} r{a}, r{d}, r{b}",
            476 => $"nand{rc} r{a}, r{d}, r{b}",
            124 => d == b ? $"not{rc} r{a}, r{d}" : $"nor{rc} r{a}, r{d}, r{b}",
            284 => $"eqv{rc} r{a}, r{d}, r{b}",
            954 => $"extsb{rc} r{a}, r{d}",
            922 => $"extsh{rc} r{a}, r{d}",
            26 => $"cntlzw{rc} r{a}, r{d}",

            24 => $"slw{rc} r{a}, r{d}, r{b}",
            536 => $"srw{rc} r{a}, r{d}, r{b}",
            792 => $"sraw{rc} r{a}, r{d}, r{b}",
            824 => $"srawi{rc} r{a}, r{d}, {b}",

            23 => $"lwzx r{d}, r{a}, r{b}",
            55 => $"lwzux r{d}, r{a}, r{b}",
            87 => $"lbzx r{d}, r{a}, r{b}",
            119 => $"lbzux r{d}, r{a}, r{b}",
            279 => $"lhzx r{d}, r{a}, r{b}",
            311 => $"lhzux r{d}, r{a}, r{b}",
            343 => $"lhax r{d}, r{a}, r{b}",
            375 => $"lhaux r{d}, r{a}, r{b}",
            151 => $"stwx r{d}, r{a}, r{b}",
            183 => $"stwux r{d}, r{a}, r{b}",
            215 => $"stbx r{d}, r{a}, r{b}",
            247 => $"stbux r{d}, r{a}, r{b}",
            407 => $"sthx r{d}, r{a}, r{b}",
            439 => $"sthux r{d}, r{a}, r{b}",
            534 => $"lwbrx r{d}, r{a}, r{b}",
            662 => $"stwbrx r{d}, r{a}, r{b}",
            535 => $"lfsx f{d}, r{a}, r{b}",
            599 => $"lfdx f{d}, r{a}, r{b}",
            663 => $"stfsx f{d}, r{a}, r{b}",
            727 => $"stfdx f{d}, r{a}, r{b}",

            339 => DescribeSpecialRegisterMove("mf", instruction, d),
            467 => DescribeSpecialRegisterMove("mt", instruction, d),
            371 => $"mftb r{d}",
            19 => $"mfcr r{d}",
            144 => $"mtcrf {Hex((instruction >> 12) & 0xFF)}, r{d}",
            83 => $"mfmsr r{d}",
            146 => $"mtmsr r{d}",
            210 => $"mtsr {a}, r{d}",
            242 => $"mtsrin r{d}, r{b}",
            595 => $"mfsr r{d}, {a}",
            659 => $"mfsrin r{d}, r{b}",

            54 => $"dcbst r{a}, r{b}",
            86 => $"dcbf r{a}, r{b}",
            246 => $"dcbtst r{a}, r{b}",
            278 => $"dcbt r{a}, r{b}",
            470 => $"dcbi r{a}, r{b}",
            1014 => $"dcbz r{a}, r{b}",
            982 => $"icbi r{a}, r{b}",
            598 => "sync",
            854 => "eieio",
            306 => $"tlbie r{b}",
            566 => "tlbsync",
            4 => $"tw {d}, r{a}, r{b}",

            _ => $".word {Hex(instruction)}"
        };
    }

    private static string DescribeSpecialRegisterMove(string prefix, uint instruction, uint register)
    {
        var field = (instruction >> 11) & 0x3FF;
        var spr = ((field & 0x1F) << 5) | ((field >> 5) & 0x1F);
        var name = SpecialRegisterName(spr);
        return name is null
            ? $"{prefix}spr {(prefix == "mt" ? $"{spr}, r{register}" : $"r{register}, {spr}")}"
            : prefix == "mt" ? $"mt{name} r{register}" : $"mf{name} r{register}";
    }

    private static string? SpecialRegisterName(uint spr) => spr switch
    {
        1 => "xer",
        8 => "lr",
        9 => "ctr",
        _ => null
    };

    private static string DescribeSingleFloat(uint instruction, uint d, uint a, uint b, string rc)
    {
        var extended = (instruction >> 1) & 0x1F;
        var c = (instruction >> 6) & 0x1F;
        return extended switch
        {
            18 => $"fdivs{rc} f{d}, f{a}, f{b}",
            20 => $"fsubs{rc} f{d}, f{a}, f{b}",
            21 => $"fadds{rc} f{d}, f{a}, f{b}",
            24 => $"fres{rc} f{d}, f{b}",
            25 => $"fmuls{rc} f{d}, f{a}, f{c}",
            28 => $"fmsubs{rc} f{d}, f{a}, f{c}, f{b}",
            29 => $"fmadds{rc} f{d}, f{a}, f{c}, f{b}",
            30 => $"fnmsubs{rc} f{d}, f{a}, f{c}, f{b}",
            31 => $"fnmadds{rc} f{d}, f{a}, f{c}, f{b}",
            _ => $".word {Hex(instruction)}"
        };
    }

    private static string DescribeDoubleFloat(uint instruction, uint d, uint a, uint b, string rc)
    {
        var extended = (instruction >> 1) & 0x3FF;
        var shortExtended = (instruction >> 1) & 0x1F;
        var c = (instruction >> 6) & 0x1F;

        var arithmetic = shortExtended switch
        {
            18 => $"fdiv{rc} f{d}, f{a}, f{b}",
            20 => $"fsub{rc} f{d}, f{a}, f{b}",
            21 => $"fadd{rc} f{d}, f{a}, f{b}",
            25 => $"fmul{rc} f{d}, f{a}, f{c}",
            28 => $"fmsub{rc} f{d}, f{a}, f{c}, f{b}",
            29 => $"fmadd{rc} f{d}, f{a}, f{c}, f{b}",
            30 => $"fnmsub{rc} f{d}, f{a}, f{c}, f{b}",
            31 => $"fnmadd{rc} f{d}, f{a}, f{c}, f{b}",
            _ => null
        };

        if (arithmetic is not null)
        {
            return arithmetic;
        }

        return extended switch
        {
            0 => $"fcmpu cr{(instruction >> 23) & 7}, f{a}, f{b}",
            12 => $"frsp{rc} f{d}, f{b}",
            14 => $"fctiw{rc} f{d}, f{b}",
            15 => $"fctiwz{rc} f{d}, f{b}",
            32 => $"fcmpo cr{(instruction >> 23) & 7}, f{a}, f{b}",
            40 => $"fneg{rc} f{d}, f{b}",
            72 => $"fmr{rc} f{d}, f{b}",

            // The FPSCR writes a GameCube __start reaches before anything
            // else PixelCube cannot execute.
            38 => $"mtfsb1{rc} {d}",
            70 => $"mtfsb0{rc} {d}",
            64 => $"mcrfs cr{(instruction >> 23) & 7}, cr{(instruction >> 18) & 7}",
            134 => $"mtfsfi{rc} cr{(instruction >> 23) & 7}, {(instruction >> 12) & 0xF}",
            136 => $"fnabs{rc} f{d}, f{b}",
            264 => $"fabs{rc} f{d}, f{b}",
            583 => $"mffs{rc} f{d}",
            711 => $"mtfsf{rc} {(instruction >> 17) & 0xFF}, f{b}",
            _ => $".word {Hex(instruction)}"
        };
    }

    /// <summary>
    /// Names the branch conditions that appear in compiled code. Returns null
    /// for the encodings that have no simplified spelling.
    /// </summary>
    private static string? SimplifiedConditionName(uint bo, uint bi) => (bo & 0x1E, bi & 3) switch
    {
        (4, 0) => "ge",
        (4, 1) => "le",
        (4, 2) => "ne",
        (4, 3) => "ns",
        (12, 0) => "lt",
        (12, 1) => "gt",
        (12, 2) => "eq",
        (12, 3) => "so",
        (16, _) => "dnz",
        (18, _) => "dz",
        (20, _) => string.Empty,
        _ => null
    };

    private static string ConditionRegister(uint bi) =>
        bi < 4 ? string.Empty : $"cr{bi / 4}, ";

    private static uint SignExtend26(uint value) =>
        (value & 0x0200_0000) != 0 ? value | 0xFC00_0000 : value;

    private static string Hex(uint value) =>
        "0x" + value.ToString("X", CultureInfo.InvariantCulture);
}
