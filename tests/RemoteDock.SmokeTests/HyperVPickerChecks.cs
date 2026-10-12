using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RemoteDock.App;
using RemoteDock.Core;

namespace RemoteDock.SmokeTests;

internal static class HyperVPickerChecks
{
    internal static async Task Run()
    {
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var profile = new ConnectionProfile { Host = "host.invalid", Kind = ConnectionKind.HyperV, Port = 2179,
            VirtualMachineId = id, VirtualMachineName = "Old VM name", UserName = "ignored-host-user" };
        var editor = new ConnectionEditor(profile);
        try
        {
            var picker = (ComboBox)editor.FindName("VmBox");
            var manual = (TextBox)editor.FindName("VmIdBox");
            var host = (TextBox)editor.FindName("HostBox");
            Require(((HyperVVirtualMachine)picker.SelectedItem).Name == "Old VM name", "The saved VM name was not displayed before discovery.");
            await editor.LoadVirtualMachinesAsync((request, token) =>
            {
                Require(request.Host == profile.Host && request.UsesWindowsHostAuthentication,
                    "VM discovery did not use the selected host's Windows authentication.");
                return Task.FromResult(new[] { new HyperVVirtualMachine(otherId, "Other VM", 3), new HyperVVirtualMachine(id, "Renamed VM", 2) });
            });
            Require(((HyperVVirtualMachine)picker.SelectedItem).Name == "Renamed VM", "Discovery did not match a renamed VM by GUID.");
            Require(((HyperVVirtualMachine)picker.SelectedItem).Label == "Renamed VM · Running", "The VM picker did not show its name and state.");
            picker.SelectedIndex = 0;
            Require(manual.Text == otherId.ToString("D"), "Selecting a VM did not populate its ID.");
            await editor.LoadVirtualMachinesAsync((_, _) => Task.FromException<HyperVVirtualMachine[]>(new UnauthorizedAccessException()));
            Require(manual.Text == otherId.ToString("D") && ((Expander)editor.FindName("VmIdDetails")).IsExpanded,
                "Failed discovery lost the selected VM or hid manual entry.");

            var pending = new TaskCompletionSource<HyperVVirtualMachine[]>();
            var load = editor.LoadVirtualMachinesAsync((_, _) => pending.Task);
            host.Text = "other-host.invalid";
            pending.SetResult([new(id, "Wrong host VM", 2)]);
            await load;
            Require(picker.Items.Count == 0 && manual.Text == "", "An old host's discovery result survived a host change.");
            await editor.LoadVirtualMachinesAsync((_, _) => Task.FromResult(Array.Empty<HyperVVirtualMachine>()));
            Require(!picker.IsEnabled && manual.Text == "", "An empty host selected a VM.");
            manual.Text = id.ToString("D");
            _ = editor.Dispatcher.BeginInvoke(new Action(() => ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Require(editor.ShowDialog() == true && editor.Result is { } result && result.VirtualMachineId == id && result.VirtualMachineName == "",
                "Manual VM entry retained a stale name or could not be saved.");
        }
        finally { editor.Close(); }

        var named = new ConnectionEditor(profile);
        try
        {
            await named.LoadVirtualMachinesAsync((_, _) => Task.FromResult(new[] { new HyperVVirtualMachine(id, "Current VM name", 32769) }));
            _ = named.Dispatcher.BeginInvoke(new Action(() => ((Button)named.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Require(named.ShowDialog() == true && named.Result?.VirtualMachineName == "Current VM name" && named.Result.VirtualMachineId == id,
                "Selecting a VM did not save its friendly name with the stable ID.");
        }
        finally { named.Close(); }

        var closing = new ConnectionEditor(profile);
        var delayed = new TaskCompletionSource<HyperVVirtualMachine[]>();
        var outstanding = closing.LoadVirtualMachinesAsync((_, _) => delayed.Task);
        closing.Close();
        delayed.SetResult([new(otherId, "Too late", 2)]);
        await outstanding;
        Require(((TextBox)closing.FindName("VmIdBox")).Text == id.ToString("D"), "Closing the editor applied a late discovery result.");
        Console.WriteLine("PASS: Hyper-V VM name/state picker; rename matching; save/manual fallback; access failure; stale-host and closed-editor results ignored. Discovery was simulated.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
