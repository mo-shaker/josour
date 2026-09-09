using System.Diagnostics;

namespace Josour.Core.Diagnostics;

/// <summary>What a console tool printed, and whether it ran at all.</summary>
/// <param name="ExitCode">The tool's exit code, or <c>null</c> when it could not be started, timed out or was killed.</param>
/// <param name="Output">Standard output; empty when the tool did not run.</param>
public readonly record struct ProcessRunResult(int? ExitCode, string Output)
{
    /// <summary>The tool could not be run (missing, refused, timed out): every value derived from it is "unknown".</summary>
    public static ProcessRunResult NotRun => new(null, string.Empty);

    public bool Ran => ExitCode is not null;
}

/// <summary>
/// Runs a console tool and returns what it printed. Abstracted so that output parsing — <c>netsh</c>'s above all — can be
/// tested on any platform, including the localized Windows output that decides whether a check is trustworthy.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>: no window, output captured, killed on timeout, never throws.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public static SystemProcessRunner Instance { get; } = new();

    public async Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return ProcessRunResult.NotRun;
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(budget.Token);
            try
            {
                await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // it exited on its own between the timeout and the kill
                }

                return ProcessRunResult.NotRun;
            }

            return new ProcessRunResult(process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return ProcessRunResult.NotRun;
        }
    }
}
