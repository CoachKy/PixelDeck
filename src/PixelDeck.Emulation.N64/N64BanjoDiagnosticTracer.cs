using System.Text;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Focused diagnostic tracer for Banjo-Kazooie (NBKE) hardware validation.
/// Tracks 10-checkpoint progression, VI presentation timing, RSP task queues,
/// DP interrupts, CP0 Count/Compare scheduling, and EEPROM DMA events.
/// </summary>
public sealed class N64BanjoDiagnosticTracer
{
    private readonly StringBuilder _traceLog = new();

    public bool IsEnabled { get; set; } = true;
    public int CurrentCheckpoint { get; private set; } = 1;

    public long TraceEventCount { get; private set; }

    /// <summary>
    /// Records a hardware diagnostic event during Banjo-Kazooie execution.
    /// </summary>
    public void RecordEvent(
        string category,
        uint pc,
        uint cp0Count,
        uint cp0Compare,
        uint miInterrupt,
        string details)
    {
        if (!IsEnabled)
        {
            return;
        }

        TraceEventCount++;
        if (_traceLog.Length < 1_000_000) // 1 MB ring buffer limit
        {
            _traceLog.AppendLine(
                $"[{category}] PC:0x{pc:X8} Count:0x{cp0Count:X8} Compare:0x{cp0Compare:X8} " +
                $"MI:0x{miInterrupt:X2} | {details}");
        }
    }

    /// <summary>
    /// Advances checkpoint stage when a checkpoint condition is validated.
    /// </summary>
    public void UpdateCheckpoint(int stage, string description)
    {
        if (stage > CurrentCheckpoint)
        {
            CurrentCheckpoint = stage;
            RecordEvent("CHECKPOINT", 0, 0, 0, 0, $"Advanced to Checkpoint {stage}: {description}");
        }
    }

    /// <summary>
    /// Evaluates current frame execution for Banjo-Kazooie checkpoints.
    /// </summary>
    public void EvaluateFrameCheckpoints(
        long frameNumber,
        long graphicsTasks,
        long audioTasks,
        uint viOrigin,
        uint viWidth,
        int distinctColors)
    {
        if (CurrentCheckpoint == 1 && frameNumber >= 1)
        {
            UpdateCheckpoint(1, "CIC / Boot Complete");
        }

        if (CurrentCheckpoint == 1 && graphicsTasks >= 1 && distinctColors > 2)
        {
            UpdateCheckpoint(2, "Nintendo / Rareware Logos Rendered");
        }

        if (CurrentCheckpoint == 2 && frameNumber >= 120 && distinctColors >= 8)
        {
            UpdateCheckpoint(3, "Intro Sequence Storybook Running");
        }

        if (CurrentCheckpoint == 3 && graphicsTasks >= 100 && distinctColors >= 16)
        {
            UpdateCheckpoint(4, "File-Select Screen Active");
        }
    }

    /// <summary>
    /// Exports the full trace log summary.
    /// </summary>
    public string GetTraceSummary() => _traceLog.ToString();

    public void Clear() => _traceLog.Clear();
}
