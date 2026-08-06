using PixelDeck.Emulation.N64;

namespace BootDiag;

// Drives the FlashRAM device through the exact command sequences libultra
// issues, because the unit-test project cannot be built while a wedged process
// holds PixelDeck.App's binaries.
internal static class FlashCheck
{
    public static int Run()
    {
        var storage = new byte[128 * 1024];
        Array.Fill(storage, (byte)0xFF);
        var flash = new N64FlashRam(storage);
        var failures = 0;

        // osFlashInit: 0xE1, then read the ID. libultra compares the status
        // register against whole 32-bit constants, so both routes must agree.
        flash.WriteIoWord(0x10000, 0xE1000000);
        Check(
            flash.ReadIoWord(0x00000) == 0x11118001,
            $"status register reads 11118001 (got {flash.ReadIoWord(0x00000):X8})",
            ref failures);

        var id = new byte[8];
        Check(flash.TryReadDma(id, 0x00000), "silicon ID DMA accepted", ref failures);
        Check(
            $"{id[0]:X2}{id[1]:X2}{id[2]:X2}{id[3]:X2}" == "11118001" &&
            $"{id[4]:X2}{id[5]:X2}{id[6]:X2}{id[7]:X2}" == "00C2001E",
            "ID DMA returns 11118001 00C2001E",
            ref failures);

        // osFlashSectorErase: 0x4B page, 0x78, 0xD2.
        storage[0] = 0x00;
        flash.WriteIoWord(0x10000, 0x4B000000);
        flash.WriteIoWord(0x10000, 0x78000000);
        Check(
            flash.ReadIoWord(0x00000) == 0x11118008,
            $"osFlashCheckEraseEnd sees 11118008 (got {flash.ReadIoWord(0x00000):X8})",
            ref failures);
        Check(storage[0] == 0x00, "0x78 alone does not erase; 0xD2 does", ref failures);
        flash.WriteIoWord(0x10000, 0xD2000000);
        Check(storage[0] == 0xFF, "0xD2 performed the erase", ref failures);

        // osFlashWriteBuffer + osFlashWriteArray: 0xB4, DMA, 0xA5 page, 0xD2.
        var page = new byte[N64FlashRam.PageSize];
        for (var index = 0; index < page.Length; index++)
        {
            page[index] = (byte)(index ^ 0x5A);
        }

        flash.MarkFlushed();
        flash.WriteIoWord(0x10000, 0xB4000000);
        Check(flash.TryWriteDma(page, 0x00000), "page buffer DMA accepted", ref failures);
        Check(!flash.Dirty, "page buffer alone does not touch the array", ref failures);

        flash.WriteIoWord(0x10000, 0xA5000000);
        Check(
            flash.ReadIoWord(0x00000) == 0x11118004,
            $"osFlashCheckWriteEnd sees 11118004 (got {flash.ReadIoWord(0x00000):X8})",
            ref failures);
        flash.WriteIoWord(0x10000, 0xD2000000);
        Check(flash.Dirty, "0xD2 performed the program", ref failures);
        Check(
            storage.AsSpan(0, N64FlashRam.PageSize).SequenceEqual(page),
            "programmed bytes reached the array",
            ref failures);

        // osFlashReadArray: 0xF0, then DMA. Offsets are in 16-bit units.
        flash.WriteIoWord(0x10000, 0xF0000000);
        Check(
            flash.ReadIoWord(0x00000) == 0x11118004,
            $"read mode status high half is 11118004 (got {flash.ReadIoWord(0x00000):X8})",
            ref failures);
        var readback = new byte[N64FlashRam.PageSize];
        Check(flash.TryReadDma(readback, 0x00000), "array read DMA accepted", ref failures);
        Check(readback.AsSpan().SequenceEqual(page), "array read matches what was written", ref failures);

        // A second page, to prove the offset is applied and doubled.
        storage[512] = 0xAB;
        flash.WriteIoWord(0x10000, 0xF0000000);
        var second = new byte[4];
        Check(flash.TryReadDma(second, 0x00100), "offset array read accepted", ref failures);
        Check(second[0] == 0xAB, $"offset 0x100 doubles to byte 512 (got {second[0]:X2})", ref failures);

        Console.WriteLine(failures == 0 ? "FLASHRAM: all checks passed" : $"FLASHRAM: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string description, ref int failures)
    {
        Console.WriteLine($"  [{(condition ? "ok  " : "FAIL")}] {description}");
        if (!condition)
        {
            failures++;
        }
    }
}
