using System;
using System.Threading.Tasks;
using RemoteDock.Core;

namespace RemoteDock.App;

internal sealed class HyperVSessionActions
{
    internal Func<ConnectionProfile, Task<HyperVVirtualMachine>> GetAsync { get; init; } = HyperVVirtualMachineControl.GetAsync;
    internal Func<ConnectionProfile, HyperVAction, IProgress<int>?, Task> ChangeAsync { get; init; } = HyperVVirtualMachineControl.ChangeAsync;

    internal async Task<bool> PrepareConnectAsync(ConnectionProfile profile, Func<string, string, bool> confirm, Func<bool>? isCancelled = null,
        Action<string>? report = null)
    {
        if (profile.Kind != ConnectionKind.HyperV) return true;
        report?.Invoke($"Checking virtual machine on {profile.Host}…");
        HyperVVirtualMachine vm;
        try { vm = await GetAsync(profile); }
        catch (Exception ex) when (HyperVHost.ShouldOfferElevation(profile, ex)) { throw; }
        catch (Exception ex)
        {
            if (isCancelled?.Invoke() == true) return false;
            report?.Invoke($"VM state unavailable: {ex.Message}");
            return confirm($"Could not check the VM state on '{profile.Host}': {ex.Message}\n\nConnect to its console anyway?", "VM state unavailable");
        }
        if (isCancelled?.Invoke() == true) return false;
        report?.Invoke($"{vm.Name} · {vm.StateName}");
        if (!vm.CanStart) return true;
        if (!confirm($"'{vm.Name}' is {vm.StateName}. {vm.StartAction} it and connect?", $"{vm.StartAction} virtual machine")) return false;
        await ChangeWithProgressAsync(profile, HyperVAction.Start, vm.StartAction == "Resume" ? "Resuming" : "Starting", report);
        return true;
    }

    internal async Task PrepareDisconnectAsync(ConnectionProfile profile, Func<string, string, bool> confirm, Action<string>? report = null)
    {
        if (profile.Kind != ConnectionKind.HyperV) return;
        var vm = await GetAsync(profile);
        if (vm.CanSave && confirm($"Save the state of '{vm.Name}' before disconnecting?\n\nYes saves its memory and pauses work until you resume it. No disconnects and leaves the VM as it is.", "Save virtual machine state"))
            await ChangeWithProgressAsync(profile, HyperVAction.Save, "Saving state", report);
    }

    internal async Task ChangeWithProgressAsync(ConnectionProfile profile, HyperVAction action, string description, Action<string>? report)
    {
        bool finished = false;
        int lastPercent = 0;
        report?.Invoke($"{description}… 0%");
        var progress = new Progress<int>(percent =>
        {
            if (finished) return;
            lastPercent = Math.Max(lastPercent, Math.Clamp(percent, 0, 99));
            report?.Invoke($"{description}… {lastPercent}%");
        });
        try
        {
            await ChangeAsync(profile, action, progress);
            report?.Invoke($"{description}… 100%");
        }
        finally { finished = true; }
    }
}
