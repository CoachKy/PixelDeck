using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace PixelDeck.Launcher;

/// <summary>
/// Loads the replaceable application assembly and hands control to it.
/// </summary>
/// <remarks>
/// The assembly is loaded into the default context on purpose. Its dependencies
/// - Avalonia, NAudio, SDL3-CS, CommunityToolkit - are inside this executable's
/// single-file bundle, and only the default context resolves from there. A
/// custom <see cref="AssemblyLoadContext"/> would isolate the component and then
/// fail to find any of them.
/// </remarks>
internal static class ComponentHost
{
    /// <summary>The type and method the application must expose.</summary>
    private const string EntryTypeName = "PixelDeck.App.Entry";
    private const string EntryMethodName = "Run";

    /// <summary>
    /// Teaches the default load context to find sibling components.
    /// </summary>
    /// <remarks>
    /// Loading PixelDeck.App.dll by path does not make the folder it came from a
    /// probing path. Its third-party dependencies still resolve, because those
    /// live in this executable's bundle, but its references to the emulation
    /// cores do not: nothing tells the runtime to look in Components. Without
    /// this hook the application loads and then fails partway through building
    /// its first window.
    ///
    /// Restricted to PixelDeck's own assemblies on purpose. A resolver that
    /// answered for any name would let a file dropped into Components shadow a
    /// framework assembly.
    /// </remarks>
    private static void RegisterComponentResolver()
    {
        AssemblyLoadContext.Default.Resolving += static (context, requested) =>
        {
            var name = requested.Name;
            if (name is null || !name.StartsWith("PixelDeck.", StringComparison.Ordinal))
            {
                return null;
            }

            var candidate = Path.Combine(LauncherPaths.ComponentFolder, name + ".dll");
            if (!File.Exists(candidate))
            {
                return null;
            }

            LauncherLog.Write($"Resolved component {name} from Components.");
            return context.LoadFromAssemblyPath(candidate);
        };
    }

    public static int Run(string[] args)
    {
        RegisterComponentResolver();

        var assemblyPath = LauncherPaths.ApplicationAssembly;
        if (!File.Exists(assemblyPath))
        {
            Fatal(
                "PixelDeck cannot start because part of the installation is missing.",
                $"Expected to find:\n{assemblyPath}\n\n" +
                "Re-extract the PixelDeck download over this folder to repair it.");
            return 2;
        }

        Assembly assembly;
        try
        {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
        {
            LauncherLog.Write("The application assembly could not be loaded.", exception);
            Fatal(
                "PixelDeck cannot start because part of the installation is damaged.",
                "Re-extract the PixelDeck download over this folder to repair it.");
            return 3;
        }

        var version = assembly.GetName().Version;
        LauncherLog.Write($"Application component version {version}.");

        var entry = assembly.GetType(EntryTypeName);
        var run = entry?.GetMethod(EntryMethodName, BindingFlags.Public | BindingFlags.Static);
        if (run is null)
        {
            // A component from a different architecture generation: it loaded,
            // but it does not speak this launcher's entry contract.
            LauncherLog.Write($"{EntryTypeName}.{EntryMethodName} not found in component {version}.");
            Fatal(
                "This version of PixelDeck is not compatible with its launcher.",
                "Install the complete PixelDeck download rather than updating in place.");
            return 4;
        }

        return (int)run.Invoke(null, [args])!;
    }

    /// <summary>
    /// Reports a startup failure the player can act on.
    /// </summary>
    /// <remarks>
    /// Uses the Win32 dialog directly rather than Avalonia: the failures this
    /// reports include "the UI assembly would not load", so it must not depend on
    /// anything the application supplies.
    /// </remarks>
    private static void Fatal(string headline, string detail)
    {
        LauncherLog.Write($"FATAL: {headline} {detail.Replace(Environment.NewLine, " ")}");

        if (OperatingSystem.IsWindows())
        {
            // 0x10 = MB_ICONERROR. The return value says which button was
            // pressed, which is not interesting for a single-button dialog.
            _ = MessageBoxW(IntPtr.Zero, $"{headline}\n\n{detail}", "PixelDeck", 0x10);
            return;
        }

        Console.Error.WriteLine(headline);
        Console.Error.WriteLine(detail);
    }

    // DllImport rather than LibraryImport: the source-generated form requires
    // AllowUnsafeBlocks, and the launcher is the last place worth enabling that
    // for a single message box.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
