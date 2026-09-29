using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace OmlTerminal.Core.Transports;

/// <summary>Local shell (PowerShell/cmd.exe) as a terminal session via the Windows ConPTY pseudo-console API -
/// the same mechanism Windows Terminal itself uses. No network involved: ConnectAsync spawns the process
/// directly, sized to the real terminal grid from the start. Follows the reference implementation at
/// https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session.</summary>
public sealed class LocalTransport : ITerminalTransport
{
    private readonly string _shellPath;
    private readonly string _arguments;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    private AnonymousPipeServerStream? _inputPipe, _outputPipe;
    private IntPtr _pseudoConsole = IntPtr.Zero;
    private IntPtr _attributeList = IntPtr.Zero;
    private IntPtr _processHandle = IntPtr.Zero;
    private int _closing, _raised;

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;

    /// <param name="arguments">Appended verbatim after the quoted executable path.</param>
    /// <param name="environment">Variables layered over this process's own environment for the child only.</param>
    public LocalTransport(string? shellPath = null, string? workingDirectory = null, string? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _shellPath = string.IsNullOrWhiteSpace(shellPath) ? DefaultShell() : shellPath.Trim().Trim('"');
        _arguments = arguments?.Trim() ?? "";
        _environment = environment;
        // A null current directory means "inherit the app's own", which for an installed WinUI app is not
        // where an interactive shell should land - default to the user's profile instead, like a real terminal.
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : workingDirectory;
    }

    /// <summary>PowerShell if present (the modern default on Windows 10/11), otherwise cmd.exe.</summary>
    public static string DefaultShell()
    {
        var pwsh = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe");
        return File.Exists(pwsh) ? pwsh : Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe");
    }

    public Task ConnectAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local shell sessions need Windows (ConPTY).");

        // Nothing upstream (TerminalSession.ConnectAsync, TerminalTabViewModel.ConnectAsync) disposes this
        // transport if this method throws - so a failure partway through must clean up after itself, or the
        // pseudo console (and its backing conhost.exe process), the attribute list, and the pipes all leak.
        try
        {
            _inputPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            _outputPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

            var size = new ConPty.COORD { X = (short)Math.Max(1, cols), Y = (short)Math.Max(1, rows) };
            int hr = ConPty.CreatePseudoConsole(size, _inputPipe.ClientSafePipeHandle, _outputPipe.ClientSafePipeHandle, 0, out _pseudoConsole);
            if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed (hr=0x{hr:X8}).");

            IntPtr attrListSize = IntPtr.Zero;
            ConPty.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
            _attributeList = Marshal.AllocHGlobal(attrListSize);
            if (!ConPty.InitializeProcThreadAttributeList(_attributeList, 1, 0, ref attrListSize))
                throw new InvalidOperationException("InitializeProcThreadAttributeList failed: " + Marshal.GetLastWin32Error());
            if (!ConPty.UpdateProcThreadAttribute(_attributeList, 0, (IntPtr)ConPty.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("UpdateProcThreadAttribute failed: " + Marshal.GetLastWin32Error());

            var startupInfo = new ConPty.STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<ConPty.STARTUPINFOEX>();
            startupInfo.lpAttributeList = _attributeList;

            var commandLine = new StringBuilder(BuildCommandLine(_shellPath, _arguments));
            if (_environment is not null) _environmentBlock = BuildEnvironmentBlock(_environment);
            bool ok = ConPty.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                ConPty.EXTENDED_STARTUPINFO_PRESENT | (_environment is null ? 0 : ConPty.CREATE_UNICODE_ENVIRONMENT),
                _environmentBlock, _workingDirectory, ref startupInfo, out var processInfo);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Could not start '{_shellPath}' (Win32 error {err}).");
            }
            _processHandle = processInfo.hProcess;
            if (processInfo.hThread != IntPtr.Zero) ConPty.CloseHandle(processInfo.hThread);

            // Once handed to CreatePseudoConsole/CreateProcess, our copies of the client ends are no longer needed -
            // keeping them open would prevent us from ever seeing EOF on a clean exit.
            _inputPipe.DisposeLocalCopyOfClientHandle();
            _outputPipe.DisposeLocalCopyOfClientHandle();
        }
        catch
        {
            // Release, but don't go through Close() here: Close() also raises Closed, and TerminalSession has
            // already wired this transport's Closed event to OnTransportClosed by this point (both for the
            // very first connect and for every reconnect attempt) - raising it now would re-enter
            // OnTransportClosed synchronously with a spurious "ended" signal for a session that never started.
            // Marking _closing first means a later explicit Close()/Dispose() from the caller (e.g. cleanup in
            // a catch block) still short-circuits safely without redoing this or raising Closed itself.
            Interlocked.Exchange(ref _closing, 1);
            ReleaseNativeResources();
            throw;
        }

