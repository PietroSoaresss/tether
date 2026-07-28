using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Orchestration.Core.Terminal;

public enum SessionState { NotStarted, Running, Exited, Killed }

/// <summary>
/// A child process attached to a Windows pseudoconsole (ConPTY).
/// Raw VT bytes come out through <see cref="OutputReceived"/>; input goes in through <see cref="Write"/>.
/// The process tree lives inside a job object with KILL_ON_JOB_CLOSE, so it dies with us even on a crash.
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();

    private IntPtr _pseudoConsole = IntPtr.Zero;
    private IntPtr _job = IntPtr.Zero;
    private IntPtr _processHandle = IntPtr.Zero;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    // The pseudoconsole does not duplicate these; they must outlive it or the pipes break instantly.
    private SafeFileHandle? _inputRead;
    private SafeFileHandle? _outputWrite;
    private FileStream? _writer;
    private FileStream? _reader;
    private Thread? _readerThread;
    private Thread? _waiterThread;
    private bool _disposed;

    public int ProcessId { get; private set; }
    public SessionState State { get; private set; } = SessionState.NotStarted;
    public int? ExitCode { get; private set; }
    public short Columns { get; private set; }
    public short Rows { get; private set; }

    /// <summary>Fires on the reader thread with a freshly allocated copy of each chunk.</summary>
    public event Action<byte[]>? OutputReceived;

    /// <summary>Fires once when the child process ends. Argument is the exit code.</summary>
    public event Action<int>? Exited;

    public void Start(string commandLine, string? workingDirectory = null, short columns = 120, short rows = 30)
    {
        if (State != SessionState.NotStarted) throw new InvalidOperationException("Session already started.");
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns), "Terminal size must be positive.");

        Columns = columns;
        Rows = rows;

        // Two anonymous pipes. We keep the outer ends; the pseudoconsole takes the inner ends.
        if (!Native.CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed.");
        if (!Native.CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed.");

        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _inputRead = inputRead;
        _outputWrite = outputWrite;

        int hr = Native.CreatePseudoConsole(new Native.Coord(columns, rows), inputRead, outputWrite, 0, out _pseudoConsole);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed.");

        IntPtr attributeList = IntPtr.Zero;
        try
        {
            attributeList = BuildAttributeList(_pseudoConsole);

            var startupInfo = new Native.StartupInfoEx();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<Native.StartupInfoEx>();
            startupInfo.lpAttributeList = attributeList;
            // STARTF_USESTDHANDLES with all three handles left null. Without this the child inherits
            // our standard handles whenever they are redirected and ignores the pseudoconsole entirely.
            startupInfo.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;

            const uint flags = Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT;

            if (!Native.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                                      IntPtr.Zero, workingDirectory, ref startupInfo, out Native.ProcessInformation pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for: {commandLine}");

            _processHandle = pi.hProcess;
            ProcessId = pi.dwProcessId;

            AttachToJob(pi.hProcess);
            Native.CloseHandle(pi.hThread);
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }

        _writer = new FileStream(_inputWrite, FileAccess.Write, bufferSize: 1, isAsync: false);
        _reader = new FileStream(_outputRead, FileAccess.Read, bufferSize: 4096, isAsync: false);
        State = SessionState.Running;

        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = $"conpty-read-{ProcessId}" };
        _readerThread.Start();
        _waiterThread = new Thread(WaitForExit) { IsBackground = true, Name = $"conpty-wait-{ProcessId}" };
        _waiterThread.Start();
    }

    private static IntPtr BuildAttributeList(IntPtr pseudoConsole)
    {
        IntPtr size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr list = Marshal.AllocHGlobal(size);

        if (!Native.InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
        }

        if (!Native.UpdateProcThreadAttribute(list, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, pseudoConsole,
                                              (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            Native.DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
        }

        return list;
    }

    private void AttachToJob(IntPtr process)
    {
        _job = Native.CreateJobObject(IntPtr.Zero, null);
        if (_job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

        var limits = new Native.JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int length = Marshal.SizeOf<Native.JobObjectExtendedLimitInformation>();

        if (!Native.SetInformationJobObject(_job, Native.JobObjectExtendedLimitInformation_Class, ref limits, length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");
        if (!Native.AssignProcessToJobObject(_job, process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read = _reader!.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                OutputReceived?.Invoke(chunk);
            }
        }
        catch (Exception) when (_disposed)
        {
            // Teardown races with the blocking read; nothing to report.
        }
        catch (IOException)
        {
            // Pipe broke because the child went away. WaitForExit reports the real outcome.
        }
    }

    private void WaitForExit()
    {
        Native.WaitForSingleObject(_processHandle, Native.INFINITE);
        Native.GetExitCodeProcess(_processHandle, out uint code);

        lock (_gate)
        {
            if (State is SessionState.Exited or SessionState.Killed) return;
            ExitCode = (int)code;
            State = SessionState.Exited;
        }
        Exited?.Invoke((int)code);
    }

    /// <summary>Writes to the child's stdin. Serialized: the user's keyboard and every pipe share this one channel.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (State != SessionState.Running) return;
        _writeLock.Wait();
        try
        {
            _writer!.Write(data);
            _writer.Flush();
        }
        catch (IOException)
        {
            // Broken pipe: the child died between the state check and the write.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Write(string text) => Write(System.Text.Encoding.UTF8.GetBytes(text));

    public void Resize(short columns, short rows)
    {
        if (columns < 1 || rows < 1) return;
        if (State != SessionState.Running || _pseudoConsole == IntPtr.Zero) return;
        Columns = columns;
        Rows = rows;
        Native.ResizePseudoConsole(_pseudoConsole, new Native.Coord(columns, rows));
    }

    /// <summary>Kills the whole process tree via the job object.</summary>
    public void Kill()
    {
        lock (_gate)
        {
            if (State != SessionState.Running) return;
            State = SessionState.Killed;
        }
        if (_job != IntPtr.Zero) Native.TerminateJobObject(_job, 1);
        Exited?.Invoke(-1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_job != IntPtr.Zero) Native.TerminateJobObject(_job, 1);

        // Closing the pseudoconsole is what unblocks the reader thread's pending Read.
        if (_pseudoConsole != IntPtr.Zero)
        {
            Native.ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        // Our own copy of the write end must go too, otherwise the reader never sees EOF.
        _outputWrite?.Dispose();
        _inputRead?.Dispose();

        _readerThread?.Join(TimeSpan.FromSeconds(2));

        _writer?.Dispose();
        _reader?.Dispose();
        _inputWrite?.Dispose();
        _outputRead?.Dispose();

        if (_processHandle != IntPtr.Zero) { Native.CloseHandle(_processHandle); _processHandle = IntPtr.Zero; }
        if (_job != IntPtr.Zero) { Native.CloseHandle(_job); _job = IntPtr.Zero; }

        _writeLock.Dispose();
    }

    private static class Native
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const int STARTF_USESTDHANDLES = 0x00000100;
        public const uint INFINITE = 0xFFFFFFFF;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        public const int JobObjectExtendedLimitInformation_Class = 9;
        public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public struct Coord
        {
            public short X;
            public short Y;
            public Coord(short x, short y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfo
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
            string? lpCurrentDirectory, ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JobObjectExtendedLimitInformation info, int cbLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
