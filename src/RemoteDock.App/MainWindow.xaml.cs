using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteDock.Core;

namespace RemoteDock.App;

public partial class MainWindow : Window
{
    private readonly ConnectionStore _store;
    private readonly SessionStore? _sessionStore;
    private bool _sessionStateReady;
    private bool _restoringSessions;
    private bool _closing;
    private ConnectionCatalog _catalog = new();
    private readonly DispatcherTimer _sidebarHideTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private bool _storeReadable = true;
    private bool _focusMode;
    private bool _fullScreen;
    private WindowState _previousState;
    private SessionView? ActiveSession => (Tabs.SelectedItem as ListBoxItem)?.Tag as SessionView;
    private object? SelectedEntry => (ConnectionsList.SelectedItem as TreeViewItem)?.Tag;
    private Guid? SelectedGroupId => SelectedEntry switch { ConnectionGroup g => g.Id, ConnectionProfile p => p.GroupId, _ => null };

    public MainWindow() : this(new ConnectionStore(Path.Combine(App.DataDirectory, "connections.json")),
        new SessionStore(Path.Combine(App.DataDirectory, "sessions.json"))) { }

    internal MainWindow(ConnectionStore store, SessionStore? sessionStore = null)
    {
        _store = store;
        _sessionStore = sessionStore;
        InitializeComponent();
        _sidebarHideTimer.Tick += HideSidebarAfterDelay;
        try { _catalog = _store.LoadCatalog(); }
        catch (Exception ex)
        {
            _storeReadable = false;
            StatusText.Text = $"Connections could not be loaded: {ex.Message}";
            Loaded += (_, _) => MessageDialog.ShowMessage(this,
                $"The connection file could not be read and has been preserved.\n\n{ex.Message}\n\n{App.DataDirectory}",
                "Connection file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        AutoHideBox.IsChecked = _catalog.AutoHideConnections;
        RefreshConnections();
        UpdateSidebarLayout();
        ContentRendered += RestorePreviousConnections;
    }

    private void RestorePreviousConnections(object? sender, EventArgs e)
    {
        ContentRendered -= RestorePreviousConnections;
        if (_sessionStore is null || !_storeReadable || _closing) return;
        try
        {
            var profiles = _catalog.Connections.ToDictionary(p => p.Id);
            var previous = _sessionStore.Load().Where(profiles.ContainsKey).Select(id => profiles[id]).ToArray();
            _sessionStateReady = true;
            if (previous.Length == 0) return;

            _restoringSessions = true;
            string names = string.Join(Environment.NewLine, previous.Select(p => p.Name));
            var answer = MessageDialog.ShowMessage(this,
                $"Reopen the connections from your previous session?\n\n{names}",
                "Restore previous connections", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                foreach (var profile in previous)
                {
                    if (_closing) break;
                    Connect(profile);
                }
            }
            _restoringSessions = false;
            SaveOpenSessions();
        }
        catch (Exception ex)
        {
            _sessionStateReady = true;
            StatusText.Text = $"Previous connections could not be restored: {ex.Message}";
            StatusText.ToolTip = StatusText.Text;
        }
        finally { _restoringSessions = false; }
    }

    private void SaveOpenSessions()
    {
        if (_sessionStore is null || !_sessionStateReady || _restoringSessions || _closing) return;
        try
        {
            var knownIds = _catalog.Connections.Select(p => p.Id).ToHashSet();
            _sessionStore.Save(Tabs.Items.Cast<ListBoxItem>().Select(t => ((SessionView)t.Tag).Profile.Id).Where(knownIds.Contains));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Open connections could not be remembered: {ex.Message}";
            StatusText.ToolTip = StatusText.Text;
        }
    }

    private static Guid? EntryId(object? entry) => entry switch { ConnectionProfile p => p.Id, ConnectionGroup g => g.Id, _ => null };

    private void RefreshConnections(Guid? selectedId = null)
    {
        if (ConnectionsList is null) return;
        selectedId ??= EntryId(SelectedEntry);
        var expanded = TreeItems(ConnectionsList.Items).Where(i => i.Tag is ConnectionGroup)
            .ToDictionary(i => ((ConnectionGroup)i.Tag).Id, i => i.IsExpanded);
        string search = SearchBox.Text.Trim();
        bool Matches(string value) => value.Contains(search, StringComparison.OrdinalIgnoreCase);

        List<TreeViewItem> Build(Guid? parent, bool includeAll)
        {
            var items = new List<TreeViewItem>();
            foreach (var group in _catalog.OrderedGroups(parent))
            {
                bool groupMatch = includeAll || Matches(group.Name);
                var children = Build(group.Id, groupMatch);
                if (!groupMatch && children.Count == 0) continue;
                var item = new TreeViewItem { Header = group.Name, Tag = group, Foreground = ConnectionsList.Foreground,
                    Style = (Style)FindResource("CompactTreeItemStyle"),
                    IsExpanded = search.Length > 0 || !expanded.TryGetValue(group.Id, out bool wasExpanded) || wasExpanded,
                    Margin = new Thickness(0, 2, 0, 2) };
                foreach (var child in children) item.Items.Add(child);
                items.Add(item);
            }
            foreach (var profile in _catalog.OrderedConnections(parent).Where(p => includeAll || Matches(p.Name) || Matches(p.Host)))
            {
                var header = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                header.Children.Add(new TextBlock { Text = profile.Name, FontWeight = FontWeights.SemiBold });
                header.Children.Add(new TextBlock { Text = profile.Host, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(145, 165, 187)) });
                var item = new TreeViewItem { Header = header, Tag = profile, ToolTip = profile.Host, Foreground = ConnectionsList.Foreground,
                    Style = (Style)FindResource("ConnectionTreeItemStyle") };
                System.Windows.Automation.AutomationProperties.SetName(item, profile.Name);
                items.Add(item);
            }
            return items;
        }

        ConnectionsList.Items.Clear();
        foreach (var item in Build(null, search.Length == 0)) ConnectionsList.Items.Add(item);
        var selected = TreeItems(ConnectionsList.Items).FirstOrDefault(i => EntryId(i.Tag) == selectedId);
        if (selected is not null) selected.IsSelected = true;
        UpdateSortButton();
        UpdateWelcome();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var order = _catalog.SortOrder == ConnectionSortOrder.Alphabetical ? ConnectionSortOrder.LastConnected : ConnectionSortOrder.Alphabetical;
        if (SaveCatalog(_catalog with { SortOrder = order })) RefreshConnections();
    }

    private void UpdateSortButton()
    {
        bool recent = _catalog.SortOrder == ConnectionSortOrder.LastConnected;
        AlphabeticalSortIcon.Visibility = recent ? Visibility.Collapsed : Visibility.Visible;
        RecentSortIcon.Visibility = recent ? Visibility.Visible : Visibility.Collapsed;
        SortButton.ToolTip = recent ? "Sorted by last connected · Click for alphabetical" : "Sorted alphabetically · Click for last connected";
        System.Windows.Automation.AutomationProperties.SetName(SortButton,
            recent ? "Sort connections: last connected" : "Sort connections: alphabetical");
    }

    private static IEnumerable<TreeViewItem> TreeItems(ItemCollection items)
    {
        foreach (TreeViewItem item in items)
        {
            yield return item;
            foreach (var child in TreeItems(item.Items)) yield return child;
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshConnections();
    private void Connection_Selected(object sender, RoutedPropertyChangedEventArgs<object> e) => UpdateWelcome();
    private void New_Click(object sender, RoutedEventArgs e) => EditConnection(null);
    private void NewGroup_Click(object sender, RoutedEventArgs e) => EditGroup(new() { ParentId = SelectedGroupId });
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is ConnectionProfile profile) EditConnection(profile);
        else if (SelectedEntry is ConnectionGroup group) EditGroup(group);
    }

    private void EditConnection(ConnectionProfile? existing)
    {
        try
        {
            bool hasPassword = existing is not null && CredentialStore.Read(CredentialStore.ConnectionTarget(existing.Id)) is not null;
            var editor = new ConnectionEditor(existing, _catalog, SelectedGroupId, hasPassword) { Owner = this };
            if (editor.ShowDialog() != true || editor.Result is not { } profile) return;
            var updated = _catalog with { Connections = _catalog.Connections.Where(p => p.Id != profile.Id).Append(profile).ToArray() };
            if (!SaveCatalog(updated, CredentialStore.ConnectionTarget(profile.Id), profile.Kind != ConnectionKind.HyperV && editor.RememberPassword,
                editor.TakePassword(), _catalog.Resolve(profile).UserName)) return;
            RefreshConnections(profile.Id);
            StatusText.Text = "Connection saved. Reconnect to apply updated settings.";
        }
        catch (Exception ex) { ShowError(ex, "Connection settings"); }
    }

    private void EditGroup(ConnectionGroup group)
    {
        try
        {
            bool hasPassword = CredentialStore.Read(CredentialStore.GroupTarget(group.Id)) is not null;
            var editor = new ConnectionEditor(group, _catalog, hasPassword) { Owner = this };
            if (editor.ShowDialog() != true || editor.GroupResult is not { } result) return;
            var updated = _catalog with { Groups = _catalog.Groups.Where(g => g.Id != result.Id).Append(result).ToArray() };
            if (!SaveCatalog(updated, CredentialStore.GroupTarget(result.Id), editor.RememberPassword, editor.TakePassword(), updated.GroupDefaults(result.Id).UserName)) return;
            RefreshConnections(result.Id);
            StatusText.Text = "Group saved. Its inherited settings apply when connections are opened or reconnected.";
        }
        catch (Exception ex) { ShowError(ex, "Group settings"); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ImportDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } imported) return;
        if (!CommitImport(imported)) return;
        RefreshConnections(imported.Connections.FirstOrDefault()?.Id);
        int passwords = imported.Passwords.Count(p => !string.IsNullOrEmpty(p.Password) || p.WindowsProtected is not null);
        StatusText.Text = $"Imported {imported.Connections.Length} connections, {imported.Groups.Length} groups and {passwords} passwords. Skipped {imported.SkippedCount} items.";
        if (imported.Warnings.Count > 0) MessageDialog.ShowMessage(this, string.Join(Environment.NewLine, imported.Warnings.Distinct()), "Import notes");
    }

