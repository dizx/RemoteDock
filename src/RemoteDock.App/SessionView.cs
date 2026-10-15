using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using AxMSTSCLib;
using RemoteDock.Core;
using Forms = System.Windows.Forms;
using Size = System.Drawing.Size;

namespace RemoteDock.App;

public sealed class SessionView : UserControl, IDisposable
{
    private readonly WindowsFormsHost _host = new();
    private readonly Forms.Panel _panel = new() { BackColor = Color.Black };
    private readonly Font _statusFont = new("Consolas", 24, System.Drawing.FontStyle.Bold);
    private readonly Forms.Label _statusScreen = new()
    {
        BackColor = Color.Black, ForeColor = Color.FromArgb(80, 255, 128),
        // Dock.Fill lets Label.AdjustSize briefly restore its default 100x23 size
        // on Text changes. Four-sided anchoring keeps the opaque cover in place.
        Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
        TextAlign = ContentAlignment.MiddleCenter,
        Padding = new Forms.Padding(32), UseMnemonic = false, AutoEllipsis = false,
        AccessibleName = "Session status", Cursor = Forms.Cursors.Default
    };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private AxMsRdpClient9NotSafeForScripting? _client;
    private PixelSize _desktop;
    private PixelSize? _lastRequestedSize;
    private int _lastRequestedDpiScale;
    private SecureString _password = new();
    private bool _reconnectQueued;
    private bool _displayRefreshPending;
    private bool _initialized;
    private bool _loggedIn;
    private bool _disposed;
    private readonly bool _connectOnLoad;
    private bool _deferConnect;
    private bool _remoteDisplayReady;
    private string? _operationStatus;
    private bool _preserveOperationResult;
    private bool _showingStatus = true;

    public ConnectionProfile Profile { get; private set; }
    internal bool ConnectsToHost => _connectOnLoad && !_deferConnect;
    internal bool IsAwaitingConnection => _deferConnect;
    internal bool IsDisconnected => (_initialized || _deferConnect) && !_disposed && !_reconnectQueued && (_client is null || _client.Connected == 0);
    public string Status { get; private set; } = "Preparing session";
    public event EventHandler? StatusChanged;
    public event EventHandler? ConnectionEstablished;
    public event EventHandler? RemoteSessionEnded;
    internal Action<PixelSize, int> UpdateDisplaySettings { get; set; }

