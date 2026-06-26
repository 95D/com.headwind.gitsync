using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace Headwind.GitSync.Data
{
    /// <summary>
    /// Result of a git process execution.
    /// </summary>
    public readonly struct ProcessResult
    {
        public readonly string Stdout;
        public readonly string Stderr;
        public readonly int ExitCode;
        public bool IsSuccess => ExitCode == 0;

        public ProcessResult(string stdout, string stderr, int exitCode)
        {
            Stdout = stdout;
            Stderr = stderr;
            ExitCode = exitCode;
        }
    }

    /// <summary>
    /// Runs Git CLI commands on a background thread via Task.Run,
    /// preventing the Unity Editor main thread from freezing.
    /// </summary>
    public static class GitProcessUtility
    {
        private const string GitExecutable = "git";

        /// <summary>
        /// Executes a git command asynchronously and returns its output.
        /// </summary>
        /// <param name="workingDirectory">Absolute path of the git repository root.</param>
        /// <param name="arguments">Arguments passed directly to git (e.g. "status --porcelain").</param>
        public static Task<ProcessResult> RunAsync(string workingDirectory, string arguments)
        {
            return Task.Run(() => RunBlocking(workingDirectory, arguments));
        }

        private static ProcessResult RunBlocking(string workingDirectory, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName               = GitExecutable,
                Arguments              = arguments,
                WorkingDirectory       = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var process = new Process { StartInfo = startInfo };

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderrBuilder.AppendLine(e.Data);
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
            }
            catch (Exception ex)
            {
                return new ProcessResult(string.Empty, $"Failed to start git process: {ex.Message}", -1);
            }

            return new ProcessResult(
                stdoutBuilder.ToString().TrimEnd(),
                stderrBuilder.ToString().TrimEnd(),
                process.ExitCode
            );
        }
    }
}
