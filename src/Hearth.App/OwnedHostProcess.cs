using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hearth.App;

/// <summary>A hidden child that Windows terminates when its launcher disappears.</summary>
internal sealed class OwnedHostProcess : IDisposable
{
    private readonly Process process;
    private readonly SafeFileHandle job;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    private OwnedHostProcess(Process process, SafeFileHandle job)
    {
        this.process = process;
        this.job = job;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == "HEARTH_HOST_READY") ready.TrySetResult();
        };
        // Both pipes must be drained even when the UI does not display host logs.
        process.ErrorDataReceived += (_, _) => { };
    }

    public int Id => process.Id;
    public bool IsRunning
    {
        get
        {
            try { return Volatile.Read(ref disposed) == 0 && !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static OwnedHostProcess Start(string executable, IEnumerable<string> arguments)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not protect the local host's lifetime.");
        OwnedHostProcess? child = null;
        try
        {
            var limits = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x2000 }
            };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not protect the local host's lifetime.");

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--launcher-managed");
            child = new(new Process { StartInfo = start }, job);
            if (!child.process.Start()) throw new IOException("Windows could not start the local host.");
            child.process.BeginOutputReadLine();
            child.process.BeginErrorReadLine();
            // The child waits on stdin before initializing data or opening a socket.
            // No host work can escape the job if this assignment fails or we crash.
            if (!AssignProcessToJobObject(job, child.process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not attach hosting to Hearth. Hosting was not started.");
            child.process.StandardInput.WriteLine("start");
            child.process.StandardInput.Flush();
            return child;
        }
        catch
        {
            if (child is not null) child.Dispose();
            else job.Dispose();
            throw;
        }
    }

    public async Task WaitUntilListeningAsync(CancellationToken cancellationToken)
    {
        var exit = process.WaitForExitAsync(cancellationToken);
        var winner = await Task.WhenAny(ready.Task, exit).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (winner == exit || !IsRunning)
            throw new IOException("The local host stopped before it could start. Check that its port and data folder are available.");
        await ready.Task.ConfigureAwait(false);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public async Task StopAsync()
    {
        RequestStop();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        finally { Dispose(); }
    }

    private void RequestStop()
    {
        if (!IsRunning) return;
        try
        {
            process.StandardInput.WriteLine("stop");
            process.StandardInput.Flush();
            process.StandardInput.Close();
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Closing the non-inherited job handle kills every owned process, including
        // on abrupt launcher termination when no managed cleanup code can run.
        job.Dispose();
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        finally { process.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref ExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
