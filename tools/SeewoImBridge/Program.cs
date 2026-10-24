using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace SeewoImBridge;

internal static class Program
{
    private const string DefaultSdk = @"C:\Program Files (x86)\Seewo\EasiNote5\EasiNote5_5.2.4.10148\Main";

    public static int Main(string[] args)
    {
        // Keep a private writer for IPC before redirecting SDK/Console output to stderr.
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var ipc = new IpcOutput(output);
        try
        {
            string sdk = DefaultSdk;
            bool probe = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--probe") probe = true;
                else if (args[i] == "--sdk" && ++i < args.Length) sdk = args[i];
                else throw new ArgumentException("Usage: SeewoImBridge [--sdk <EasiNote Main directory>] [--probe]");
            }
            if (!OperatingSystem.IsWindows() || Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("The bridge requires Windows and the x86 .NET 10 runtime.");

            Console.SetOut(Console.Error);
            if (!SetStdHandle(-11, GetStdHandle(-12))) throw new Win32Exception();
            ConfigureSdk(Path.GetFullPath(sdk));
            return RunSdk(ipc, probe);
        }
        catch (Exception ex)
        {
            // Exception messages from SDK calls may contain auth/JSON. Never echo them.
            ipc.Error($"Bridge startup failed ({ex.GetType().Name}, 0x{ex.HResult:X8}). Check --sdk, x86 runtime and installed SDK dependencies.");
            return 1;
        }
    }

    private static void ConfigureSdk(string sdk)
    {
        string wrapperDirectory = Path.Combine(sdk, "runtimes", "win-x86", "lib", "net5.0");
        string wrapper = Path.Combine(wrapperDirectory, "IMWrapper.dll");
        if (!File.Exists(wrapper)) throw new FileNotFoundException("Installed wrapper missing.");
        if (!SetDefaultDllDirectories(0x00001000)) throw new Win32Exception(); // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS
        foreach (string directory in new[] { wrapperDirectory, Path.Combine(sdk, "runtimes", "win-x86", "native"), sdk })
            if (Directory.Exists(directory) && AddDllDirectory(directory) == IntPtr.Zero) throw new Win32Exception();

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            // Resolve only installed SDK assemblies, never the working directory or PATH.
            if (name.Name is null || Path.GetFileName(name.Name) != name.Name) return null;
            foreach (string directory in new[] { wrapperDirectory, sdk })
            {
                string file = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
            }
            return null;
        };
        AssemblyLoadContext.Default.LoadFromAssemblyPath(wrapper);
    }

    // The wrapper must not be JIT-resolved until ConfigureSdk installs the resolver.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunSdk(IpcOutput ipc, bool probe) => Bridge.Run(ipc, probe);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string directory);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetStdHandle(int handle, IntPtr value);
    [DllImport("kernel32")]
    private static extern IntPtr GetStdHandle(int handle);
}

internal sealed class IpcOutput(TextWriter writer)
{
    private readonly object gate = new();

    public void Write(object value)
    {
        lock (gate)
        {
            try { writer.WriteLine(JsonSerializer.Serialize(value)); }
            catch (IOException) { Environment.Exit(0); }
        }
    }

    public void Error(string message) => Write(new { type = "error", message });
}
