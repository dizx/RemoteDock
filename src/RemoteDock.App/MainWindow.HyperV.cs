using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RemoteDock.Core;

namespace RemoteDock.App;

public partial class MainWindow
{
    private readonly SemaphoreSlim _vmOperation = new(1, 1);
    private bool _closeRequested;
    internal HyperVSessionActions VmActions { get; set; } = new();

    private bool ConfirmVmAction(string message, string title)
    {
        bool confirmed = MessageDialog.ShowMessage(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (confirmed) SetStatusText($"{title}…");
        return confirmed;
    }

    private bool TryBeginVmOperation(ConnectionProfile profile)
    {
        if (_closing || _closeRequested) return false;
        if (profile.Kind != ConnectionKind.HyperV) return true;
        if (!_vmOperation.Wait(0))
        {
            SetStatusText("A Hyper-V operation is already in progress. Try again when it finishes.");
            return false;
        }
        SetStatusText($"Checking virtual machine on {profile.Host}…");
        return true;
    }

    private void EndVmOperation(ConnectionProfile profile, bool keepStatus = false)
    {
        if (profile.Kind != ConnectionKind.HyperV) return;
        _vmOperation.Release();
        if (!keepStatus && !_closing) UpdateStatus();
    }

    internal async Task RequestCloseSessionAsync(SessionView session)
    {
        var profile = session.Profile;
        if (!SessionSurface.Children.Contains(session) || !TryBeginVmOperation(profile)) return;
        try
        {
            if (await PrepareSessionDisconnectAsync(session)) CloseSession(session);
        }
        finally { EndVmOperation(profile); }
    }

    private async Task<bool> PrepareSessionDisconnectAsync(SessionView session)
    {
        if (!session.ConnectsToHost) return true;
        try
        {
            await VmActions.PrepareDisconnectAsync(session.Profile, ConfirmVmAction, session.ReportOperation);
            return true;
        }
        catch (Exception ex) when (!_closeRequested && HyperVHost.ShouldOfferElevation(session.Profile, ex))
        {
            session.FinishOperation("Administrator access is needed for local Hyper-V management.");
            RequestElevation(session.Profile, ElevationAction.Disconnect);
            return false;
        }
        catch (Exception ex)
        {
            session.FinishOperation($"Could not save or check VM state: {ex.Message}");
            return ConfirmVmAction($"Could not save or check the state of '{session.Profile.Name}': {ex.Message}\n\nDisconnect anyway?", "Virtual machine state unavailable");
        }
        finally { session.FinishOperation(); }
    }

    private void AddVmMenuItems(ContextMenu menu, Func<ConnectionProfile?> getProfile, int? insertAt = null)
    {
        var separator = new Separator { Visibility = Visibility.Collapsed };
        var state = new MenuItem { Header = "VM state: checking…", IsEnabled = false, Visibility = Visibility.Collapsed };
        var start = new MenuItem { Header = "Start", Visibility = Visibility.Collapsed };
        var save = new MenuItem { Header = "Save state", Visibility = Visibility.Collapsed };
        ToolTipService.SetShowOnDisabled(state, true);
        ToolTipService.SetShowOnDisabled(start, true);
        ToolTipService.SetShowOnDisabled(save, true);
        int index = insertAt ?? menu.Items.Count;
        menu.Items.Insert(index, separator);
        menu.Items.Insert(index + 1, state);
        menu.Items.Insert(index + 2, start);
        menu.Items.Insert(index + 3, save);
        ConnectionProfile? menuProfile = null;
        bool needsElevation = false;
        int generation = 0;
        menu.Closed += (_, _) => generation++;
        menu.Opened += async (_, _) =>
        {
            int request = ++generation;
            menuProfile = getProfile();
            var profile = menuProfile;
            bool hyperV = profile?.Kind == ConnectionKind.HyperV;
            separator.Visibility = state.Visibility = start.Visibility = save.Visibility = hyperV ? Visibility.Visible : Visibility.Collapsed;
            needsElevation = false;
            state.Header = "VM state: checking…";
            state.ToolTip = null;
            start.Header = "Start";
            start.IsEnabled = save.IsEnabled = false;
            start.ToolTip = save.ToolTip = "Checking VM state…";
            if (!hyperV || _closing || _closeRequested) return;
            if (_vmOperation.CurrentCount == 0)
            {
                start.ToolTip = save.ToolTip = "A Hyper-V operation is already in progress.";
                state.Header = "VM state: operation in progress…";
                return;
            }
            try
            {
                var vm = await VmActions.GetAsync(profile!);
                if (request != generation || !menu.IsOpen) return;
                state.Header = $"VM state: {vm.StateName}";
                start.Header = vm.StartAction;
                start.IsEnabled = vm.CanStart && _vmOperation.CurrentCount != 0;
                save.IsEnabled = vm.CanSave && _vmOperation.CurrentCount != 0;
                start.ToolTip = save.ToolTip = $"{vm.Name} · {vm.StateName}";
            }
            catch (Exception ex)
            {
                if (request == generation && menu.IsOpen)
                {
                    start.ToolTip = save.ToolTip = $"Could not check VM state: {ex.Message}";
                    needsElevation = HyperVHost.ShouldOfferElevation(profile!, ex);
                    state.Header = needsElevation ? "VM state: administrator access needed" : "VM state: unavailable";
                    state.ToolTip = ex.Message;
                    if (needsElevation)
                    {
                        start.Header = "Restart as administrator…";
                        start.IsEnabled = true;
                    }
                }
            }
        };
        start.Click += async (_, _) =>
        {
            if (menuProfile is not { } profile) return;
            if (needsElevation) RequestElevation(profile, ElevationAction.Inspect);
            else await ChangeVmStateAsync(profile, HyperVAction.Start);
        };
        save.Click += async (_, _) => { if (menuProfile is { } profile) await ChangeVmStateAsync(profile, HyperVAction.Save); };
    }

    private async Task ChangeVmStateAsync(ConnectionProfile profile, HyperVAction action)
    {
        if (!TryBeginVmOperation(profile)) return;
        SessionView? session = null;
        try
        {
            session = ActivateExistingSession(profile) ?? AddSession(profile, "", deferConnect: true);
            string description = action == HyperVAction.Start ? "Starting / resuming" : "Saving state";
            await VmActions.ChangeWithProgressAsync(profile, action, description, session.ReportOperation);
            session.FinishOperation(action == HyperVAction.Start ? "Running · 100%\nReady to connect." : "Saved · 100%\nResume to continue working.");
        }
        catch (Exception ex) when (HyperVHost.ShouldOfferElevation(profile, ex))
        {
            session?.FinishOperation("Administrator access is needed for local Hyper-V management.");
            RequestElevation(profile, action == HyperVAction.Start ? ElevationAction.Start : ElevationAction.Save);
        }
        catch (Exception ex)
        {
            if (session is not null) session.FinishOperation($"Hyper-V operation failed: {ex.Message}");
            else SetStatusText($"Hyper-V operation failed: {ex.Message}");
        }
        finally { EndVmOperation(profile, keepStatus: true); }
    }
}
