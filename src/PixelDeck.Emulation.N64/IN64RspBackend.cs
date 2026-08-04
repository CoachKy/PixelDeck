namespace PixelDeck.Emulation.N64;

/// <summary>
/// Defines the execution interface for the Nintendo 64 Reality Signal Processor (RSP).
/// Abstracts raw scalar/vector instruction execution, IMEM/DMEM DMA state,
/// and task-level execution.
/// </summary>
public interface IN64RspBackend
{
    /// <summary>
    /// Descriptive name of the RSP execution backend.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Current scalar and vector register state of the RSP.
    /// </summary>
    N64RspState State { get; }

    /// <summary>
    /// Total number of RSP instructions executed since initialization or reset.
    /// </summary>
    long InstructionsExecuted { get; }

    /// <summary>
    /// Total number of RSP tasks processed.
    /// </summary>
    long TasksProcessed { get; }

    /// <summary>
    /// Indicates whether high-level microcode fallback execution is enabled for standard tasks.
    /// </summary>
    bool HleFallbackEnabled { get; set; }

    /// <summary>
    /// Resets the RSP state, registers, and execution counters.
    /// </summary>
    void Reset();

    /// <summary>
    /// Executes a single RSP instruction at the current Program Counter (PC).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Step is the canonical single-instruction execution method name across PixelDeck core CPUs and processors.")]
    void Step();

    /// <summary>
    /// Executes an RSP task (e.g. OSTask payload) using instruction-level execution or HLE fallback.
    /// </summary>
    void ExecuteTask(N64RspTask task);

    /// <summary>
    /// Persists RSP state to the binary stream for save-states.
    /// </summary>
    void SaveState(BinaryWriter writer);

    /// <summary>
    /// Restores RSP state from the binary stream for save-states.
    /// </summary>
    void LoadState(BinaryReader reader);
}