    internal bool CommitImport(ConnectionImport imported)
    {
        var previous = new Dictionary<string, SavedCredential?>();
        var changed = new List<string>();
        try
        {
            if (!_storeReadable) throw new IOException("Repair the existing connection file before importing.");
            var catalog = imported.MergeInto(_catalog);
            foreach (var item in imported.Passwords)
            {
                string target = item.IsGroup ? CredentialStore.GroupTarget(item.OwnerId) : CredentialStore.ConnectionTarget(item.OwnerId);
                string? password = item.WindowsProtected is null ? item.Password : WindowsDataProtection.UnprotectRdpPassword(item.WindowsProtected);
                previous.TryAdd(target, CredentialStore.Read(target));
                if (string.IsNullOrEmpty(password)) CredentialStore.Delete(target);
                else
                {
                    string userName = item.IsGroup ? catalog.GroupDefaults(item.OwnerId).UserName
                        : catalog.Resolve(catalog.Connections.Single(p => p.Id == item.OwnerId)).UserName;
                    CredentialStore.Write(target, userName, password);
                }
                changed.Add(target);
            }
            _store.SaveCatalog(catalog);
            _catalog = catalog;
            return true;
        }
        catch (Exception ex)
        {
            foreach (string target in changed.Distinct().Reverse())
            {
                try
                {
                    if (previous[target] is { } saved) CredentialStore.Write(target, saved.UserName, saved.Password);
                    else CredentialStore.Delete(target);
                }
                catch (Exception rollback) { ex = new AggregateException("Import failed and a previous credential could not be restored.", ex, rollback); }
            }
            ShowError(ex, "Cannot import connections");
            return false;
        }
    }