        _ = Task.Run(ReadLoop);
        return Task.CompletedTask;
    }

    private IntPtr _environmentBlock = IntPtr.Zero;

    /// <summary>Quotes the executable (paths like "C:\Program Files\Git\bin\bash.exe" would otherwise be split at the
    /// first space by CreateProcess) and appends the arguments untouched.</summary>
    public static string BuildCommandLine(string exe, string arguments)
    {
        var quoted = exe.Contains(' ') && !exe.StartsWith('"') ? $"\"{exe}\"" : exe;
        return string.IsNullOrWhiteSpace(arguments) ? quoted : $"{quoted} {arguments}";
    }

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            merged[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var (k, v) in overrides) merged[k] = v;
        var sb = new StringBuilder();
        foreach (var (k, v) in merged) sb.Append(k).Append('=').Append(v).Append((char)0);
        sb.Append((char)0);
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        string? error = null;
        try
        {
            while (_outputPipe is { } pipe)
            {
                int n = pipe.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                DataReceived?.Invoke(buf.AsSpan(0, n).ToArray());
            }
        }
        catch (Exception ex) when (Volatile.Read(ref _closing) == 0) { error = ex.Message; }
        catch { }
        RaiseClosed(error);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_inputPipe is null || data.IsEmpty) return;
        var copy = data.ToArray();
        try { _inputPipe.Write(copy, 0, copy.Length); _inputPipe.Flush(); } catch { }
    }

    public void Resize(int cols, int rows)
    {
        if (_pseudoConsole == IntPtr.Zero) return;
        var size = new ConPty.COORD { X = (short)Math.Max(1, cols), Y = (short)Math.Max(1, rows) };
        try { ConPty.ResizePseudoConsole(_pseudoConsole, size); } catch { }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        ReleaseNativeResources();
        RaiseClosed(null);
    }

    /// <summary>Idempotent teardown of everything ConnectAsync might have allocated - every field is
    /// null/zero-checked so this is safe to call however far setup got. Shared by Close() (which also raises
    /// Closed) and ConnectAsync's own failure path (which must not - see the comment there).</summary>
    private void ReleaseNativeResources()
    {
        if (_processHandle != IntPtr.Zero) { try { ConPty.TerminateProcess(_processHandle, 0); } catch { } }
        if (_pseudoConsole != IntPtr.Zero) { ConPty.ClosePseudoConsole(_pseudoConsole); _pseudoConsole = IntPtr.Zero; }
        if (_attributeList != IntPtr.Zero) { ConPty.DeleteProcThreadAttributeList(_attributeList); Marshal.FreeHGlobal(_attributeList); _attributeList = IntPtr.Zero; }
        if (_processHandle != IntPtr.Zero) { ConPty.CloseHandle(_processHandle); _processHandle = IntPtr.Zero; }
        try { _inputPipe?.Dispose(); } catch { }
        try { _outputPipe?.Dispose(); } catch { }
        if (_environmentBlock != IntPtr.Zero) { Marshal.FreeHGlobal(_environmentBlock); _environmentBlock = IntPtr.Zero; }
    }

    private void RaiseClosed(string? error)
    {
        if (Interlocked.Exchange(ref _raised, 1) == 0) Closed?.Invoke(error);
    }

    public void Dispose() => Close();

    /// <summary>The documented Win32 ConPTY surface (see class remarks). Kept private to this file - nothing
    /// outside LocalTransport needs it.</summary>
    private static class ConPty
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct COORD { public short X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        [DllImport("kernel32.dll")]
        public static extern int CreatePseudoConsole(COORD size, SafeHandle hInput, SafeHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll")]
        public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
