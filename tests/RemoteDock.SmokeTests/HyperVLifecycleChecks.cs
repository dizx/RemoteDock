using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RemoteDock.App;
using RemoteDock.Core;

namespace RemoteDock.SmokeTests;

internal static class HyperVLifecycleChecks
{
    internal static async Task Run()
    {
        var profile = new ConnectionProfile { Name = "Lifecycle VM", Host = "host.invalid", Kind = ConnectionKind.HyperV,
            Port = 2179, VirtualMachineId = Guid.NewGuid() };
        HyperVVirtualMachine vm = new(profile.VirtualMachineId.Value, "Current VM name", 3);
        int reads = 0, changes = 0, prompts = 0;
        HyperVAction? action = null;
        bool answer = false;
        string title = "";
        TaskCompletionSource? pending = null;
        Exception? failure = null;
        var actions = new HyperVSessionActions
        {
            GetAsync = request =>
            {
                Require(request.Host == profile.Host && request.VirtualMachineId == profile.VirtualMachineId,
                    "Lifecycle operation targeted a different host or VM.");
                reads++;
                return Task.FromResult(vm);
            },
            ChangeAsync = (request, requested, _) =>
            {
                Require(request.VirtualMachineId == profile.VirtualMachineId, "State change lost the VM ID.");
                changes++;
                action = requested;
                return failure is not null ? Task.FromException(failure) : pending?.Task ?? Task.CompletedTask;
            }
        };
        bool Confirm(string message, string heading)
        {
            prompts++;
            title = heading;
            Require(message.Contains(vm.Name), "Prompt did not use the current VM name.");
            return answer;
        }

        Require(!await actions.PrepareConnectAsync(profile, Confirm) && changes == 0 && title.StartsWith("Start"),
            "Declining Start did not cancel connection without changing the VM.");
        answer = true;
        pending = new();
        var connecting = actions.PrepareConnectAsync(profile, Confirm);
        Require(!connecting.IsCompleted && action == HyperVAction.Start, "Connection did not wait for the start job.");
        pending.SetResult();
        Require(await connecting, "Successful start did not allow connection.");
        pending = null;

        foreach (ushort state in new ushort[] { 6, 32769, 32768 })
        {
            vm = vm with { State = state };
            Require(await actions.PrepareConnectAsync(profile, Confirm) && title.StartsWith("Resume"),
                "Saved/paused VM did not offer Resume.");
        }
        vm = vm with { State = 2 };
        int beforePrompts = prompts, beforeChanges = changes;
        Require(await actions.PrepareConnectAsync(profile, Confirm) && prompts == beforePrompts && changes == beforeChanges,
            "Running VM was prompted or started again.");

        answer = false;
        await actions.PrepareDisconnectAsync(profile, Confirm);
        Require(changes == beforeChanges, "Declining Save changed the VM.");
        answer = true;
        pending = new();
        var disconnecting = actions.PrepareDisconnectAsync(profile, Confirm);
        Require(!disconnecting.IsCompleted && action == HyperVAction.Save, "Disconnect did not wait for saving.");
        pending.SetResult();
        await disconnecting;
        pending = null;

        beforePrompts = prompts;
        beforeChanges = changes;
        foreach (ushort state in new ushort[] { 3, 6, 32769, 32773 })
        {
            vm = vm with { State = state };
            await actions.PrepareDisconnectAsync(profile, Confirm);
        }
        Require(prompts == beforePrompts && changes == beforeChanges, "Off, saved or saving VM was asked to save again.");

        int beforeReads = reads;
        var rdp = profile with { Kind = ConnectionKind.Rdp };
        Require(await actions.PrepareConnectAsync(rdp, Confirm), "RDP connection was blocked.");
        await actions.PrepareDisconnectAsync(rdp, Confirm);
        Require(reads == beforeReads && prompts == beforePrompts, "RDP invoked Hyper-V management.");

        failure = new InvalidOperationException("Simulated host job failure");
        vm = vm with { State = 3 };
        await RequireFailure(() => actions.PrepareConnectAsync(profile, Confirm));
        vm = vm with { State = 2 };
        await RequireFailure(() => actions.PrepareDisconnectAsync(profile, Confirm));
        failure = null;

        var unavailable = new HyperVSessionActions { GetAsync = _ => Task.FromException<HyperVVirtualMachine>(new UnauthorizedAccessException()) };
        Require(!await unavailable.PrepareConnectAsync(profile, (_, _) => false), "Unavailable WMI ignored declined console fallback.");
        Require(await unavailable.PrepareConnectAsync(profile, (_, _) => true), "Unavailable WMI blocked explicit console fallback.");
        await RequireFailure(() => unavailable.PrepareDisconnectAsync(profile, (_, _) => true));

        await CheckMenusAndPendingClose(profile, actions, state => vm = vm with { State = state }, () => changes);
        Console.WriteLine("PASS: Hyper-V start/resume/save decisions; declined prompts; async completion and failures; WMI fallback; state-aware connection/tab menus; stale menu results; duplicate requests and close-during-connect. Host operations were simulated.");
    }

