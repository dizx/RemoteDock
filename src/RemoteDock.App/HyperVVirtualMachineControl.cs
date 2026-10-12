using System;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using RemoteDock.Core;

namespace RemoteDock.App;

internal enum HyperVAction { Start, Save }

internal static class HyperVVirtualMachineControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    internal static Task<HyperVVirtualMachine> GetAsync(ConnectionProfile profile) => Task.Run(() =>
    {
        var scope = HyperVHost.Connect(profile, CancellationToken.None);
        using var vm = Find(scope, profile);
        return Snapshot(vm);
    });

    internal static Task ChangeAsync(ConnectionProfile profile, HyperVAction action, IProgress<int>? progress = null) => Task.Run(async () =>
    {
        var scope = HyperVHost.Connect(profile, CancellationToken.None);
        using var vm = Find(scope, profile);
        var current = Snapshot(vm);
        bool AtTarget(HyperVVirtualMachine machine) => action == HyperVAction.Start ? machine.State == 2 : machine.IsSaved;
        if (AtTarget(current)) { progress?.Report(100); return; }
        if (!(action == HyperVAction.Start ? current.CanStart : current.CanSave))
            throw new InvalidOperationException($"Cannot {action.ToString().ToLowerInvariant()} '{current.Name}' while it is {current.StateName}.");

        using var input = vm.GetMethodParameters("RequestStateChange");
        // V2 uses Offline (6) to save; hosts report saved as 6 or legacy 32769.
        // Disabled (3) would turn the VM off and must never be used for Save.
        input["RequestedState"] = action == HyperVAction.Start ? (ushort)2 : (ushort)6;
        progress?.Report(0);
        using var output = vm.InvokeMethod("RequestStateChange", input,
            new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(15) });
        uint result = Convert.ToUInt32(output["ReturnValue"]);
        if (result == 32769) throw new UnauthorizedAccessException($"Hyper-V denied permission to {action.ToString().ToLowerInvariant()} '{current.Name}'.");
        if (result != 0 && result != 4096)
            throw new InvalidOperationException($"Hyper-V could not {action.ToString().ToLowerInvariant()} '{current.Name}' (code {result}).");

        var elapsed = Stopwatch.StartNew();
        if (result == 4096)
        {
            if (output["Job"] is not string path || string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Hyper-V started the operation but returned no job to monitor.");
            using var job = new ManagementObject(scope, new ManagementPath(path),
                new ObjectGetOptions { Timeout = TimeSpan.FromSeconds(15) });
            while (true)
            {
                CheckTimeout(elapsed);
                job.Get();
                ushort state = Convert.ToUInt16(job["JobState"]);
                // Some hosts hold at 0 until completion; report the provider's value, not elapsed time.
                if (job["PercentComplete"] is { } percent)
                    progress?.Report(Math.Clamp(Convert.ToInt32(percent), 0, 99));
                if (state == 7 && Convert.ToUInt16(job["ErrorCode"]) == 0) break;
                if (state is 7 or 8 or 9 or 10)
                    throw new InvalidOperationException($"Hyper-V could not {action.ToString().ToLowerInvariant()} '{current.Name}': {job["ErrorDescription"]} (code {job["ErrorCode"]}).");
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        do
        {
            CheckTimeout(elapsed);
            vm.Get();
            if (AtTarget(Snapshot(vm))) { progress?.Report(100); return; }
            await Task.Delay(250).ConfigureAwait(false);
        } while (true);
    });

    private static void CheckTimeout(Stopwatch elapsed)
    {
        if (elapsed.Elapsed >= Timeout)
            throw new TimeoutException("Timed out waiting for Hyper-V. The operation may still finish on the host; refresh its state before trying again.");
    }

    private static ManagementObject Find(ManagementScope scope, ConnectionProfile profile)
    {
        if (profile.Kind != ConnectionKind.HyperV || profile.VirtualMachineId is not { } id || id == Guid.Empty)
            throw new ArgumentException("Select a Hyper-V virtual machine first.");
        // Match GUIDs as identities, then use the provider's exact object path. Do not
        // reconstruct a path from a lower-case GUID: provider keys need not be normalized.
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT * FROM Msvm_ComputerSystem"),
            new EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
        using var systems = searcher.Get();
        bool visibleVm = false;
        foreach (ManagementObject system in systems)
        {
            using (system)
            {
                if (!Guid.TryParse(system["Name"] as string, out var candidate)) continue;
                visibleVm = true;
                if (candidate != id) continue;
                var vm = new ManagementObject(scope, system.Path,
                    new ObjectGetOptions { Timeout = TimeSpan.FromSeconds(15) });
                try { vm.Get(); return vm; }
                catch { vm.Dispose(); throw; }
            }
        }
        if (!visibleVm) HyperVHost.CheckEmptyInventory(profile);
        throw new InvalidOperationException($"VM {id:D} is not visible on '{profile.Host}'. Check the saved VM ID and this Windows account's Hyper-V management access on that host.");
    }

    private static HyperVVirtualMachine Snapshot(ManagementObject vm) => new(
        Guid.Parse((string)vm["Name"]), (string)vm["ElementName"], Convert.ToUInt16(vm["EnabledState"]));
}
