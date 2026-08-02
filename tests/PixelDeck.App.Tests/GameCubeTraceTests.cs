using PixelDeck.Emulation.GameCube;

namespace PixelDeck.App.Tests;

public sealed class GameCubeTraceTests
{
    [Fact]
    public void ADisabledChannelNeverFormatsItsMessage()
    {
        // The guarantee the whole design rests on. If a disabled channel still
        // evaluated its arguments, no trace call could survive inside an
        // instruction loop, and the traces that matter are exactly the ones
        // that belong there.
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.Disc));
        var evaluations = 0;

        trace.Write(GameCubeTraceChannel.Cpu, GameCubeTraceLevel.Verbose, $"pc={Count()}");

        Assert.Equal(0, evaluations);
        Assert.Empty(trace.CaptureRecent());

        trace.Write(GameCubeTraceChannel.Disc, GameCubeTraceLevel.Verbose, $"pc={Count()}");

        Assert.Equal(1, evaluations);
        Assert.Single(trace.CaptureRecent());

        int Count() => ++evaluations;
    }

    [Fact]
    public void ALevelBelowTheThresholdIsNotRecorded()
    {
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Warning, GameCubeTraceChannel.All));

        trace.Write(GameCubeTraceChannel.Boot, GameCubeTraceLevel.Error, "kept");
        trace.Write(GameCubeTraceChannel.Boot, GameCubeTraceLevel.Warning, "kept");
        trace.Write(GameCubeTraceChannel.Boot, GameCubeTraceLevel.Information, "dropped");
        trace.Write(GameCubeTraceChannel.Boot, GameCubeTraceLevel.Verbose, "dropped");

        Assert.Equal(["kept", "kept"], trace.CaptureRecent().Select(record => record.Message));
        Assert.Equal(2, trace.KeptCount);
    }

    [Fact]
    public void WriteOnce_ReportsTheFirstOccurrenceAndCountsTheRest()
    {
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All));
        var evaluations = 0;

        for (var index = 0; index < 500; index++)
        {
            trace.WriteOnce(
                GameCubeTraceChannel.Unimplemented,
                GameCubeTraceLevel.Warning,
                "opcode/0x1F",
                $"unimplemented opcode 0x1F at {Count()}");
        }

        var record = Assert.Single(trace.CaptureRecent());
        Assert.Contains("unimplemented opcode 0x1F", record.Message, StringComparison.Ordinal);

        // Only the reported occurrence built its message; the other 499 did
        // not even evaluate their arguments.
        Assert.Equal(1, evaluations);
        Assert.Equal(1, trace.KeptCount);
        Assert.Equal(499, trace.SuppressedCount);

        var counter = Assert.Single(trace.CaptureCounters());
        Assert.Equal("opcode/0x1F", counter.Key);
        Assert.Equal(500, counter.Count);

        int Count() => ++evaluations;
    }

    [Fact]
    public void WriteEvery_ReportsTheFirstOccurrenceAndThenOnEachInterval()
    {
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All));

        for (var frame = 0; frame < 10; frame++)
        {
            trace.WriteEvery(
                GameCubeTraceChannel.Performance,
                GameCubeTraceLevel.Information,
                "frame-sample",
                4,
                $"frame {frame}");
        }

        Assert.Equal(
            ["frame 0", "frame 4", "frame 8"],
            trace.CaptureRecent().Select(record => record.Message));
    }

    [Fact]
    public void WriteCounterSummary_ListsTheMostFrequentKeysFirst()
    {
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All));

        for (var index = 0; index < 3; index++)
        {
            trace.WriteOnce(GameCubeTraceChannel.Unimplemented, GameCubeTraceLevel.Warning, "rare", $"{index}");
        }

        for (var index = 0; index < 40; index++)
        {
            trace.WriteOnce(GameCubeTraceChannel.Unimplemented, GameCubeTraceLevel.Warning, "common", $"{index}");
        }

        trace.WriteCounterSummary();

        var lines = trace.CaptureRecent().Select(record => record.Message).ToArray();
        var summaryIndex = Array.FindIndex(
            lines,
            line => line.StartsWith("trace summary", StringComparison.Ordinal));

        Assert.True(summaryIndex >= 0, "the summary line was not written");
        Assert.Contains("2 distinct keys", lines[summaryIndex], StringComparison.Ordinal);
        Assert.Contains("common", lines[summaryIndex + 1], StringComparison.Ordinal);
        Assert.Contains("rare", lines[summaryIndex + 2], StringComparison.Ordinal);
    }

    [Fact]
    public void BusiestCounter_NamesWhatARunKeepsHitting()
    {
        // What the session panel asks once a second to answer "what is it
        // waiting on", so it must not need the whole table sorted.
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All));

        Assert.Equal(0, trace.BusiestCounter().Count);

        for (var index = 0; index < 7; index++)
        {
            trace.WriteOnce(GameCubeTraceChannel.Cpu, GameCubeTraceLevel.Debug, "quiet", $"{index}");
        }

        for (var index = 0; index < 900; index++)
        {
            trace.WriteOnce(GameCubeTraceChannel.Cpu, GameCubeTraceLevel.Debug, "spinning", $"{index}");
        }

        var busiest = trace.BusiestCounter();

        Assert.Equal("spinning", busiest.Key);
        Assert.Equal(900, busiest.Count);
    }

    [Fact]
    public void BusiestCounter_IgnoresTheEmulatorWatchingItself()
    {
        // A once-a-frame heartbeat outnumbers everything early in a run, so
        // without this the panel answers "what is it waiting on" with
        // PixelDeck's own bookkeeping — which is exactly what it did.
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All));

        for (var index = 0; index < 400; index++)
        {
            trace.WriteEvery(
                GameCubeTraceChannel.Performance,
                GameCubeTraceLevel.Information,
                GameCubeTraceLog.ObserverKeyPrefix + "heartbeat",
                60,
                $"frame {index}");
        }

        trace.WriteOnce(GameCubeTraceChannel.Cpu, GameCubeTraceLevel.Debug, "real", "once");

        var busiest = trace.BusiestCounter();

        Assert.Equal("real", busiest.Key);
        Assert.Equal(1, busiest.Count);
    }

    [Fact]
    public void CaptureRecent_KeepsTheMostRecentRecordsInOrder()
    {
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All),
            recentCapacity: 4);

        for (var index = 0; index < 10; index++)
        {
            trace.Write(GameCubeTraceChannel.Cpu, GameCubeTraceLevel.Debug, $"step {index}");
        }

        Assert.Equal(
            ["step 6", "step 7", "step 8", "step 9"],
            trace.CaptureRecent().Select(record => record.Message));
        Assert.Equal(10, trace.KeptCount);
    }

    [Fact]
    public void RecordsCarryTheFrameTheyBelongTo()
    {
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All))
        {
            Frame = 42
        };

        trace.Write(GameCubeTraceChannel.Video, GameCubeTraceLevel.Information, "field start");

        var record = Assert.Single(trace.CaptureRecent());
        Assert.Equal(42, record.Frame);
        Assert.Contains("f42", record.Format(), StringComparison.Ordinal);
        Assert.Contains("Video", record.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheLevelWhileRunningTakesEffectImmediately()
    {
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Error, GameCubeTraceChannel.All));

        trace.Write(GameCubeTraceChannel.Cpu, GameCubeTraceLevel.Debug, "before");
        trace.Level = GameCubeTraceLevel.Debug;
        trace.Write(GameCubeTraceChannel.Cpu, GameCubeTraceLevel.Debug, "after");

        var record = Assert.Single(trace.CaptureRecent());
        Assert.Equal("after", record.Message);
    }

    [Fact]
    public void FileSink_WritesRecordsThatCanBeReadBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pixelcube-trace-{Guid.NewGuid():N}.log");

        try
        {
            using (var trace = new GameCubeTraceLog(
                       new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All)))
            {
                trace.AddOwnedSink(new GameCubeTraceFileSink(path));
                trace.Write(GameCubeTraceChannel.Boot, GameCubeTraceLevel.Information, "disc opened");
                trace.Write(GameCubeTraceChannel.Boot, GameCubeTraceLevel.Error, "disc exploded");
                trace.Flush();
            }

            var lines = File.ReadAllLines(path);

            Assert.Equal(2, lines.Length);
            Assert.Contains("INFO", lines[0], StringComparison.Ordinal);
            Assert.Contains("disc opened", lines[0], StringComparison.Ordinal);
            Assert.Contains("ERR", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void DelegateSink_ForwardsEveryKeptRecord()
    {
        var forwarded = new List<GameCubeTraceRecord>();
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Information, GameCubeTraceChannel.All));
        trace.AddSink(new GameCubeTraceDelegateSink(forwarded.Add));

        trace.Write(GameCubeTraceChannel.Disc, GameCubeTraceLevel.Information, "kept");
        trace.Write(GameCubeTraceChannel.Disc, GameCubeTraceLevel.Debug, "dropped");

        var record = Assert.Single(forwarded);
        Assert.Equal("kept", record.Message);
    }

    [Theory]
    [InlineData("off", GameCubeTraceLevel.Off, GameCubeTraceChannel.None)]
    [InlineData("info", GameCubeTraceLevel.Information, GameCubeTraceChannel.Default)]
    [InlineData("warn", GameCubeTraceLevel.Warning, GameCubeTraceChannel.Default)]
    [InlineData("verbose:all", GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All)]
    [InlineData(
        "debug:disc,cpu",
        GameCubeTraceLevel.Debug,
        GameCubeTraceChannel.Disc | GameCubeTraceChannel.Cpu)]
    public void Settings_ParseTheSpellingsSomeoneWouldActuallyType(
        string specification,
        GameCubeTraceLevel expectedLevel,
        GameCubeTraceChannel expectedChannels)
    {
        var settings = GameCubeTraceSettings.Parse(specification);

        Assert.Equal(expectedLevel, settings.Level);
        Assert.Equal(expectedChannels, settings.Channels);
    }

    [Theory]
    [InlineData("chatty")]
    [InlineData("debug:nosuchchannel")]
    [InlineData("3")]
    public void Settings_RejectSpecificationsItCannotHonour(string specification)
    {
        Assert.False(GameCubeTraceSettings.TryParse(specification, out _));
        Assert.Throws<FormatException>(() => GameCubeTraceSettings.Parse(specification));
    }

    [Fact]
    public void Settings_FallBackToTheDefaultAndSayWhyWhenTheEnvironmentIsWrong()
    {
        // Silently ignoring a typo would leave someone convinced tracing is on
        // while nothing is being recorded, so the complaint is carried into
        // the log rather than dropped.
        var settings = GameCubeTraceSettings.FromEnvironment("chatty", null);

        Assert.Equal(GameCubeTraceSettings.Default.Level, settings.Level);
        Assert.Equal(GameCubeTraceSettings.Default.Channels, settings.Channels);
        Assert.NotNull(settings.ConfigurationWarning);
        Assert.Contains("chatty", settings.ConfigurationWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_HonourAnExplicitTraceFile()
    {
        var settings = GameCubeTraceSettings.FromEnvironment("debug:disc", "traces/run.log");

        Assert.Equal(GameCubeTraceLevel.Debug, settings.Level);
        Assert.Equal(Path.GetFullPath("traces/run.log"), settings.FilePath);
    }

    [Fact]
    public void UnmappedMemoryIsReportedOnceRatherThanOnEveryAccess()
    {
        using var trace = new GameCubeTraceLog(
            new GameCubeTraceSettings(GameCubeTraceLevel.Verbose, GameCubeTraceChannel.All));
        var memory = new GameCubeMemory(trace);

        // Past the register window entirely: the locked cache, which nothing
        // models and nothing routes.
        for (var index = 0; index < 100; index++)
        {
            memory.ReadUInt32(0xE000_0000);
        }

        var record = Assert.Single(trace.CaptureRecent());
        Assert.Equal(GameCubeTraceChannel.Unimplemented, record.Channel);
        Assert.Contains("locked cache", record.Message, StringComparison.Ordinal);
        Assert.Equal(99, trace.SuppressedCount);
    }

    [Fact]
    public void MainMemoryIsAddressableThroughBothTheCachedAndUncachedWindows()
    {
        using var trace = new GameCubeTraceLog(GameCubeTraceSettings.Disabled);
        var memory = new GameCubeMemory(trace);

        memory.WriteUInt32(GameCubeMemory.CachedBase + 0x1000, 0xDEAD_BEEF);

        Assert.Equal(0xDEAD_BEEFu, memory.ReadUInt32(GameCubeMemory.UncachedBase + 0x1000));
        Assert.Equal("cached main memory", GameCubeMemory.DescribeRegion(GameCubeMemory.CachedBase));
        Assert.Equal("uncached main memory", GameCubeMemory.DescribeRegion(GameCubeMemory.UncachedBase));
    }
}
