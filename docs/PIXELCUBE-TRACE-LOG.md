# PixelCube trace log

PixelCube is PixelDeck's GameCube core. It is scaffolding today: it reads
discs and places a boot image in memory, and there is no Gekko interpreter
behind it yet. The finished part is the trace log, because everything that
comes next is going to be debugged through it.

## Why this was built first

A GameCube fails in ways that all look the same from the outside. An
unimplemented opcode, a bad DVD read, a missing interrupt and a framebuffer
pointed at the wrong address all produce one black screen and no explanation.
Pixel64 spent a long time being debugged by inference — reading frame rates to
guess which renderer was live, adding a `Debug.WriteLine`, rebuilding, and
running the game again. The trace log exists so that loop does not happen a
fourth time.

## Turning it on

Two environment variables, read once when the log is created. Nothing needs
rebuilding.

```
PIXELCUBE_TRACE=<level>[:<channel>[,<channel>...]]
PIXELCUBE_TRACE_FILE=<path>
```

Levels, shortest spelling first: `off`, `err`/`error`, `warn`/`warning`,
`info`/`information`, `dbg`/`debug`, `verb`/`verbose`.

Channels: `boot`, `disc`, `executable`, `memory`, `registers`, `cpu`,
`interrupts`, `dsp`, `graphics`, `video`, `audio`, `input`, `storage`,
`unimplemented`, `performance`, and `all`.

Omitting the channel list selects the default set — `boot`, `disc`,
`executable`, `interrupts`, `storage`, `unimplemented` and `performance` — which
is what a normal session records: what was decided at startup, what went wrong,
and what is still missing. The per-instruction and per-access channels are off
unless asked for.

```powershell
# Everything about disc parsing and the boot image.
$env:PIXELCUBE_TRACE = "debug:disc,executable,boot"

# Everything, into a file of its own.
$env:PIXELCUBE_TRACE = "verbose:all"
$env:PIXELCUBE_TRACE_FILE = "C:\traces\sunshine.log"
```

Unrecognised settings do not stop a game from starting. They fall back to the
default and say so on the log's first line, because a typo that silently
disabled tracing would be worse than one that is loud about it.

## Where records go

- `%LOCALAPPDATA%\PixelDeck\pixelcube-trace.log` — everything that passes the
  filter, written from a background thread. Trimmed by dropping its oldest half
  once it passes 8 MB, so the startup lines survive a long session.
- `%LOCALAPPDATA%\PixelDeck\emulator.log` — a copy of the `information` level
  and above only, alongside the other cores' diagnostics. Debug and verbose
  records stay out of it.
- The last 1024 records stay in memory whatever the sinks did, reachable
  through `GameCubeTraceLog.CaptureRecent()`. That is the answer to "what
  happened immediately before the freeze" when nothing reached disk.

## Writing traces

```csharp
trace.Write(GameCubeTraceChannel.Video, GameCubeTraceLevel.Debug,
    $"vi mode={mode} origin=0x{origin:X8}");
```

An interpolated message is **not** built unless the log has already agreed to
keep it. A disabled channel costs one bit test and no allocation, which is what
makes it safe to leave these calls inside an instruction or memory-access path.

For anything that repeats, give it a key:

```csharp
trace.WriteOnce(GameCubeTraceChannel.Unimplemented, GameCubeTraceLevel.Warning,
    "opcode/0x1F",
    $"unimplemented opcode 0x{opcode:X2} at 0x{pc:X8}");
```

The first occurrence is reported and the rest are counted — and the decision is
made *before* the message is formatted, so an opcode hit four million times
costs four million dictionary lookups, not four million discarded strings.
`WriteEvery(..., key, interval, ...)` is the same idea for samples worth
watching over time.

Closing a machine writes the tally, so a run ends with the list of what it kept
hitting:

```
trace summary: 14 distinct keys, 312 records kept, 4,181,663 repeats collapsed
     3,904,112  opcode/0x1F
       201,553  memory/read/hardware registers
```

That list is the work queue for whatever gets implemented next.

## What the scaffold covers today