    public SessionView(ConnectionProfile profile, string password, bool connectOnLoad = true, bool deferConnect = false)
    {
        Profile = profile;
        UpdateDisplaySettings = (size, dpiScale) => _client!.UpdateSessionDisplaySettings(
            (uint)size.Width, (uint)size.Height, 0, 0, 0,
            (uint)dpiScale, (uint)DisplayPolicy.RemoteDeviceScale(dpiScale));
        RememberPassword(password);
        _connectOnLoad = connectOnLoad;
        _deferConnect = deferConnect;
        _desktop = new(profile.DesktopWidth, profile.DesktopHeight);
        Content = _host;
        _host.Child = _panel;
        _host.Background = System.Windows.Media.Brushes.Black;
        _statusScreen.Font = _statusFont;
        _statusScreen.Bounds = _panel.ClientRectangle;
        _panel.Controls.Add(_statusScreen);
        SetStatus(Status);
        _panel.Resize += PanelResized;
        _panel.DpiChangedAfterParent += PanelResized;
        _resizeTimer.Tick += ResizeRemoteDesktop;
        IsVisibleChanged += SessionVisibilityChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _disposed || _deferConnect) return;
        _initialized = true;
        InitializeClient();
    }

    private void InitializeClient()
    {
        try
        {
            Profile.Validate();
            // Keep the ActiveX window in-place active while connecting. OLE hiding can
            // deactivate the control and its connection UI; cover it with the native label instead.
            var client = new AxMsRdpClient9NotSafeForScripting();
            _client = client;
            ((ISupportInitialize)_client).BeginInit();
            _panel.Controls.Add(_client);
            _statusScreen.BringToFront();
            ((ISupportInitialize)_client).EndInit();
            _client.CreateControl();

            _client.OnConnected += (_, _) => { if (_client == client) ShowRemoteDisplay(); };
            _client.OnAutoReconnecting2 += (_, args) =>
            {
                if (_client == client && !_preserveOperationResult)
                    ShowSessionStatus($"Reconnecting…\nAttempt {args.attemptCount} of {args.maxAttemptCount}");
            };
            _client.OnAutoReconnected += (_, _) => { if (_client == client) ShowRemoteDisplay(); };
            _client.OnLoginComplete += (_, _) =>
            {
                if (_client != client || _disposed) return;
                _loggedIn = true;
                _remoteDisplayReady = true;
                SetStatus(DisplayDescription());
                RefreshDisplay();
                ConnectionEstablished?.Invoke(this, EventArgs.Empty);
            };
            _client.OnDisconnected += (_, args) =>
            {
                if (_client != client || _disposed) return;
                string reason = _client.GetErrorDescription((uint)args.discReason, (uint)_client.ExtendedDisconnectReason);
                HandleDisconnected($"Disconnected: {reason} ({args.discReason})", args.discReason, (int)_client.ExtendedDisconnectReason);
            };
            _client.OnFatalError += (_, args) => { if (_client == client) ShowSessionStatus($"RDP error {args.errorCode}"); };
            _client.OnRemoteDesktopSizeChange += (_, args) =>
            {
                if (_client == client) RemoteDesktopSizeChanged(args.width, args.height);
            };

            // Loaded can run before the native host has its final layout. Never use
            // its temporary/default bounds as the server's initial desktop size.
            _desktop = TryGetStableViewport(out var viewport)
                ? DisplayPolicy.RequestedDesktop(Profile, viewport)
                : new(Profile.DesktopWidth, Profile.DesktopHeight);
            _lastRequestedSize = _desktop;
            _lastRequestedDpiScale = DisplayPolicy.RemoteDesktopScale(Profile, _panel.DeviceDpi);
            _client.Server = Profile.Host;
            _client.UserName = Profile.UsesWindowsHostAuthentication ? "" : Profile.UserName;
            _client.Domain = Profile.UsesWindowsHostAuthentication ? "" : Profile.Domain;
            _client.DesktopWidth = _desktop.Width;
            _client.DesktopHeight = _desktop.Height;
            _client.ColorDepth = 32;
            _client.AdvancedSettings2.RDPPort = Profile.Port;
            _client.AdvancedSettings2.SmartSizing = Profile.DisplayMode == DisplayMode.ScaledFit;
            var extended = (MSTSCLib.IMsRdpExtendedSettings)_client.GetOcx()!;
            if (Profile.DisplayMode == DisplayMode.ScaledFit)
            {
                object enableZoom = true;
                extended.set_Property("EnableZoom", ref enableZoom);
            }
            object desktopScale = (uint)_lastRequestedDpiScale;
            object deviceScale = (uint)DisplayPolicy.RemoteDeviceScale(_lastRequestedDpiScale);
            extended.set_Property("DesktopScaleFactor", ref desktopScale);
            extended.set_Property("DeviceScaleFactor", ref deviceScale);
            _client.AdvancedSettings7.EnableCredSspSupport = true;
            _client.AdvancedSettings7.ConnectToAdministerServer = Profile.AdministrativeSession;
            _client.AdvancedSettings5.AuthenticationLevel = 2;
            _client.AdvancedSettings6.RedirectClipboard = Profile.RedirectClipboard;
            _client.AdvancedSettings2.RedirectDrives = false;
            _client.AdvancedSettings2.RedirectPrinters = false;
            _client.AdvancedSettings3.EnableAutoReconnect = true;
            _client.AdvancedSettings3.MaxReconnectAttempts = 5;
            _client.SecuredSettings2.KeyboardHookMode = Profile.RedirectWindowsKeys ? 1 : 0;
            _client.SecuredSettings2.AudioRedirectionMode = 2;
            if (!Profile.UsesWindowsHostAuthentication && _password.Length > 0)
            {
                var secret = Marshal.SecureStringToBSTR(_password);
                try { _client.AdvancedSettings2.ClearTextPassword = Marshal.PtrToStringBSTR(secret); }
                finally { Marshal.ZeroFreeBSTR(secret); }
            }

            if (Profile.Kind == ConnectionKind.HyperV)
            {
                object disableDelegation = true;
                extended.set_Property("DisableCredentialsDelegation", ref disableDelegation);
                _client.AdvancedSettings7.AuthenticationServiceClass = "Microsoft Virtual Console Service";
                _client.AdvancedSettings8.NegotiateSecurityLayer = false;
                _client.AdvancedSettings7.PCB = Profile.VirtualMachineId!.Value.ToString("D") +
                    (Profile.EnhancedSession ? ";EnhancedMode=1" : "");
            }

            LayoutClient();
            if (_connectOnLoad)
            {
                ShowSessionStatus($"Connecting to {Profile.Host}:{Profile.Port}…");
                _client.Connect();
            }
            else
            {
                ShowSessionStatus("RDP control initialized (offline)");
            }
        }
        catch (Exception ex)
        {
            ShowSessionStatus($"Cannot start session: {ex.Message}");
        }
    }

    private void RememberPassword(string password)
    {
        _password.Dispose();
        _password = new SecureString();
        foreach (char character in password) _password.AppendChar(character);
        _password.MakeReadOnly();
    }

    public void Reconnect(ConnectionProfile settings, string? savedPassword = null)
    {
        if (_disposed || _reconnectQueued || _client?.Connected == 2) return;
        settings.Validate();
        if (savedPassword is not null) RememberPassword(savedPassword);
        else if (settings.UserName != Profile.UserName || settings.Domain != Profile.Domain) RememberPassword("");
        Profile = settings;
        _deferConnect = false;
        _initialized = true;
        _operationStatus = null;
        _preserveOperationResult = false;
        _reconnectQueued = true;
        _loggedIn = false;
        _resizeTimer.Stop();
        ShowSessionStatus("Reconnecting…");
        DisposeClient();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _reconnectQueued = false;
            if (!_disposed) InitializeClient();
        }), DispatcherPriority.Background);
    }

    private void DisposeClient()
    {
        var client = _client;
        _client = null;
        if (client is null) return;
        try { if (client.Connected != 0) client.Disconnect(); }
        catch (COMException) { }
        catch (Forms.AxHost.InvalidActiveXStateException) { }
        finally
        {
            _panel.Controls.Remove(client);
            client.Dispose();
        }
    }

    private PixelSize PanelSize => new(_panel.ClientSize.Width, _panel.ClientSize.Height);

    private string DisplayDescription()
    {
        string mode = Profile.DisplayMode switch
        {
            DisplayMode.ScaledFit => $"Scaled Fit · {Profile.EffectiveScale} resolution",
            DisplayMode.ActualSize => "Actual Size",
            _ => "Match Panel"
        };
        string console = Profile.Kind == ConnectionKind.HyperV && !Profile.EnhancedSession
            ? " · Basic console: guest controls resolution" : "";
        return $"Connected · {_desktop.Width} × {_desktop.Height} · {mode}{console}";
    }

    private void PanelResized(object? sender, EventArgs e)
    {
        if (_disposed) return;
        LayoutClient();
        QueueResize();
    }

    private void LayoutClient()
    {
        if (_client is null || _disposed) return;
        var panel = PanelSize;
        if (panel.Width < 1 || panel.Height < 1) return;

        _panel.AutoScroll = _remoteDisplayReady && _operationStatus is null && Profile.DisplayMode == DisplayMode.ActualSize;
        if (Profile.DisplayMode == DisplayMode.ActualSize)
        {
            _panel.AutoScrollMinSize = _panel.AutoScroll ? new Size(_desktop.Width, _desktop.Height) : Size.Empty;
            _client.Bounds = new Rectangle(_panel.AutoScrollPosition, new Size(_desktop.Width, _desktop.Height));
        }
        else
        {
            var bounds = Profile.DisplayMode == DisplayMode.ScaledFit
                ? DisplayPolicy.Fit(_desktop, panel)
                : new PixelRect(0, 0, panel.Width, panel.Height);
            _client.Bounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
    }

    private void QueueResize()
    {
        _resizeTimer.Stop();
        if (!_loggedIn || !_remoteDisplayReady || _operationStatus is not null || !DisplayPolicy.AllowsRemoteResize(Profile) ||
            _disposed || !IsVisible) return;
        _resizeTimer.Start();
    }

    private void SessionVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _resizeTimer.Stop();
        if (IsVisible) RefreshDisplay();
    }

    private bool TryGetStableViewport(out PixelSize viewport)
    {
        viewport = default;
        if (!IsVisible || !IsMeasureValid || !IsArrangeValid || !_host.IsMeasureValid || !_host.IsArrangeValid ||
            Window.GetWindow(this)?.WindowState == WindowState.Minimized) return false;

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_host);
        // Use the session's WPF allocation, not the host's ActualWidth/Height:
        // WindowsFormsHost can mirror a temporary child size back into its own size.
        var arranged = new PixelSize((int)Math.Round(ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        var native = PanelSize;
        // Collapsing/reactivating a tab can temporarily resize the WinForms host.
        // Only a native viewport matching the arranged WPF area may resize the server.
        if (arranged.Width < 200 || arranged.Height < 200 ||
            Math.Abs(native.Width - arranged.Width) > 1 || Math.Abs(native.Height - arranged.Height) > 1) return false;
        viewport = native;
        return true;
    }

    public void RefreshDisplay()
    {
        if (_disposed || _displayRefreshPending || !_remoteDisplayReady || _operationStatus is not null) return;
        _displayRefreshPending = true;
        // Run after WPF layout and the native resize callback have both finished.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _displayRefreshPending = false;
            if (_disposed || _client is null || !IsVisible || !_remoteDisplayReady || _operationStatus is not null) return;
            try
            {
                LayoutClient();
                if (Profile.DisplayMode == DisplayMode.ScaledFit)
                {
                    _client.AdvancedSettings2.SmartSizing = true;
                    // A remote resolution change can leave the native renderer at its old size
                    // even when the outer control already has the right bounds. Reproduce the
                    // resize that repairs it, without changing the host or remote resolution.
                    var bounds = _client.Bounds;
                    if (bounds.Width > 1)
                    {
                        _client.Width = bounds.Width - 1;
                        _client.Bounds = bounds;
                    }
                }
                _client.Invalidate(invalidateChildren: true);
                QueueResize();
            }
            catch (Exception ex) when (ex is COMException or Forms.AxHost.InvalidActiveXStateException)
            {
                SetStatus($"Display refresh unavailable: {ex.Message}");
            }
        }), DispatcherPriority.Background);
    }

    private void ResizeRemoteDesktop(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        if (!_loggedIn || _client is null || _disposed || !IsVisible || !_remoteDisplayReady || _operationStatus is not null ||
            !DisplayPolicy.AllowsRemoteResize(Profile) || !TryGetStableViewport(out var viewport))
            return;

        var size = DisplayPolicy.RequestedDesktop(Profile, viewport);
        int dpiScale = DisplayPolicy.RemoteDesktopScale(Profile, _panel.DeviceDpi);
        if (size == _lastRequestedSize && dpiScale == _lastRequestedDpiScale) return;
        try
        {
            UpdateDisplaySettings(size, dpiScale);
            _lastRequestedSize = size;
            _lastRequestedDpiScale = dpiScale;
            RefreshDisplay();
        }
        catch (Exception ex)
        {
            SetStatus($"Connected · Dynamic resize unavailable: {ex.Message}");
        }
    }

    public void Activate()
    {
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            LayoutClient();
            RefreshDisplay();
            if (_remoteDisplayReady && _operationStatus is null) _client?.Focus();
        }), DispatcherPriority.Input);
    }

    internal void BeginConnection()
    {
        if (_disposed || !_deferConnect) return;
        _deferConnect = false;
        _operationStatus = null;
        _preserveOperationResult = false;
        ShowSessionStatus("Preparing session…");
        if (IsLoaded) OnLoaded(this, new RoutedEventArgs());
    }

    internal void ShowSessionStatus(string status)
    {
        _remoteDisplayReady = false;
        SetStatus(status);
    }

    internal void ShowRemoteDisplay()
    {
        // Reveal on transport connection, before login, so basic Hyper-V boot/login screens remain usable.
        _remoteDisplayReady = true;
        _preserveOperationResult = false;
        SetStatus(_loggedIn ? DisplayDescription() : "Connected; signing in…");
        RefreshDisplay();
    }

    internal void RemoteDesktopSizeChanged(int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0) return;
        var size = new PixelSize(width, height);
        // The renderer refresh can itself repeat the same notification. Do not feed
        // unchanged dimensions back into another native resize/refresh cycle.
        if (size == _desktop) return;
        _desktop = size;
        LayoutClient();
        RefreshDisplay();
        if (_loggedIn) SetStatus(DisplayDescription());
    }

    internal void ReportOperation(string status)
    {
        if (_disposed) return;
        _operationStatus = status;
        _preserveOperationResult = false;
        SetStatus(status);
    }

    internal void FinishOperation(string? status = null)
    {
        if (_disposed) return;
        _operationStatus = null;
        if (status is not null)
        {
            _preserveOperationResult = true;
            ShowSessionStatus(status);
        }
        else SetStatus(_remoteDisplayReady ? DisplayDescription() : Status);
    }

    internal void HandleDisconnected(string status, int disconnectReason = 0, int extendedReason = 0)
    {
        if (_disposed) return;
        _loggedIn = false;
        _remoteDisplayReady = false;
        _resizeTimer.Stop();
        // 2 is remote disconnection by user; extended 2/12 are API/user logoff.
        // VM management can also end the transport: retain its progress/result screen.
        if ((disconnectReason == 2 || extendedReason is 2 or 12) &&
            _operationStatus is null && !_preserveOperationResult && !_reconnectQueued)
        {
            SetStatus("Remote session ended");
            var client = _client;
            // Never dispose the ActiveX control inside its own disconnect callback.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed && !_reconnectQueued && _client == client &&
                    _operationStatus is null && !_preserveOperationResult)
                    RemoteSessionEnded?.Invoke(this, EventArgs.Empty);
            }), DispatcherPriority.Background);
            return;
        }
        // Saving the VM can disconnect its console before or after the management job completes.
        SetStatus(_preserveOperationResult ? Status : status);
    }

    private void SetStatus(string status)
    {
        if (_disposed) return;
        Status = _operationStatus ?? status;
        string text = $"{Profile.Name}\n\n{Status}";
        if (_statusScreen.Text != text) _statusScreen.Text = text;
        bool showStatus = !_remoteDisplayReady || _operationStatus is not null;
        if (_showingStatus != showStatus)
        {
            _showingStatus = showStatus;
            _statusScreen.Visible = showStatus;
            if (showStatus)
            {
                _panel.AutoScroll = false;
                _panel.AutoScrollMinSize = Size.Empty;
                _panel.AutoScrollPosition = System.Drawing.Point.Empty;
                _statusScreen.BringToFront();
            }
            LayoutClient();
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _password.Dispose();
        _resizeTimer.Stop();
        _resizeTimer.Tick -= ResizeRemoteDesktop;
        IsVisibleChanged -= SessionVisibilityChanged;
        Loaded -= OnLoaded;
        _panel.Resize -= PanelResized;
        _panel.DpiChangedAfterParent -= PanelResized;
        try
        {
            DisposeClient();
        }
        catch (System.Runtime.InteropServices.COMException) { }
        finally
        {
            _host.Dispose();
            _statusFont.Dispose();
            _client = null;
        }
    }
}