    private static async Task CheckMenusAndPendingClose(ConnectionProfile profile, HyperVSessionActions actions, Action<ushort> setState, Func<int> changes)
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "opencode", "RemoteDock.HyperVLifecycle", Guid.NewGuid().ToString("N"));
        var store = new ConnectionStore(Path.Combine(sandbox, "connections.json"));
        var rdp = new ConnectionProfile { Name = "RDP", Host = "offline.invalid" };
        store.SaveCatalog(new() { Connections = [profile, rdp] });
        var window = new MainWindow(store) { VmActions = actions };
        try
        {
            window.Show();
            await Task.Delay(100);
            var tree = (TreeView)window.FindName("ConnectionsList");
            var item = tree.Items.Cast<TreeViewItem>().Single(i => ((ConnectionProfile)i.Tag).Id == profile.Id);
            item.IsSelected = true;
            var menu = tree.ContextMenu;
            var start = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Start");
            var save = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Save state");
            foreach (var (state, label, canStart, canSave) in new[]
            {
                ((ushort)3, "Start", true, false), ((ushort)6, "Resume", true, false), ((ushort)32769, "Resume", true, false),
                ((ushort)2, "Start", false, true), ((ushort)32773, "Start", false, false)
            })
            {
                setState(state);
                menu.IsOpen = true;
                await Task.Delay(40);
                Require(start.Visibility == Visibility.Visible && (string)start.Header == label && start.IsEnabled == canStart && save.IsEnabled == canSave,
                    $"Incorrect Hyper-V menu for state {state}: open={menu.IsOpen}, visibility={start.Visibility}, label={start.Header}, start={start.IsEnabled}, save={save.IsEnabled}, detail={start.ToolTip}.");
                string stateName = new HyperVVirtualMachine(profile.VirtualMachineId!.Value, "VM", state).StateName;
                Require(menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == $"VM state: {stateName}" && i.Visibility == Visibility.Visible),
                    "The connection menu did not display the VM state explicitly.");
                if (canStart || canSave)
                {
                    int before = changes();
                    (canStart ? start : save).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Require(changes() == before + 1, "VM menu did not invoke its state change.");
                }
                menu.IsOpen = false;
                await Task.Delay(100);
            }

            var delayed = new TaskCompletionSource<HyperVVirtualMachine>();
            int reads = 0;
            window.VmActions = new() { GetAsync = _ => { reads++; return delayed.Task; } };
            menu.IsOpen = true;
            Require(!start.IsEnabled && !save.IsEnabled, "Pending menu enabled VM actions.");
            menu.IsOpen = false;
            await Task.Delay(100);
            tree.Items.Cast<TreeViewItem>().Single(i => ((ConnectionProfile)i.Tag).Id == rdp.Id).IsSelected = true;
            menu.IsOpen = true;
            delayed.SetResult(new(profile.VirtualMachineId!.Value, "Late VM", 32769));
            await Task.Yield();
            Require(start.Visibility == Visibility.Collapsed && save.Visibility == Visibility.Collapsed && !start.IsEnabled,
                "Stale VM state appeared on the RDP menu.");
            menu.IsOpen = false;
            await Task.Delay(100);

            window.VmActions = actions;
            var session = window.AddSession(profile, "", connect: false);
            var tabs = (ListBox)window.FindName("Tabs");
            var tabMenu = ((ListBoxItem)tabs.Items[0]).ContextMenu;
            setState(2);
            tabMenu.IsOpen = true;
            Require(tabMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Save state").IsEnabled,
                "Session tab did not expose Save state.");
            tabMenu.IsOpen = false;
            await window.RequestCloseSessionAsync(session);

            setState(3);
            bool sawPrompt = false;
            _ = window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<MessageDialog>().Single();
                sawPrompt = dialog.Title == "Start virtual machine";
                ((Button)dialog.FindName("DismissButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }), DispatcherPriority.Background);
            await window.ConnectAsync(profile);
            Require(sawPrompt && tabs.Items.Count == 0, "Declining the real Start dialog opened a session.");

            delayed = new();
            reads = 0;
            window.VmActions = new() { GetAsync = _ => { reads++; return delayed.Task; } };
            var connecting = window.ConnectAsync(profile);
            await window.ConnectAsync(profile);
            Require(reads == 1, "Repeated Connect issued concurrent VM requests.");
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            Require(!closed, "Window closed before its pending VM operation finished.");
            delayed.SetResult(new(profile.VirtualMachineId!.Value, "Saved VM", 32769));
            await connecting;
            for (int i = 0; i < 50 && !closed; i++) await Task.Delay(20);
            Require(closed && tabs.Items.Count == 0, "Pending connection created a tab after window close.");
        }
        finally
        {
            window.Close();
            Directory.Delete(sandbox, recursive: true);
        }
    }

    private static async Task RequireFailure(Func<Task> operation)
    {
        bool failed = false;
        try { await operation(); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { failed = true; }
        Require(failed, "Host failure was treated as a successful state change.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
