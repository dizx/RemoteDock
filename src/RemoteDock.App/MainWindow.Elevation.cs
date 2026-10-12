using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RemoteDock.Core;

namespace RemoteDock.App;

public partial class MainWindow
{
    private readonly ElevationRetry? _elevationRetry;
    private bool _elevationPromptOpen;
    internal Func<ElevationRetry, bool> StartElevated { get; set; } = ElevationRestart.Start;

    internal bool RequestElevation(ConnectionProfile profile, ElevationAction action, Window? owner = null)
    {
        if (_closing || _closeRequested || _elevationPromptOpen) return false;
        _elevationPromptOpen = true;
        try
        {
            if (MessageDialog.ShowMessage(owner ?? this,
                $"Hyper-V on '{profile.Host}' needs administrator access for this operation.\n\nRestart RemoteDock as administrator and retry? Open tabs will be remembered.",
                "Administrator access needed", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;

            // Startup can request elevation before all previous tabs have reopened.
            // Carry forward both open tabs and the remaining accepted restore queue.
            if (_sessionStore is not null && _sessionStateReady && _storeReadable)
            {
                var known = _catalog.Connections.Select(p => p.Id).ToHashSet();
                _sessionStore.Save(Tabs.Items.Cast<ListBoxItem>().Select(t => ((SessionView)t.Tag).Profile.Id)
                    .Concat(_pendingRestoreIds).Where(known.Contains).Distinct());
            }
            if (!StartElevated(new(profile, action, DataDirectory: _store.DirectoryPath)))
            {
                SetStatusText("Elevation cancelled. RemoteDock is still running.");
                return false;
            }
            // Restarting only disconnects consoles; it must not save or stop running VMs.
            FinishClosing();
            Close();
            return true;
        }
        catch (Exception ex)
        {
            MessageDialog.ShowMessage(owner ?? this, ex.Message, "Cannot restart as administrator", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { _elevationPromptOpen = false; }
    }

    private async Task RetryElevatedActionAsync(ElevationRetry retry)
    {
        switch (retry.Action)
        {
            case ElevationAction.Connect:
                await ConnectAsync(retry.Profile);
                break;
            case ElevationAction.LoadVirtualMachines:
                EditConnection(retry.Profile, loadVirtualMachines: true);
                break;
            case ElevationAction.Start:
            case ElevationAction.Save:
                await ChangeVmStateAsync(retry.Profile, retry.Action == ElevationAction.Start ? HyperVAction.Start : HyperVAction.Save);
                break;
            case ElevationAction.Disconnect:
                var closing = AddSession(retry.Profile, "", deferConnect: true);
                try
                {
                    await VmActions.PrepareDisconnectAsync(retry.Profile, ConfirmVmAction, closing.ReportOperation);
                    CloseSession(closing);
                }
                catch (Exception ex) { closing.FinishOperation($"Could not save or check VM state: {ex.Message}"); }
                break;
            case ElevationAction.Inspect:
                var session = AddSession(retry.Profile, "", deferConnect: true);
                try
                {
                    var vm = await VmActions.GetAsync(retry.Profile);
                    session.FinishOperation($"{vm.Name} · {vm.StateName}");
                }
                catch (Exception ex) { session.FinishOperation(ex.Message); }
                break;
        }
    }
}
