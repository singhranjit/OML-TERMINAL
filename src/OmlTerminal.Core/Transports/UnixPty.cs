using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OmlTerminal.Core.Transports;

/// <summary>
/// A pseudo-terminal on Linux and macOS: opens a pty pair, then starts the shell with posix_spawn in a new session
/// whose controlling terminal is the pty's slave side - so job control, Ctrl+C and full-screen programs (vim, top)
/// behave as in any terminal. posix_spawn rather than fork(): forking a multi-threaded .NET process is unsafe.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixPty : IDisposable
{
    private readonly int _master;
    private readonly FileStream _stream;
    public int Pid { get; }

    private UnixPty(int master, int pid)
    {
        _master = master;
        Pid = pid;
        _stream = new FileStream(new SafeFileHandle((IntPtr)master, ownsHandle: true), FileAccess.ReadWrite, 1, isAsync: false);
    }

    public Stream Stream => _stream;

    public static UnixPty Start(string file, IReadOnlyList<string> args, string? workingDirectory,
        IReadOnlyDictionary<string, string> environment, int cols, int rows)
    {
        bool mac = OperatingSystem.IsMacOS();
        int master = Native.posix_openpt(Native.O_RDWR | Native.O_NOCTTY);
        if (master < 0) throw new IOException($"posix_openpt failed (errno {Marshal.GetLastPInvokeError()}).");
        IntPtr fileActions = IntPtr.Zero, attr = IntPtr.Zero;
        try
        {
            if (Native.grantpt(master) != 0 || Native.unlockpt(master) != 0)
                throw new IOException($"grantpt/unlockpt failed (errno {Marshal.GetLastPInvokeError()}).");
            var slaveName = Native.SlaveName(master);
            Resize(master, cols, rows);

            // Opaque libc structs: glibc's are 80 and 336 bytes, macOS's are a pointer. 1 KB is plenty for either.
            fileActions = Marshal.AllocHGlobal(1024);
            attr = Marshal.AllocHGlobal(1024);
            Check(Native.posix_spawn_file_actions_init(fileActions), "file_actions_init");
            Check(Native.posix_spawnattr_init(attr), "spawnattr_init");
            // New session first (posix_spawn applies SETSID before the file actions), then opening the slave as
            // stdin makes it the controlling terminal. The master must not leak into the shell.
            Check(Native.posix_spawnattr_setflags(attr, (short)(mac ? Native.POSIX_SPAWN_SETSID_MAC : Native.POSIX_SPAWN_SETSID_LINUX)), "setflags");
            Check(Native.posix_spawn_file_actions_addopen(fileActions, 0, slaveName, Native.O_RDWR, 0), "addopen");
            Check(Native.posix_spawn_file_actions_adddup2(fileActions, 0, 1), "dup2 stdout");
            Check(Native.posix_spawn_file_actions_adddup2(fileActions, 0, 2), "dup2 stderr");
            Check(Native.posix_spawn_file_actions_addclose(fileActions, master), "close master");
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                try { Native.posix_spawn_file_actions_addchdir_np(fileActions, workingDirectory); }
                catch (EntryPointNotFoundException) { } // very old libc: start in the app's directory instead
            }

            var argv = ToNative([file, .. args]);
            var envp = ToNative(environment.Select(kv => $"{kv.Key}={kv.Value}").ToList());
            try
            {
                int rc = Native.posix_spawnp(out int pid, file, fileActions, attr, argv, envp);
                if (rc != 0) throw new IOException($"Could not start '{file}' (errno {rc}).");
                return new UnixPty(master, pid);
            }
            finally { FreeNative(argv); FreeNative(envp); }
        }
        catch
        {
            Native.close(master);
            throw;
        }
        finally
        {
            if (fileActions != IntPtr.Zero) { Native.posix_spawn_file_actions_destroy(fileActions); Marshal.FreeHGlobal(fileActions); }
            if (attr != IntPtr.Zero) { Native.posix_spawnattr_destroy(attr); Marshal.FreeHGlobal(attr); }
        }
    }

    public void Resize(int cols, int rows) => Resize(_master, cols, rows);

    private static void Resize(int fd, int cols, int rows)
    {
        var ws = new Native.WinSize { Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue), Cols = (ushort)Math.Clamp(cols, 1, ushort.MaxValue) };
        Native.ioctl(fd, OperatingSystem.IsMacOS() ? Native.TIOCSWINSZ_MAC : Native.TIOCSWINSZ_LINUX, ref ws);
    }

    /// <summary>Hang up the shell (as closing a terminal window does), escalating if it ignores that, and reap it.</summary>
    public void Kill()
    {
        Native.kill(Pid, Native.SIGHUP);
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                if (Native.waitpid(Pid, out _, Native.WNOHANG) != 0) return;
                await Task.Delay(100).ConfigureAwait(false);
            }
            Native.kill(Pid, Native.SIGKILL);
            Native.waitpid(Pid, out _, 0);
        });
    }

    /// <summary>Waits (blocking) for the shell to exit and returns its exit code - call from a background thread.</summary>
    public int WaitForExit()
    {
        int r = Native.waitpid(Pid, out int status, 0);
        return r == Pid && (status & 0x7f) == 0 ? (status >> 8) & 0xff : -1;
    }

    public void Dispose() => _stream.Dispose();

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new IOException($"posix_spawn setup failed at {what} (errno {rc}).");
    }

    private static IntPtr ToNative(IReadOnlyList<string> items)
    {
        var arr = Marshal.AllocHGlobal(IntPtr.Size * (items.Count + 1));
        for (int i = 0; i < items.Count; i++) Marshal.WriteIntPtr(arr, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(items[i]));
        Marshal.WriteIntPtr(arr, items.Count * IntPtr.Size, IntPtr.Zero);
        return arr;
    }

    private static void FreeNative(IntPtr arr)
    {
        for (int i = 0; ; i++)
        {
            var p = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
            if (p == IntPtr.Zero) break;
            Marshal.FreeCoTaskMem(p);
        }
        Marshal.FreeHGlobal(arr);
    }

    private static class Native
    {
        private const string Libc = "libc";
        public const int O_RDWR = 2;
        public static int O_NOCTTY => OperatingSystem.IsMacOS() ? 0x20000 : 0x100;
        public const int POSIX_SPAWN_SETSID_LINUX = 0x80, POSIX_SPAWN_SETSID_MAC = 0x400;
        public const ulong TIOCSWINSZ_LINUX = 0x5414, TIOCSWINSZ_MAC = 0x80087467;
        public const int SIGHUP = 1, SIGKILL = 9, WNOHANG = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct WinSize { public ushort Rows, Cols, XPixel, YPixel; }

        [DllImport(Libc, SetLastError = true)] public static extern int posix_openpt(int flags);
        [DllImport(Libc, SetLastError = true)] public static extern int grantpt(int fd);
        [DllImport(Libc, SetLastError = true)] public static extern int unlockpt(int fd);
        [DllImport(Libc, SetLastError = true)] private static extern IntPtr ptsname(int fd);
        [DllImport(Libc, SetLastError = true)] public static extern int close(int fd);
        [DllImport(Libc, SetLastError = true)] public static extern int ioctl(int fd, ulong request, ref WinSize ws);
        [DllImport(Libc, SetLastError = true)] public static extern int kill(int pid, int sig);
        [DllImport(Libc, SetLastError = true)] public static extern int waitpid(int pid, out int status, int options);
        [DllImport(Libc)] public static extern int posix_spawn_file_actions_init(IntPtr fa);
        [DllImport(Libc)] public static extern int posix_spawn_file_actions_destroy(IntPtr fa);
        [DllImport(Libc)] public static extern int posix_spawn_file_actions_addopen(IntPtr fa, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int oflag, int mode);
        [DllImport(Libc)] public static extern int posix_spawn_file_actions_adddup2(IntPtr fa, int fd, int newfd);
        [DllImport(Libc)] public static extern int posix_spawn_file_actions_addclose(IntPtr fa, int fd);
        [DllImport(Libc)] public static extern int posix_spawn_file_actions_addchdir_np(IntPtr fa, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Libc)] public static extern int posix_spawnattr_init(IntPtr attr);
        [DllImport(Libc)] public static extern int posix_spawnattr_destroy(IntPtr attr);
        [DllImport(Libc)] public static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
        [DllImport(Libc)] public static extern int posix_spawnp(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, IntPtr fa, IntPtr attr, IntPtr argv, IntPtr envp);

        /// <summary>The slave device path (/dev/pts/N on Linux, /dev/ttysNNN on macOS). Called before any other
        /// thread could open a pty, so ptsname's static buffer is safe here.</summary>
        public static string SlaveName(int master)
        {
            var p = ptsname(master);
            if (p == IntPtr.Zero) throw new IOException($"ptsname failed (errno {Marshal.GetLastPInvokeError()}).");
            return Marshal.PtrToStringUTF8(p)!;
        }
    }
}