    private bool SaveCatalog(ConnectionCatalog catalog, string? target = null, bool? remember = null, string? password = null, string userName = "")
    {
        var previous = new Dictionary<string, SavedCredential?>();
        try
        {
            if (!_storeReadable) throw new IOException("Repair the existing connection file before saving changes.");
            catalog.Validate();
            if (target is not null && remember.HasValue)
            {
                if (!remember.Value)
                {
                    previous[target] = CredentialStore.Read(target);
                    CredentialStore.Delete(target);
                }
                else if (!string.IsNullOrEmpty(password))
                {
                    previous[target] = CredentialStore.Read(target);
                    CredentialStore.Write(target, userName, password);
                }
            }
            _store.SaveCatalog(catalog);
            _catalog = catalog;
            return true;
        }
        catch (Exception ex)
        {
            foreach (var entry in previous.Reverse())
            {
                try
                {
                    if (entry.Value is null) CredentialStore.Delete(entry.Key);
                    else CredentialStore.Write(entry.Key, entry.Value.UserName, entry.Value.Password);
                }
                catch (Exception rollback) { ex = new AggregateException("Saving failed and the previous credential could not be restored.", ex, rollback); }
            }
            ShowError(ex, "Cannot save connections");
            return false;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is ConnectionProfile profile)
        {
            if (MessageDialog.ShowMessage(this, $"Delete saved connection '{profile.Name}' and its saved password?", "Delete connection",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (SaveCatalog(_catalog with { Connections = _catalog.Connections.Where(p => p.Id != profile.Id).ToArray() }, CredentialStore.ConnectionTarget(profile.Id), false)) RefreshConnections();
        }
        else if (SelectedEntry is ConnectionGroup group)
        {
            if (_catalog.Connections.Any(p => p.GroupId == group.Id) || _catalog.Groups.Any(g => g.ParentId == group.Id))
            {
                MessageDialog.ShowMessage(this, "Move this group's connections and subgroups before deleting it.", "Group is not empty");
                return;
            }
            if (MessageDialog.ShowMessage(this, $"Delete group '{group.Name}' and its shared password?", "Delete group", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            if (SaveCatalog(_catalog with { Groups = _catalog.Groups.Where(g => g.Id != group.Id).ToArray() }, CredentialStore.GroupTarget(group.Id), false)) RefreshConnections();
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e) { if (SelectedEntry is ConnectionProfile profile) Connect(profile); }
    private void ConnectAs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is not ConnectionProfile profile) return;
        if (profile.Kind == ConnectionKind.HyperV) EditConnection(profile);
        else Connect(profile, prompt: true);
    }

    private static TreeViewItem? ItemUnderPointer(DependencyObject? element)
    {
        while (element is not null && element is not TreeViewItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : (element as FrameworkContentElement)?.Parent;
        return element as TreeViewItem;
    }

    private void Connection_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnderPointer(e.OriginalSource as DependencyObject) is { } item) { item.IsSelected = true; item.Focus(); }
    }

    private void Connection_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnderPointer(e.OriginalSource as DependencyObject)?.Tag is ConnectionProfile profile) { e.Handled = true; Connect(profile); }
    }

