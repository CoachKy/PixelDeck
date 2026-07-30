using System;

namespace PixelDeck.App;

/// <summary>
/// Development entry point, used when this project is run directly.
/// </summary>
/// <remarks>
/// A release starts at PixelDeck.exe, which loads this assembly and calls
/// <see cref="Entry.Run"/>. Running this project instead skips the launcher and
/// therefore skips update installation and launcher-level crash logging, which
/// is what you want while working on the emulator: the debugger attaches
/// straight to the application and there is no indirection in the way.
///
/// Both paths funnel into the same method, so nothing can behave differently
/// here than it does in a release.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => Entry.Run(args);
}
