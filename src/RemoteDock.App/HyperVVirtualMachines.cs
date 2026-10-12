using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using RemoteDock.Core;

namespace RemoteDock.App;

internal sealed record HyperVVirtualMachine(Guid Id, string Name, ushort? State)
{
    public bool DuplicateName { get; init; }
    public bool IsSaved => State is 6 or 32769;
    public bool CanStart => State is 3 or 32768 || IsSaved;
    public bool CanSave => State is 2 or 32768;
    public string StartAction => State == 32768 || IsSaved ? "Resume" : "Start";
    public string Label => $"{Name} · {StateName}" + (DuplicateName ? $" · {Id.ToString("D")[..8]}" : "");
    public string StateName => State switch
    {
        null => "Load VMs to refresh", 2 => "Running", 3 => "Off", 4 => "Stopping", 6 or 32769 => "Saved", 10 => "Starting", 32768 => "Paused",
        32770 => "Starting", 32771 => "Taking snapshot", 32773 => "Saving", 32774 => "Stopping",
        32776 => "Pausing", 32777 => "Resuming", _ => $"State {State}"
    };
}

internal static class HyperVVirtualMachines
{
    internal static Task<HyperVVirtualMachine[]> LoadAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var scope = HyperVHost.Connect(profile, cancellationToken);
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem"),
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
            using var systems = searcher.Get();
            var machines = new List<HyperVVirtualMachine>();
            foreach (ManagementObject system in systems)
            {
                using (system)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // The host also appears in this class; only VMs have GUID identities.
                    if (Guid.TryParse(system["Name"] as string, out var id) && id != Guid.Empty)
                        machines.Add(new(id, system["ElementName"] as string ?? "Unnamed VM", Convert.ToUInt16(system["EnabledState"])));
                }
            }
            if (machines.Count == 0) HyperVHost.CheckEmptyInventory(profile);
            return machines.OrderBy(vm => vm.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(vm => vm.Id)
                .Select(vm => vm with { DuplicateName = machines.Count(other => string.Equals(other.Name, vm.Name, StringComparison.OrdinalIgnoreCase)) > 1 }).ToArray();
        }, cancellationToken);
}
