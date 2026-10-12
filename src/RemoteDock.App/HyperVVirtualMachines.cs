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
    public string Label => $"{Name} · {StateName}" + (DuplicateName ? $" · {Id.ToString("D")[..8]}" : "");
    public string StateName => State switch
    {
        null => "Load VMs to refresh", 2 => "Running", 3 => "Off", 32768 => "Paused", 32769 => "Saved",
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
            return machines.OrderBy(vm => vm.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(vm => vm.Id)
                .Select(vm => vm with { DuplicateName = machines.Count(other => string.Equals(other.Name, vm.Name, StringComparison.OrdinalIgnoreCase)) > 1 }).ToArray();
        }, cancellationToken);
}
