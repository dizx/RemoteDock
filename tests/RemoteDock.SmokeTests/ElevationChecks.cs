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

internal static class ElevationChecks
{
    internal static async Task Run()
    {
        var profile = new ConnectionProfile { Name = "Elevation test VM", Host = "localhost", Kind = ConnectionKind.HyperV,
            Port = 2179, VirtualMachineId = Guid.NewGuid() };
        var denied = new HyperVElevationRequiredException();
        Require(HyperVHost.ShouldOfferElevation(profile, denied, elevated: false), "Local access denial did not offer elevation.");
        Require(!HyperVHost.ShouldOfferElevation(profile, denied, elevated: true), "An elevated process offered elevation again.");
        Require(!HyperVHost.ShouldOfferElevation(profile with { Host = "remote.invalid" }, denied, elevated: false),
            "A remote host permission error tried to elevate locally.");
        Require(!HyperVHost.ShouldOfferElevation(profile, new TimeoutException(), elevated: false), "A network timeout offered elevation.");
        Require(!HyperVHost.IsLocal(" ") && HyperVHost.IsLocal(".") && HyperVHost.IsLocal(Environment.MachineName + "."),
            "Local-host detection confused empty input or local aliases.");
        HyperVHost.CheckEmptyInventory(profile, hasManagementRole: true);
        bool blocked = false;
        try { HyperVHost.CheckEmptyInventory(profile, hasManagementRole: false); }
        catch (HyperVElevationRequiredException) { blocked = true; }
        Require(blocked, "A permission-filtered empty VM list did not request elevation.");

        var retry = ElevationRestart.Decode(ElevationRestart.Encode(new(profile with { Name = "Draft \"VM\" — test", UserName = "discard", Domain = "discard" }, ElevationAction.Start)));
        Require(retry.Action == ElevationAction.Start && retry.Profile.VirtualMachineId == profile.VirtualMachineId &&
            retry.Profile.Name == "Draft \"VM\" — test" && retry.Profile.UserName == "" && retry.Profile.Domain == "",
            "Elevation handoff changed the target/action or retained host credentials.");

        string sandbox = Path.Combine(Path.GetTempPath(), "opencode", "RemoteDock.Elevation", Guid.NewGuid().ToString("N"));
        var store = new ConnectionStore(Path.Combine(sandbox, "connections.json"));
        var sessions = new SessionStore(Path.Combine(sandbox, "sessions.json"));
        var rdp = new ConnectionProfile { Name = "Offline preserved tab", Host = "offline.invalid" };
        store.SaveCatalog(new() { Connections = [profile, rdp] });
        var window = new MainWindow(store, sessions);
        MainWindow? replacement = null;
        try
        {
            window.Show();
            await Task.Delay(100);
            window.AddSession(rdp, "", connect: false);
            int launches = 0;
            ElevationRetry? captured = null;
            window.StartElevated = request => { launches++; captured = request; return false; };
            window.VmActions = new() { GetAsync = _ => Task.FromException<HyperVVirtualMachine>(denied) };

            if (!HyperVHost.IsElevated)
            {
                AnswerNext(window, yes: false);
                await window.ConnectAsync(profile);
                Require(launches == 0 && window.IsVisible, "Declining elevation started a process or closed the window.");

                var tree = (TreeView)window.FindName("ConnectionsList");
                tree.Items.Cast<TreeViewItem>().Single(t => ((ConnectionProfile)t.Tag).Id == profile.Id).IsSelected = true;
                tree.ContextMenu.IsOpen = true;
                await Task.Delay(50);
                Require(tree.ContextMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header == "VM state: administrator access needed"),
                    "The menu did not explain the missing VM state.");
                Require(tree.ContextMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Restart as administrator…").IsEnabled,
                    "The menu did not expose an actionable elevation option.");
                tree.ContextMenu.IsOpen = false;
                await Task.Delay(100);
            }

            AnswerNext(window, yes: true);
            Require(!window.RequestElevation(profile, ElevationAction.Connect) && launches == 1 && window.IsVisible,
                "Cancelled UAC discarded the current window.");

            var editor = new ConnectionEditor(profile) { Owner = window };
            try
            {
                editor.Show();
                ((TextBox)editor.FindName("NameBox")).Text = "Unsaved draft";
                ((TextBox)editor.FindName("VmIdBox")).Text = "";
                ((TextBox)editor.FindName("WidthBox")).Text = "1440";
                var draft = editor.CaptureHyperVDraft();
                Require(draft.Name == "Unsaved draft" && draft.VirtualMachineId is null && draft.DesktopWidth == 1440,
                    "The editor's elevation draft lost unsaved values or required a VM selection.");
                if (!HyperVHost.IsElevated)
                {
                    AnswerNext(window, yes: true);
                    await editor.LoadVirtualMachinesAsync((_, _) => Task.FromException<HyperVVirtualMachine[]>(denied));
                    Require(captured?.Action == ElevationAction.LoadVirtualMachines && captured.Profile.Name == "Unsaved draft" &&
                        captured.Profile.VirtualMachineId is null && editor.IsVisible,
                        "VM discovery did not offer restart with its unsaved draft, or cancellation closed the editor.");
                }
            }
            finally { editor.Close(); }

            window.StartElevated = request =>
            {
                captured = request;
                Require(request.DataDirectory == sandbox, "Elevation did not preserve the original configuration directory.");
                Require(sessions.Load().Contains(rdp.Id), "Open tabs were not persisted before the elevated launch.");
                return true;
            };
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            AnswerNext(window, yes: true);
            Require(window.RequestElevation(profile, ElevationAction.Inspect) && closed,
                "Successful elevated launch did not close the original window.");
            Require(sessions.Load().Contains(rdp.Id), "Closing for elevation erased the session recovery list.");

            sessions.Save([]); // Keep the restart probe offline; only Inspect is retried.
            replacement = new MainWindow(new ConnectionStore(Path.Combine(sandbox, "connections.json")), sessions, captured)
            {
                VmActions = new() { GetAsync = _ => Task.FromResult(new HyperVVirtualMachine(profile.VirtualMachineId!.Value, "Current VM name", 32769)) }
            };
            replacement.Show();
            await Task.Delay(150);
            var tabs = (ListBox)replacement.FindName("Tabs");
            Require(tabs.Items.Count == 1 && ((SessionView)((ListBoxItem)tabs.Items[0]).Tag).Status == "Current VM name · Saved",
                "The elevated restart did not retry state inspection for the requested VM.");
        }
        finally
        {
            replacement?.Close();
            window.Close();
            Directory.Delete(sandbox, recursive: true);
        }
        Console.WriteLine("PASS: local-only elevation detection; empty inventory permission handling; restart payload; declined prompt/UAC cancellation; editor draft preservation; session preservation and state retry. Elevated launch was simulated.");
    }

    private static void AnswerNext(Window window, bool yes) => _ = window.Dispatcher.BeginInvoke(new Action(() =>
    {
        var dialog = Application.Current.Windows.OfType<MessageDialog>().Single();
        Require(dialog.Title == "Administrator access needed", $"Unexpected dialog: {dialog.Title}");
        ((Button)dialog.FindName(yes ? "ConfirmButton" : "DismissButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }), DispatcherPriority.Background);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