| Area | State |
| --- | --- |
| Trace log, channels, sinks, suppression | Complete |
| `.iso` / `.gcm` disc images | Complete |
| `.ciso` containers | Complete, including absent zero blocks |
| `.rvz` containers (GameCube, Zstandard) | Complete |
| `.gcz` / `.wia` containers | Refused by name, not readable |
| Disc header, apploader header | Complete |
| File table (FST) with nested paths | Complete |
| DOL executable section table | Complete |
| Main memory, ARAM, cached/uncached windows | Skeleton; unmapped accesses are reported |
| Gekko integer, branch and load/store core | Complete |
| Gekko scalar floating point and FPSCR | Complete |
| Paired singles and the quantised load/store unit | Complete |
| DSP boot handshake (ROM and microcode announcements) | Complete |
| Disassembler, including instructions that cannot run | Complete |
| OS low-memory globals and initial CPU state | Complete |
| DSP reset, EXI/SI/DI transfer completion, ARAM DMA | Modelled enough to boot past |
| DSP microcode past the boot handshake | Absent |
| ARAM mode/status bits | Absent — the current wall |
| Graphics, video, audio, input, save states | Absent |

## Launching a disc

A readable disc starts from the dashboard, badged `PARTIAL`. Launching is how a
trace gets produced, so a disc that could not be started could not be
investigated — but nothing executes. `Boot()` does what the apploader would do
(places the DOL's sections, clears the BSS, reports the entry point) and stops
there, and the session then idles at the disc's field rate so the trace has a
clock.

Because there is no video hardware, the window shows a session panel instead of
a picture: the disc's identity, the boot image, the live frame counter, the
trace level and channels in force, and the path to the trace file. A blank
screen would be indistinguishable from a hang; the panel is not. Save states
and library images are disabled for a GameCube session, because there is no
execution state to keep and no image to capture.

An unreadable disc — an RVZ container, a Wii disc, a truncated file — stays
`UNSUPPORTED` and says why on its card, since there would be nothing to trace.

## Verified against a real disc

Super Mario Sunshine (USA), stored as a 1.13 GB CISO, expanded and parsed:

```
disc image: container=CISO stored=1,180,729,920 bytes expanded=1,461,714,944 bytes
disc header: id=GMSE01 region=NTSC-U disc=0 revision=0 title="Super Mario Sunshine"
disc layout: dol=0x0001E000 fst=0x0040E100+0x1140 user=0x803FEEC0+0x410000
apploader: date=2002/04/10 entry=0x81200268 size=5588 trailer=107888
file system: 181 entries, 174 files
dol: 10 sections, 4,128,672 bytes, bss=0x803E9700+0x25498 entry=0x8000522C
```

The DOL ends at 0x40E060 and the FST begins at 0x40E100, which is the internal
consistency check worth having: the two offsets were read independently and
they meet. The first instruction at the entry point is `0x48000139`, a `bl`,
which is what a PowerPC `__start` begins with.

## The harness

Working out what to implement next should not require the dashboard. The
`cubetrace` tool runs a disc from the command line and ends with the list:

```
dotnet run --project tools/PixelDeck.CubeTrace -c Release -- <disc> [options]

  --instructions <n>   How far to run (default 2,000,000).
  --survey             Skip unimplemented instructions instead of stopping.
  --trace <spec>       Level and channels, e.g. "debug:disc,cpu".
  --disassemble <n>    Print n instructions from the entry point.
  --files              List the disc's file table and stop.
```

`--survey` deserves its warning. Skipping an instruction means the state after
it is invented, so a survey run is a plan, never a verification. What it buys
is the whole ranked list from one run instead of one obstacle at a time.

## What the first run found

Super Mario Sunshine, stopping at the first thing PixelCube cannot do:

```
outcome       : Unimplemented
instructions  : 47,251
stopped at    : 0x80341B14
instruction   : FFA0004C  mtfsb1 29
```

47,251 instructions of retail `__start`, `__init_registers`, `__init_data` and
`OSInit` executed before an FPSCR write stopped it — `mtfsb1 29` sets
non-IEEE mode, which is the first thing a GameCube title does to its floating
point unit.

Surveying past it changed the picture completely:

```
trace summary: 34 distinct keys, 42 records kept, 978,850 repeats collapsed
       978,834  register/read/DSP / ARAM / audio DMA+0x00A
             5  register/write/EXI external interface+0x000
             3  register/write/DSP / ARAM / audio DMA+0x00A
             3  register/write/PI processor interface+0x004
             2  register/read/DI DVD interface+0x024
             1  gekko/opcode/17/0
             1  gekko/opcode/63/38
```

`DSP+0x00A` is `0xCC00500A`, the DSP control and status register. The game
writes it three times and then reads it 978,834 times: `DSPInit` waiting for a
handshake from hardware that does not exist. A third of every instruction in
the run is that one loop.

This is the result that justifies the whole approach. The floating point
instruction that *stops* the run is a single occurrence and would take an hour
to implement; the register that actually *blocks* the boot is invisible until
something counts it. Guessing would have started with floating point and been
right about the order and wrong about the priority.

## Where it got to

Both were built. The wall moved three times, and each move was found the same
way — by count, not by guess:

| After | Instructions | What blocked it | Reads |
| --- | --- | --- | --- |
| Integer core | 47,251 | `mtfsb1` (an instruction) | 1 |
| Floating point | — | DSP reset handshake `0xCC00500A` | 978,834 |
| DSP reset | — | EXI transfer start `0xCC00680C` | 3,323,071 |
| EXI completion | 50,000,000+ | DSP mailbox `0xCC005004` | 12,483,566 |

Fifty million instructions now execute with **no unimplemented instruction at
all** — the scalar floating point set covered the entire startup sequence, and
nothing has yet reached a paired single.

## The lesson that cost a rebuild

Marking a register "modelled" removed it from the unimplemented list, and with
it any count of how often it was touched. The DSP reset fix worked, the wall
moved to a register I had just declared handled, and the tally went quiet about
it — the second spin loop was invisible in a way the first never was.

`GameCubeHardware` now counts *every* register access, modelled or not, and
`Registers` is in the default channel set for that reason. **Modelled is not
correct.** A handshake that answers the wrong thing produces exactly the spin
loop an unhandled one does, and only a count tells them apart.

## RVZ, and two silent mistakes

RVZ is Dolphin's default format, so "convert your library" was never a real
answer. It needs Zstandard, which .NET does not ship — hence the one package
reference in `Directory.Build.props`, pinned there because the launcher must
reference it too or the assembly never reaches a release.

Two things about the format fail *quietly*, and both cost a debugging round:

**The disc header is not in a group.** RVZ keeps the first 0x80 bytes verbatim
in its own header, and the first raw data entry still reports a start of 0x80
even though its groups tile from zero. Trusting that offset shifts every group
by 0x80: the disc header reads perfectly and every field past it is nonsense —
a failure that looks like a corrupt image rather than a reader bug.

**A junk run carries 68 bytes of seed, not 4.** Get that wrong and the packed
stream desynchronises after the first junk run, so a group whose real data
comes first decodes perfectly and one whose padding comes first decodes to
nothing. It presented as a disc whose header and DOL were correct and whose
file table was empty.

Both were found by decoding the actual bytes rather than by reasoning about the
format, and both now have a test that fails if they regress. Metroid Prime
reads out of RVZ with 178 files, and its `default.dol` sits at `0x073338E0` —
the same offset the disc header independently reports.

## The DSP handshake, and paired singles

Two more walls fell, and the pattern held: each was named by a count, and each
fix moved execution by orders of magnitude.

The DSP mailbox turned out to be a handshake in two halves, both documented
hardware behaviour rather than anything invented. Resetting the DSP makes its
boot ROM announce itself with `0x8071FEED` — a value whose top bit *is* the
mailbox's "mail waiting" flag, which is what the CPU's poll tests. Then
clearing the init bit in the control register hands over to the microcode the
ROM loads out of ARAM, which announces itself with `0x80544348`. Emulating only
the first half is worse than emulating neither: the CPU takes the greeting and
waits forever for the second.

That alone took `DSP+0x004` from 24,421,066 reads to **three**, and execution
from 570,000 instructions to 6.6 million — where it stopped on `psq_st`, a
paired-single store. Paired singles are the part of Gekko that is not a stock
PowerPC 750: a second slot on every floating point register, and a quantised
load/store unit that converts to and from scaled 8- and 16-bit integers on the
way to memory, with the type and scale chosen by one of eight graphics
quantisation registers named in the instruction.

With both in, Super Mario Sunshine runs **1.5 billion instructions** with no
unimplemented instruction and no register spin at all.

## What "no spin" made visible

At 200 million instructions the tally showed **44 collapsed repeats in total** —
the busiest key hit eleven times. Nothing was polling anything. But the program
counter sat around `0x803436B0`, which the harness disassembled to:

```
803436AC  dcbf r0, r3
803436B0  addi r3, r3, 32
803436B4  bdnz 0x803436AC
```

A cache flush walking 32 bytes at a time — not a hang, just a very long loop,
and it does exit. That distinction only exists because the counters were quiet:
a spin and a slow loop look identical from a frame counter, and the tally is
what separates them.

## The one that was worth looking up

The next wall was `0xCC005016`, polled 365,125,555 times, and the trace showed
the game never writes it — so it was waiting on hardware, not reading back its
own configuration.

It had exactly the shape of the bits already modelled: something hardware sets
when it is ready. Setting it would have worked. But the documentation says it
is `DSP_AR_MODE`, bit 0 is `ARAM_NORM` — *"the ARAM Controller sets this flag
after it has finished initializing"* — and the **upper bits of that same
register are a mode the CPU writes**. Guessing would have produced a register
that overwrites the game's own configuration on every read, which is the kind
of fault that surfaces hours later somewhere unrelated.

One search. It is always cheaper than the alternative.

## Where that leaves it

Past ARAM, Super Mario Sunshine writes to the **GX command FIFO** — it is
trying to render. It also spends about 57 million writes and 35 million reads
on a wild pointer:

```
80346760  lwz  r3, 760(r30)     ; r30 = 0x37000000
8034676C  stw  r28, 756(r30)
```

A linked-list insertion into a queue at structure offsets `0x2F4`/`0x2F8` —
the operating system's thread scheduler, enqueuing and never getting anywhere.

## The DVD drive, and a prediction that was wrong

The drive was stubbed to report transfers finished without moving anything, so
every file a game read came back as zeros. That looked like the obvious cause
of the wild pointer, and implementing it properly was cheap — the disc reader
already existed, and the registers are a command word, a disc offset in units
of four bytes, a destination and a length.

It works, and it changed nothing:

```
DI+0x008 write32 = 0x12000000
DVD read: disc=0x00000000 length=0x20 -> 0x80402580
```

That disc-identifier read is the **only** DVD command Super Mario Sunshine
issues in four hundred million instructions. It never asks for a file, so
unloaded data was never the problem, and the tallies afterwards came back
byte-for-byte identical to before. The drive is right now and it was worth
doing; the theory attached to it was not.

That is the whole argument for counting rather than reasoning. The prediction
was plausible, cheap to act on, and wrong, and the only reason that is a
footnote instead of a week is that the tally said so immediately.

## Interrupts, and three bugs they uncovered

The interrupt subsystem went in: the processor interface's cause and mask
registers, four programmable display interrupts fired against a video clock
derived from core cycles, the external interrupt at `0x500`, and the
decrementer at `0x900` ticking once per twelve instructions to match the bus
clock against the core. It works — a decrementer exception is delivered with
the right saved address and state within the first second of a run.

It also did not fix anything, which turned out to be the useful part. Three
separate faults were hiding behind the one symptom, and all three were mine.

**The arena started at zero.** The boot state left the low water mark of free
memory at address zero, so the operating system handed out allocations from the
bottom of memory. The game then wrote its own structures over the low-memory
globals — including `__OSCurrentThread` — and the scheduler spent fifty-seven
million writes following a pointer to `0x37000000`. A watchpoint on that global
named the instruction that clobbered it in one run. Setting the arena past the
executable's last section took the tally from **92,237,282 collapsed repeats to
42**.

**BSS was cleared after the sections were loaded.** A DOL's declared BSS range
routinely overlaps sections the linker put there on purpose — Sunshine's
`data6` sits squarely inside it — so clearing afterwards erased loaded code and
data. Clearing first and placing sections over it took Metroid Prime from
569,274 instructions to **6,621,290**.

**The interrupt cause register was destroyed by any write to it.** It is
acknowledge-by-writing-ones, and the generic register store ran before the
acknowledge logic could read what was there, so a handler clearing one device's
cause silently cleared every device's. Found by a test, not by a game.

## Where both games now stand

Super Mario Sunshine and Metroid Prime now fail *identically*, within nine
thousand instructions of each other, at around 6.6 million: a branch through a
count register holding zero, landing on address zero, with the stack pointer
also zero. Two unrelated games converging on one failure is a statement about
PixelCube rather than about either game.

## Next

PixelCube has **no interrupt delivery of any kind**. No processor-interface
cause and mask registers, no external interrupt at vector `0x80000500`, no
decrementer, no vertical blank. Every one of those is something the GameCube's
operating system builds on: threads sleep waiting to be woken, alarms fire off
the decrementer, and `VIWaitForRetrace` blocks until the video interface says a
field has finished.

A scheduler with nothing to wake it is exactly what that spinning enqueue looks
like. That is the next subsystem, and it is also the one that leads to a
framebuffer — the video interface is where a picture would eventually come
from. This is not another stub: the game has
uploaded microcode and is waiting for the DSP to reply, so answering honestly
means either a DSP interpreter or an HLE layer that recognises what the
microcode was asked to do. Faking a reply would let the game believe its audio
system is running, and every trace after that would describe a machine that
does not exist — the precise failure the counters exist to catch.

Paired singles are the other known gap, and will appear the moment anything
past startup runs.
