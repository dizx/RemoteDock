using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RemoteDock.Core;

namespace RemoteDock.App;

internal enum ElevationAction { Connect, LoadVirtualMachines, Inspect, Start, Save, Disconnect }

internal sealed record ElevationRetry(ConnectionProfile Profile, ElevationAction Action, int PreviousProcessId = 0,
    long PreviousProcessStartTicks = 0, string? DataDirectory = null);

internal static class ElevationRestart
{
    internal const string Argument = "--elevated-retry";

    internal static string Encode(ElevationRetry retry) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        retry with { Profile = retry.Profile with { UserName = "", Domain = "" } })));

    internal static ElevationRetry Decode(string encoded)
    {
        if (encoded.Length > 32768) throw new ArgumentException("The elevation request is too large.");
        var retry = JsonSerializer.Deserialize<ElevationRetry>(Convert.FromBase64String(encoded))
            ?? throw new ArgumentException("The elevation request is empty.");
        if (retry.Profile.Kind != ConnectionKind.HyperV || string.IsNullOrWhiteSpace(retry.Profile.Host) ||
            !HyperVHost.IsLocal(retry.Profile.Host) || !Enum.IsDefined(retry.Action) ||
            retry.DataDirectory is not null && !System.IO.Path.IsPathFullyQualified(retry.DataDirectory))
            throw new ArgumentException("The elevation request must target a local Hyper-V connection.");
        return retry;
    }

    internal static bool Start(ElevationRetry retry)
    {
        using var current = Process.GetCurrentProcess();
        retry = retry with { PreviousProcessId = current.Id, PreviousProcessStartTicks = current.StartTime.ToUniversalTime().Ticks };
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate RemoteDock to restart it."),
            UseShellExecute = true, Verb = "runas", Arguments = $"{Argument} {Encode(retry)}"
        };
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the elevated application.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
    }

    internal static async Task WaitForPreviousProcessAsync(ElevationRetry retry)
    {
        if (retry.PreviousProcessId <= 0 || retry.PreviousProcessId == Environment.ProcessId) return;
        try
        {
            using var previous = Process.GetProcessById(retry.PreviousProcessId);
            if (previous.StartTime.ToUniversalTime().Ticks != retry.PreviousProcessStartTicks) return;
            await previous.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { } // The original instance already exited.
    }
}
