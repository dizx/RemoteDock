using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using AxMSTSCLib;
using RemoteDock.App;
using RemoteDock.Core;

namespace RemoteDock.SmokeTests;

// Opt-in: exercises the actual local VM, restoring its saved state after the probe.
internal static class LiveHyperVChecks
{
    internal static async Task Run(MainWindow window, Guid vmId, string output)
    {
        Directory.CreateDirectory(output);
        string log = Path.Combine(output, "live-hyperv.log");
        File.WriteAllText(log, "");
        void Log(string message) => File.AppendAllText(log, $"{DateTimeOffset.Now:O} {message}\r\n");
        var saved = new ConnectionStore(Path.Combine(RemoteDock.App.App.DataDirectory, "connections.json")).LoadCatalog();
        var profile = saved.Resolve(saved.Connections.Single(p => p.VirtualMachineId == vmId));
        if (!HyperVHost.IsLocal(profile.Host) || !HyperVHost.IsElevated)
            throw new InvalidOperationException("The live Hyper-V check requires elevation and a local VM.");
        var initial = await HyperVVirtualMachineControl.GetAsync(profile);
        Log($"Initial VM: {initial.Name}, raw state={initial.State}, label={initial.StateName}; enhanced={profile.EnhancedSession}; display={profile.DisplayMode}");
        if (!initial.IsSaved) throw new InvalidOperationException("The live cycle requires an initially saved VM.");
        SessionView? session = null;
        AxMsRdpClient9NotSafeForScripting? client = null;
        int nativeResizes = 0, remoteResizes = 0, visibilityChanges = 0, connected = 0, disconnected = 0;
        bool resumePrompt = false, savePrompt = false;
        int saveUpdates = 0, saveCoverResizes = 0;
        string? uncovered = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            foreach (var dialog in Application.Current.Windows.OfType<MessageDialog>().ToArray())
            {
                if (dialog.Title is not ("Resume virtual machine" or "Save virtual machine state")) continue;
                Log($"Accepting dialog: {dialog.Title}");
                resumePrompt |= dialog.Title == "Resume virtual machine";
                savePrompt |= dialog.Title == "Save virtual machine state";
                ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            var tabs = (ListBox)window.FindName("Tabs");
            if (session is null && tabs.Items.Count > 0)
            {
                session = (SessionView)((ListBoxItem)tabs.Items[0]).Tag;
                var panel = ((WindowsFormsHost)session.Content).Child;
                var screen = panel.Controls.OfType<System.Windows.Forms.Label>().Single();
                screen.SizeChanged += (_, _) =>
                {
                    if (!session.Status.StartsWith("Saving state")) return;
                    saveCoverResizes++;
                    if (screen.Bounds != panel.ClientRectangle)
                        uncovered ??= $"{screen.Bounds} instead of {panel.ClientRectangle}";
                    Log($"Save cover resized: {screen.Bounds}");
                };
                session.StatusChanged += (_, _) =>
                {
                    Log($"Status: {session.Status}");
                    if (!session.Status.StartsWith("Saving state")) return;
                    saveUpdates++;
                    if (!screen.Visible || screen.Bounds != panel.ClientRectangle)
                        uncovered ??= $"visible={screen.Visible}, {screen.Bounds} instead of {panel.ClientRectangle}";
                };
            }
            if (client is not null || session is null) return;
            client = ((WindowsFormsHost)session.Content).Child.Controls.OfType<AxMsRdpClient9NotSafeForScripting>().SingleOrDefault();
            if (client is null) return;
            client.SizeChanged += (_, _) => { nativeResizes++; if (nativeResizes <= 30) Log($"Native resize: {client.Bounds}"); };
            client.VisibleChanged += (_, _) => { visibilityChanges++; Log($"Native visible: {client.Visible}"); };
            client.OnConnected += (_, _) => { connected++; Log("RDP OnConnected"); };
            client.OnLoginComplete += (_, _) => Log("RDP OnLoginComplete");
            client.OnDisconnected += (_, e) => { disconnected++; Log($"RDP OnDisconnected: {e.discReason}"); };
            client.OnRemoteDesktopSizeChange += (_, e) => { remoteResizes++; if (remoteResizes <= 30) Log($"Remote size: {e.width}x{e.height}"); };
        };
        timer.Start();
        window.Activate();
        try
        {
            await window.ConnectAsync(profile);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(45) && (client is null || client.Connected != 1)) await Task.Delay(100);
            Log($"After connection wait: status={session?.Status}, connected={client?.Connected}");
            if (!resumePrompt || client?.Connected != 1) throw new InvalidOperationException("The real resume/console connection did not succeed.");
            await Task.Delay(4000);
            int startResizes = nativeResizes, startVisibility = visibilityChanges, startDisconnects = disconnected;
            await Task.Delay(8000);
            Log($"Idle 8s: nativeResizes={nativeResizes - startResizes}, visibilityChanges={visibilityChanges - startVisibility}, disconnects={disconnected - startDisconnects}, remoteResizesTotal={remoteResizes}, connectionsTotal={connected}; status={session?.Status}");
            var point = window.PointToScreen(new System.Windows.Point());
            var bottomRight = window.PointToScreen(new System.Windows.Point(window.ActualWidth, window.ActualHeight));
            using (var image = new Bitmap((int)(bottomRight.X - point.X), (int)(bottomRight.Y - point.Y)))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.CopyFromScreen((int)point.X, (int)point.Y, 0, 0, image.Size);
                image.Save(Path.Combine(output, "live-connected.png"));
            }
            if (client.Connected != 1 || nativeResizes - startResizes > 4 || visibilityChanges != startVisibility || disconnected != startDisconnects)
                throw new InvalidOperationException("The real console is unstable while idle (see native event counts).");
            await window.RequestCloseSessionAsync(session!);
            var final = await HyperVVirtualMachineControl.GetAsync(profile);
            Log($"Final VM: raw state={final.State}, label={final.StateName}; savePrompt={savePrompt}");
            Log($"Save progress: updates={saveUpdates}, coverResizes={saveCoverResizes}, uncovered={uncovered ?? "none"}");
            if (!savePrompt || !final.IsSaved) throw new InvalidOperationException("The real disconnect/save did not reach Saved.");
            if (saveUpdates < 2 || saveCoverResizes != 0 || uncovered is not null)
                throw new InvalidOperationException("Save progress did not keep a stable opaque cover over the RDP surface.");
            Log("PASS: real saved VM resume prompt, WMI resume, RDP connection, idle native stability, stable opaque save-progress cover, disconnect/save prompt and WMI save completion.");
        }
        catch (Exception ex) { Log($"FAIL: {ex}"); throw; }
        finally
        {
            timer.Stop();
            var current = await HyperVVirtualMachineControl.GetAsync(profile);
            if (current.CanSave)
            {
                Log("Restoring the VM's initial saved state after the probe.");
                await HyperVVirtualMachineControl.ChangeAsync(profile, HyperVAction.Save);
                Log("Restored the VM to Saved.");
            }
            if (session is not null) await window.RequestCloseSessionAsync(session);
        }
    }
}
