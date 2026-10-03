using System.Diagnostics;
using System.Text;

namespace UniversalMediaOS.Core.Services;

internal sealed record PreparationProcessResult(int ExitCode, string Output);
internal interface IPreparationProcessRunner
{
    Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token);
}

internal sealed class PreparationProcessRunner : IPreparationProcessRunner
{
    public async Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Process did not start.");
        // Drain both pipes immediately. Keep bounded diagnostic output even when pip is noisy.
        Task<string> output = DrainAsync(process.StandardOutput, token);
        Task<string> error = DrainAsync(process.StandardError, token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            try { await Task.WhenAll(output, error, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* Cancellation/closed pipes are expected during owned-process cleanup. */ }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            if (output.Length < 65536) output.Append(buffer, 0, Math.Min(count, 65536 - output.Length));
        return output.ToString();
    }
}
