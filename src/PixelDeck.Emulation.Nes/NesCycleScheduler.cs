namespace PixelDeck.Emulation.Nes;

internal sealed class NesCycleScheduler
{
    private readonly NesApu _apu;
    private readonly NesPpu _ppu;
    private readonly Func<bool> _apuIrqPending;
    private readonly Func<bool> _cartridgeIrqPending;
    private bool _currentApuIrqPending;
    private bool _currentCartridgeIrqPending;
    private bool _currentNmiPending;

    public NesCycleScheduler(
        NesApu apu,
        NesPpu ppu,
        Func<bool> apuIrqPending,
        Func<bool> cartridgeIrqPending)
    {
        _apu = apu;
        _ppu = ppu;
        _apuIrqPending = apuIrqPending;
        _cartridgeIrqPending = cartridgeIrqPending;
    }

    public long CpuCycles { get; private set; }

    public bool ApuIrqPendingAtPollPoint { get; private set; }

    public bool ApuIrqPendingBeforePollPoint { get; private set; }

    public bool CartridgeIrqPendingAtPollPoint { get; private set; }

    public bool IrqPendingAtPollPoint =>
        ApuIrqPendingAtPollPoint || CartridgeIrqPendingAtPollPoint;

    public bool NmiPendingAtPollPoint { get; private set; }

    public bool CurrentNmiPending => _currentNmiPending;

    public void ClockCpuCycle()
    {
        ApuIrqPendingBeforePollPoint = ApuIrqPendingAtPollPoint;
        ApuIrqPendingAtPollPoint = _currentApuIrqPending;
        CartridgeIrqPendingAtPollPoint = _currentCartridgeIrqPending;
        NmiPendingAtPollPoint = _currentNmiPending;
        CpuCycles++;
        _apu.Clock(1);
        _ppu.Tick();
        // Mapper IRQs are sampled between the first and second PPU sub-phases
        // of each NTSC CPU cycle.
        _currentCartridgeIrqPending = _cartridgeIrqPending();
        _ppu.Tick();
        _ppu.Tick();
        _currentApuIrqPending = _apuIrqPending();
        _currentNmiPending = _ppu.NmiRequested;
    }

    public void ClockCpuCycles(int count)
    {
        for (var cycle = 0; cycle < count; cycle++)
        {
            ClockCpuCycle();
        }
    }

    public void ResetCpuCycleCount()
    {
        CpuCycles = 0;
        _currentApuIrqPending = _apuIrqPending();
        _currentCartridgeIrqPending = _cartridgeIrqPending();
        _currentNmiPending = _ppu.NmiRequested;
        ApuIrqPendingAtPollPoint = false;
        ApuIrqPendingBeforePollPoint = false;
        CartridgeIrqPendingAtPollPoint = false;
        NmiPendingAtPollPoint = false;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(CpuCycles);
        writer.Write(_currentApuIrqPending);
        writer.Write(_currentCartridgeIrqPending);
        writer.Write(_currentNmiPending);
        writer.Write(ApuIrqPendingAtPollPoint);
        writer.Write(ApuIrqPendingBeforePollPoint);
        writer.Write(CartridgeIrqPendingAtPollPoint);
        writer.Write(NmiPendingAtPollPoint);
    }

    public void LoadState(BinaryReader reader)
    {
        CpuCycles = reader.ReadInt64();
        _currentApuIrqPending = reader.ReadBoolean();
        _currentCartridgeIrqPending = reader.ReadBoolean();
        _currentNmiPending = reader.ReadBoolean();
        ApuIrqPendingAtPollPoint = reader.ReadBoolean();
        ApuIrqPendingBeforePollPoint = reader.ReadBoolean();
        CartridgeIrqPendingAtPollPoint = reader.ReadBoolean();
        NmiPendingAtPollPoint = reader.ReadBoolean();
    }
}
