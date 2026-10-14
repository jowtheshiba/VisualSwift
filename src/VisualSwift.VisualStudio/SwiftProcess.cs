using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace VisualSwift
{
    internal static class SwiftProcess
    {
        public static async Task<string> RunAsync(Toolchain toolchain, string arguments, string workspace, CancellationToken token, TimeSpan? timeout = null, Action<string> progress = null)
        {
            token.ThrowIfCancellationRequested();
            using (var process = new Process { StartInfo = toolchain.CreateSwiftStartInfo(arguments, workspace), EnableRaisingEvents = true })
            using (var job = new SwiftProcessJob())
            {
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) => exited.TrySetResult(true);
                if (!process.Start()) throw new InvalidOperationException("Swift could not be started.");
                job.Attach(process);
                var output = ReadAsync(process.StandardOutput, progress);
                var error = ReadAsync(process.StandardError, progress);
                var completed = Task.WhenAll(exited.Task, output, error);
                var duration = timeout ?? TimeSpan.FromSeconds(30);
                var limit = Task.Delay(duration, token);
                if (await Task.WhenAny(completed, limit).ConfigureAwait(false) != completed)
                {
                    job.Dispose();
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("Swift did not respond within " + duration.TotalSeconds + " seconds.");
                }
                await completed.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidOperationException("Swift exited with code " + process.ExitCode + ": " + await error.ConfigureAwait(false));
                return await output.ConfigureAwait(false);
            }
        }
        private static async Task<string> ReadAsync(StreamReader reader, Action<string> progress)
        {
            var result = new StringBuilder();
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                result.AppendLine(line);
                progress?.Invoke(line);
            }
            return result.ToString();
        }
    }

    internal sealed class SwiftProcessJob : IDisposable
    {
        private readonly SafeFileHandle handle;
        public SwiftProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new System.ComponentModel.Win32Exception(error);
            }
        }
        public void Attach(Process process)
        {
            if (process.HasExited) return;
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                if (process.HasExited) return;
                var error = Marshal.GetLastWin32Error();
                process.Kill();
                throw new System.ComponentModel.Win32Exception(error, "The Swift process could not be attached to its lifetime manager.");
            }
        }
        public void Dispose() => handle.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
