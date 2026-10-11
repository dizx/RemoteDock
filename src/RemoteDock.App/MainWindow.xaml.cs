using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteDock.Core;

namespace RemoteDock.App;

public partial class MainWindow : Window
{
    private readonly ConnectionStore _store = new(Path.Combine(App.DataDirectory, "connections.json"));
    private readonly List<ConnectionProfile> _connections = [];
    private bool _storeReadable = true;
    private bool _focusMode;
    private bool _fullScreen;
    private WindowState _previousState;
    private SessionView? ActiveSession => (Tabs.SelectedItem as ListBoxItem)?.Tag as SessionView;

    public MainWindow()
    {
        InitializeComponent();
        try { _connections.AddRange(_store.Load()); }
        catch (Exception ex)
        {
            _storeReadable = false;
            StatusText.Text = $"Connections could not be loaded: {ex.Message}";
            Loaded += (_, _) => MessageBox.Show(this,
                $"The connection file could not be read and has been preserved.\n\n{ex.Message}\n\n{App.DataDirectory}",
                "Connection file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshConnections();
    }

    private void RefreshConnections(Guid? selectedId = null)
    {
        if (ConnectionsList is null) return;
        selectedId ??= (ConnectionsList.SelectedItem as ConnectionProfile)?.Id;
        string search = SearchBox.Text.Trim();
        var shown = _connections.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                           p.Host.Contains(search, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(p => p.Name).ToArray();
        ConnectionsList.ItemsSource = shown;
        ConnectionsList.SelectedItem = shown.FirstOrDefault(p => p.Id == selectedId);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshConnections();
    private void New_Click(object sender, RoutedEventArgs e) => EditConnection(null);
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is ConnectionProfile profile) EditConnection(profile);
    }

    private void EditConnection(ConnectionProfile? existing)
    {
        var editor = new ConnectionEditor(existing) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } profile) return;
        var updated = _connections.Where(p => p.Id != profile.Id).Append(profile).ToArray();
        if (!SaveConnections(updated)) return;
        RefreshConnections(profile.Id);
        StatusText.Text = "Connection saved. New settings apply on the next connection.";
    }

    private bool SaveConnections(ConnectionProfile[] profiles)
    {
        try
        {
            if (!_storeReadable) throw new IOException("Repair the existing connection file before saving changes.");
            _store.Save(profiles);
            _connections.Clear();
            _connections.AddRange(profiles);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot save connections", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is not ConnectionProfile profile) return;
        if (MessageBox.Show(this, $"Delete saved connection '{profile.Name}'?", "Delete connection",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (SaveConnections(_connections.Where(p => p.Id != profile.Id).ToArray())) RefreshConnections();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is ConnectionProfile profile) Connect(profile);
    }

    private void Connection_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ConnectionsList.SelectedItem is ConnectionProfile profile) Connect(profile);
    }

    private void Connection_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ConnectionsList.SelectedItem is not ConnectionProfile profile) return;
        e.Handled = true;
        Connect(profile);
    }

    private void Connect(ConnectionProfile profile, SessionView? replace = null)
    {
        var credentials = new CredentialsDialog(profile) { Owner = this };
        if (credentials.ShowDialog() != true) return;
        if (replace is not null) CloseSession(replace);
        AddSession(profile with { UserName = credentials.UserName, Domain = credentials.Domain }, credentials.TakePassword());
    }

    internal SessionView AddSession(ConnectionProfile profile, string password, bool connect = true)
    {
        var session = new SessionView(profile, password, connect);
        session.StatusChanged += Session_StatusChanged;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = profile.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) });
        var close = new Button { Content = "×", Padding = new Thickness(6, 1, 6, 1), ToolTip = "Close and disconnect session" };
        close.Click += (_, e) => { e.Handled = true; CloseSession(session); };
        header.Children.Add(close);
        var tab = new ListBoxItem { Content = header, Tag = session, ToolTip = profile.Host };
        SessionSurface.Children.Add(session);
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        Welcome.Visibility = Visibility.Collapsed;
        return session;
    }

    private void Tab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SessionSurface is null) return;
        foreach (var session in SessionSurface.Children.OfType<SessionView>())
            session.Visibility = session == ActiveSession ? Visibility.Visible : Visibility.Collapsed;
        ActiveSession?.Activate();
        UpdateStatus();
    }

    private void Session_StatusChanged(object? sender, EventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        StatusText.Text = ActiveSession?.Status ?? "Ready";
        StatusText.ToolTip = StatusText.Text;
        FocusTitle.Text = ActiveSession is { } session ? $"{session.Profile.Name} · {session.Status}" : "RemoteDock";
        Title = ActiveSession is { } active ? $"{active.Profile.Name} — RemoteDock" : "RemoteDock";
    }

    private void CloseSession(SessionView session)
    {
        var tab = Tabs.Items.Cast<ListBoxItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, session));
        session.StatusChanged -= Session_StatusChanged;
        session.Dispose();
        SessionSurface.Children.Remove(session);
        if (tab is not null) Tabs.Items.Remove(tab);
        if (Tabs.SelectedItem is null && Tabs.Items.Count > 0) Tabs.SelectedIndex = Tabs.Items.Count - 1;
        Welcome.Visibility = Tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveSession is not { } session) return;
        var profile = _connections.FirstOrDefault(p => p.Id == session.Profile.Id) ?? session.Profile;
        Connect(profile, session);
    }

    private void Focus_Click(object sender, RoutedEventArgs e) => ToggleFocus();
    private void ToggleFocus()
    {
        _focusMode = !_focusMode;
        Toolbar.Visibility = Sidebar.Visibility = Tabs.Visibility = StatusBar.Visibility =
            _focusMode ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(_focusMode ? 0 : 260);
        FocusStrip.Visibility = _focusMode ? Visibility.Visible : Visibility.Collapsed;
        ActiveSession?.Activate();
    }

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
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => SelectRelative(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => SelectRelative(1);
    private void SelectRelative(int offset)
    {
        if (Tabs.Items.Count > 0) Tabs.SelectedIndex = (Tabs.SelectedIndex + offset + Tabs.Items.Count) % Tabs.Items.Count;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        { ToggleFocus(); e.Handled = true; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        foreach (var session in SessionSurface.Children.OfType<SessionView>().ToArray()) CloseSession(session);
    }
}