    private void Connection_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SelectedEntry is ConnectionProfile profile) { e.Handled = true; Connect(profile); }
        else if (e.Key == Key.F2) { e.Handled = true; Edit_Click(sender, e); }
    }

    private SavedCredential? FindCredential(ConnectionProfile profile)
    {
        if (profile.UsesWindowsHostAuthentication) return null;
        var credential = CredentialStore.Read(CredentialStore.ConnectionTarget(profile.Id));
        if (credential is not null || !profile.Inherit.HasFlag(InheritedSettings.Password)) return credential;
        foreach (var group in _catalog.GroupChain(profile.GroupId).Reverse())
        {
            credential = CredentialStore.Read(CredentialStore.GroupTarget(group.Id));
            if (credential is not null) return credential;
            if (!group.InheritPassword) break;
        }
        return null;
    }

    private void Connect(ConnectionProfile profile, bool prompt = false)
    {
        try
        {
            var effective = _catalog.Resolve(profile);
            if (ActivateExistingSession(effective) is not null) return;
            var saved = FindCredential(effective);
            string password = saved?.Password ?? "";
            if (!effective.UsesWindowsHostAuthentication && (prompt || (saved is null && !string.IsNullOrWhiteSpace(effective.UserName))))
            {
                var credentials = new CredentialsDialog(effective) { Owner = this };
                if (credentials.ShowDialog() != true) return;
                password = credentials.TakePassword();
                bool identityChanged = credentials.UserName != effective.UserName || credentials.Domain != effective.Domain;
                var updatedProfile = identityChanged ? profile with { UserName = credentials.UserName, Domain = credentials.Domain,
                    Inherit = profile.Inherit & ~InheritedSettings.SignIn } : profile;
                if (identityChanged || credentials.RememberPassword)
                {
                    var updated = _catalog with { Connections = _catalog.Connections.Select(p => p.Id == profile.Id ? updatedProfile : p).ToArray() };
                    if (!SaveCatalog(updated, credentials.RememberPassword ? CredentialStore.ConnectionTarget(profile.Id) : null,
                        credentials.RememberPassword ? true : null, password, credentials.UserName)) return;
                    RefreshConnections(profile.Id);
                }
                effective = _catalog.Resolve(updatedProfile);
            }
            AddSession(effective, password);
        }
        catch (Exception ex) { ShowError(ex, "Cannot connect"); }
    }

    internal SessionView AddSession(ConnectionProfile profile, string password, bool connect = true)
    {
        if (ActivateExistingSession(profile) is { } existing) return existing;
        var session = new SessionView(profile, password, connect);
        session.StatusChanged += Session_StatusChanged;
        session.ConnectionEstablished += Session_ConnectionEstablished;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = profile.Name.ReplaceLineEndings(" "), TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200, ToolTip = profile.Name,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 6, 0) });
        var close = new Button { Content = "×", Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(0), ToolTip = "Close and disconnect session" };
        close.Click += (_, e) => { e.Handled = true; CloseSession(session); };
        header.Children.Add(close);
        var tab = new ListBoxItem { Content = header, Tag = session, Padding = new Thickness(3, 2, 3, 2), ToolTip = profile.Host, ContextMenu = new ContextMenu() };
        var edit = new MenuItem { Header = "Edit connection", Icon = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\uE70F" } };
        edit.Click += (_, _) => { if (_catalog.Connections.FirstOrDefault(p => p.Id == session.Profile.Id) is { } saved) EditConnection(saved); };
        tab.ContextMenu.Items.Add(edit);
        var focusButton = new Button { Tag = tab, Style = (Style)FindResource("FocusSessionButton") };
        focusButton.Click += FocusSession_Click;
        FocusSessionButtons.Children.Add(focusButton);
        SessionSurface.Children.Add(session);
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateWelcome();
        SaveOpenSessions();
        return session;
    }

    private SessionView? ActivateExistingSession(ConnectionProfile profile, SessionView? except = null)
    {
        var tab = Tabs.Items.Cast<ListBoxItem>().FirstOrDefault(t =>
        {
            var session = (SessionView)t.Tag;
            var open = session.Profile;
            return session != except && open.Kind == profile.Kind && open.Port == profile.Port &&
                string.Equals(open.Host.Trim().TrimEnd('.'), profile.Host.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase) &&
                (profile.Kind != ConnectionKind.HyperV || open.VirtualMachineId == profile.VirtualMachineId);
        });
        if (tab is null) return null;
        Tabs.SelectedItem = tab;
        Tabs.ScrollIntoView(tab);
        var existing = (SessionView)tab.Tag;
        existing.Activate();
        return existing;
    }

    private void Tab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SessionSurface is null) return;
        foreach (var session in SessionSurface.Children.OfType<SessionView>()) session.Visibility = session == ActiveSession ? Visibility.Visible : Visibility.Collapsed;
        ActiveSession?.Activate();
        UpdateStatus();
        RevealFocusSession();
    }

    private void Session_StatusChanged(object? sender, EventArgs e) => UpdateStatus();
    private void Session_ConnectionEstablished(object? sender, EventArgs e)
    {
        if (!_storeReadable || sender is not SessionView session || !_catalog.Connections.Any(p => p.Id == session.Profile.Id)) return;
        var updated = _catalog.RecordConnected(session.Profile.Id, DateTimeOffset.UtcNow);
        try
        {
            _store.SaveCatalog(updated);
            _catalog = updated;
            RefreshConnections();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Connected, but last-connected history could not be saved: {ex.Message}";
            StatusText.ToolTip = StatusText.Text;
        }
    }
    private void UpdateStatus()
    {
        StatusText.Text = ActiveSession?.Status ?? "Ready";
        StatusText.ToolTip = StatusText.Text;
        Title = ActiveSession is { } active ? $"{active.Profile.Name} — RemoteDock" : "RemoteDock";
        ReconnectButton.IsEnabled = ActiveSession is not null;
    }

    private void UpdateWelcome()
    {
        if (WelcomeAction is null) return;
        Welcome.Visibility = Tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var profile = SelectedEntry as ConnectionProfile ?? _catalog.OrderConnections(_catalog.Connections).FirstOrDefault();
        WelcomeAction.Content = profile is null ? "Add your first connection" : $"Connect to {profile.Name}";
        WelcomeDescription.Text = profile is null
            ? "Connect to a Windows desktop or Hyper-V console. Fit your workspace with fewer remote pixels and more room to work."
            : "Your saved connections are ready. Choose one from the connections panel or open it below.";
    }

    private void Welcome_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedEntry as ConnectionProfile ?? _catalog.OrderConnections(_catalog.Connections).FirstOrDefault();
        if (profile is null) EditConnection(null); else Connect(profile);
    }

    private void CloseSession(SessionView session)
    {
        var tab = Tabs.Items.Cast<ListBoxItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, session));
        session.StatusChanged -= Session_StatusChanged;
        session.ConnectionEstablished -= Session_ConnectionEstablished;
        session.Dispose();
        SessionSurface.Children.Remove(session);
        var focusButton = FocusSessionButtons.Children.OfType<Button>().FirstOrDefault(b => ReferenceEquals(b.Tag, tab));
        if (focusButton is not null) FocusSessionButtons.Children.Remove(focusButton);
        if (tab is not null) Tabs.Items.Remove(tab);
        if (Tabs.SelectedItem is null && Tabs.Items.Count > 0) Tabs.SelectedIndex = Tabs.Items.Count - 1;
        UpdateWelcome();
        UpdateStatus();
        SaveOpenSessions();
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveSession is not { } session) return;
        try
        {
            var profile = _catalog.Connections.FirstOrDefault(p => p.Id == session.Profile.Id) ?? session.Profile;
            var effective = _catalog.Resolve(profile);
            if (ActivateExistingSession(effective, session) is not null) return;
            session.Reconnect(effective, FindCredential(profile)?.Password);
            if (Tabs.SelectedItem is ListBoxItem { Content: StackPanel header })
            {
                var label = (TextBlock)header.Children[0];
                label.Text = session.Profile.Name.ReplaceLineEndings(" ");
                label.ToolTip = session.Profile.Name;
            }
        }
        catch (Exception ex) { ShowError(ex, "Cannot reconnect"); }
    }

    private void AutoHide_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCatalog(_catalog with { AutoHideConnections = AutoHideBox.IsChecked == true }))
            AutoHideBox.IsChecked = _catalog.AutoHideConnections;
        UpdateSidebarLayout();
    }

    private void UpdateSidebarLayout()
    {
        SidebarPopup.IsOpen = false;
        if (_catalog.AutoHideConnections)
        {
            SidebarDockHost.Content = null;
            SidebarPopup.Child = Sidebar;
            Sidebar.Width = 240;
        }
        else
        {
            SidebarPopup.Child = null;
            SidebarDockHost.Content = Sidebar;
            Sidebar.Width = Sidebar.Height = double.NaN;
        }
        Sidebar.Visibility = _focusMode ? Visibility.Collapsed : Visibility.Visible;
        SidebarDockHost.Visibility = _focusMode || _catalog.AutoHideConnections ? Visibility.Collapsed : Visibility.Visible;
        SidebarRail.Visibility = !_focusMode && _catalog.AutoHideConnections ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(_focusMode ? 0 : _catalog.AutoHideConnections ? 22 : 240);
    }

    private void RevealSidebar(object sender, RoutedEventArgs e)
    {
        if (!_catalog.AutoHideConnections || _focusMode) return;
        _sidebarHideTimer.Stop();
        Sidebar.Height = Math.Max(200, Workspace.ActualHeight);
        SidebarPopup.IsOpen = true;
    }

    private void Sidebar_MouseEnter(object sender, MouseEventArgs e) => _sidebarHideTimer.Stop();
    private void Sidebar_MouseLeave(object sender, MouseEventArgs e) => ScheduleSidebarHide();
    private void Sidebar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ScheduleSidebarHide();
    private void ConnectionsMenu_Closed(object sender, RoutedEventArgs e) => ScheduleSidebarHide();
    private void ScheduleSidebarHide() { if (SidebarPopup.IsOpen) { _sidebarHideTimer.Stop(); _sidebarHideTimer.Start(); } }
    private void HideSidebarAfterDelay(object? sender, EventArgs e)
    {
        _sidebarHideTimer.Stop();
        if (!Sidebar.IsMouseOver && !SidebarRail.IsMouseOver && !ConnectionsList.ContextMenu.IsOpen &&
            !(Sidebar.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox) && OwnedWindows.Count == 0)
            SidebarPopup.IsOpen = false;
    }
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SidebarPopup.IsOpen && !Sidebar.IsMouseOver && !SidebarRail.IsMouseOver && !ConnectionsList.ContextMenu.IsOpen)
            SidebarPopup.IsOpen = false;
    }
    private void Window_Deactivated(object? sender, EventArgs e) => SidebarPopup.IsOpen = false;
    private void SidebarPopup_Closed(object? sender, EventArgs e) => _sidebarHideTimer.Stop();
    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e) { if (SidebarPopup?.IsOpen == true) Sidebar.Height = Math.Max(200, Workspace.ActualHeight); }

    private void Focus_Click(object sender, RoutedEventArgs e) => ToggleFocus();
    private void ToggleFocus()
    {
        _focusMode = !_focusMode;
        Toolbar.Visibility = Tabs.Visibility = StatusBar.Visibility = _focusMode ? Visibility.Collapsed : Visibility.Visible;
        FocusStrip.Visibility = _focusMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidebarLayout();
        RevealFocusSession();
        ActiveSession?.Activate();
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutDialog { Owner = this }.ShowDialog();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (!_fullScreen)
        {
            _previousState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _previousState;
        }
        _fullScreen = !_fullScreen;
        ActiveSession?.RefreshDisplay();
    }

    private void FocusSession_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = ((Button)sender).Tag;
        ActiveSession?.Activate();
    }

    private void RevealFocusSession()
    {
        if (!_focusMode) return;
        Dispatcher.BeginInvoke(new Action(() => FocusSessionButtons.Children.OfType<Button>()
            .FirstOrDefault(b => ReferenceEquals(b.Tag, Tabs.SelectedItem))?.BringIntoView()), DispatcherPriority.Loaded);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { ToggleFocus(); e.Handled = true; }
    }

    private void ShowError(Exception ex, string title) => MessageDialog.ShowMessage(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveOpenSessions();
        _closing = true;
        SidebarPopup.IsOpen = false;
        _sidebarHideTimer.Stop();
        foreach (var session in SessionSurface.Children.OfType<SessionView>().ToArray()) CloseSession(session);
    }
}
