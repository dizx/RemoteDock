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
    private readonly DispatcherTimer _restartTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Forms.Panel _transitionCover = new()
    {
        BackColor = Color.Black, Visible = false, TabStop = false,
        Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right,
        AccessibleName = "Display transition"
    };
    private readonly DispatcherTimer _transitionTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private long _transitionStarted;
    private long _transitionLastResize;
    internal bool IsDisplayTransitionCovered => _transitionTimer.IsEnabled;
    private DateTimeOffset? _restartDeadline;
    private bool _useMultiMonitor;
    private bool _clientUsesMultiMonitor;
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
    private readonly string? _displayTracePath = Environment.GetEnvironmentVariable("REMOTEDOCK_DISPLAY_TRACE");

    public ConnectionProfile Profile { get; private set; }
    internal bool ConnectsToHost => _connectOnLoad && !_deferConnect;
    internal bool IsAwaitingConnection => _deferConnect;
    internal bool IsDisconnected => (_initialized || _deferConnect) && !_disposed && !_reconnectQueued && (_client is null || _client.Connected == 0);
    public string Status { get; private set; } = "Preparing session";
    public event EventHandler? StatusChanged;
    public event EventHandler? ConnectionEstablished;
    public event EventHandler? RemoteSessionEnded;
    internal event EventHandler? LeaveMultiMonitorRequested;
    internal bool SupportsMultiMonitor => Profile.Kind == ConnectionKind.Rdp || Profile.EnhancedSession;
    internal bool UsesMultiMonitor => _useMultiMonitor;
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
        _panel.Controls.Add(_transitionCover);
        _transitionTimer.Tick += CheckDisplayTransition;
        SetStatus(Status);
        _panel.Resize += PanelResized;
        _panel.DpiChangedAfterParent += PanelResized;
        _resizeTimer.Tick += ResizeRemoteDesktop;
        _restartTimer.Tick += RetryAfterRestart;
        IsVisibleChanged += SessionVisibilityChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _disposed || _deferConnect) return;
        _initialized = true;
        // Restoring several tabs invalidates layout while Loaded is running.
        // Wait for the workspace and native hosts to finish arranging before
        // choosing the resolution sent with the initial connection.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed && !_deferConnect && !_reconnectQueued && _client is null) InitializeClient();
        }), DispatcherPriority.ContextIdle);
    }

    private void InitializeClient()
    {
        try
        {
            Profile.Validate();
            _clientUsesMultiMonitor = _useMultiMonitor && SupportsMultiMonitor;
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
                StopRestartReconnect();
                TraceDisplay("Login complete");
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
            _client.OnFatalError += (_, args) =>
            {
                if (_client != client) return;
                StopRestartReconnect();
                ShowSessionStatus($"RDP error {args.errorCode}");
            };
            _client.OnRemoteDesktopSizeChange += (_, args) =>
            {
                if (_client == client) RemoteDesktopSizeChanged(args.width, args.height);
            };
            _client.OnRequestLeaveFullScreen += (_, _) =>
            {
                if (_client == client && _useMultiMonitor)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_disposed && _useMultiMonitor) LeaveMultiMonitorRequested?.Invoke(this, EventArgs.Empty);
                    }));
            };

            // Loaded can run before the native host has its final layout. Never use
            // its temporary/default bounds as the server's initial desktop size.
            _desktop = TryGetInitialViewport(out var viewport)
                ? DisplayPolicy.RequestedDesktop(Profile, viewport)
                : new(Profile.DesktopWidth, Profile.DesktopHeight);
            if (_clientUsesMultiMonitor)
            {
                var bounds = Forms.SystemInformation.VirtualScreen;
                _desktop = new(bounds.Width, bounds.Height);
            }
            _lastRequestedSize = _desktop;
            _lastRequestedDpiScale = DisplayPolicy.RemoteDesktopScale(Profile, _panel.DeviceDpi);
            _client.Server = Profile.Host;
            _client.UserName = Profile.UsesWindowsHostAuthentication ? "" : Profile.UserName;
            _client.Domain = Profile.UsesWindowsHostAuthentication ? "" : Profile.Domain;
            _client.DesktopWidth = _desktop.Width;
            _client.DesktopHeight = _desktop.Height;
            TraceDisplay("Initial resolution configured");
            _client.ColorDepth = 32;
            _client.AdvancedSettings2.RDPPort = Profile.Port;
            _client.AdvancedSettings2.SmartSizing = !_clientUsesMultiMonitor && Profile.DisplayMode == DisplayMode.ScaledFit;
            var extended = (MSTSCLib.IMsRdpExtendedSettings)_client.GetOcx()!;
            if (_clientUsesMultiMonitor)
            {
                // Let RemoteDock own the fullscreen container and keep the native
                // multi-monitor layout intact (no single-desktop resize or zoom).
                var native = (MSTSCLib.IMsRdpClientNonScriptable5)_client.GetOcx()!;
                native.ConnectionBarText = Profile.Name;
                _client.FullScreen = true;
                _client.AdvancedSettings.ContainerHandledFullScreen = 1;
                _client.AdvancedSettings2.DisplayConnectionBar = false;
                native.UseMultimon = true;
            }
            if (!_clientUsesMultiMonitor && (Profile.DisplayMode is DisplayMode.ScaledFit or DisplayMode.MatchPanel))
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
        StopRestartReconnect();
        ReconnectCore(settings, savedPassword);
    }

    internal void SetMultiMonitor(bool enabled, bool reconnect = true)
    {
        enabled &= SupportsMultiMonitor;
        if (_disposed) return;
        _useMultiMonitor = enabled;
        if (enabled) EndDisplayTransition();
        // Deferred restores must still wait for the user's normal connect action.
        if (reconnect && _initialized && !_deferConnect && _operationStatus is null && !_preserveOperationResult &&
            _clientUsesMultiMonitor != enabled) ReconnectCore(Profile);
    }

    private void ReconnectCore(ConnectionProfile settings, string? savedPassword = null)
    {
        if (_disposed || _reconnectQueued) return;
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
        }), DispatcherPriority.ContextIdle);
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

    // Login completion and an accepted resize request do not mean the guest has
    // changed resolution. Fit its reported desktop until the size actually matches.
    private bool FitPendingDesktop => Profile.DisplayMode == DisplayMode.MatchPanel &&
        !MatchesReportedDesktop(PanelSize, _desktop);

    // The guest may round an odd requested width down to an even pixel count.
    // Treat that response as confirmed instead of repeatedly requesting the odd width.
    private static bool MatchesReportedDesktop(PixelSize requested, PixelSize reported) =>
        requested == reported || requested.Height == reported.Height &&
        (requested.Width & 1) != 0 && reported.Width == requested.Width - 1;

    private string DisplayDescription()
    {
        if (_clientUsesMultiMonitor)
        {
            if (_client?.Connected == 1)
            {
                uint count = ((MSTSCLib.IMsRdpClientNonScriptable5)_client.GetOcx()!).RemoteMonitorCount;
                return count > 1 ? $"Connected · {count} monitors" : "Connected · Server provided one monitor";
            }
            return "Connected · Multi-monitor";
        }
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
        if (!_clientUsesMultiMonitor && !_useMultiMonitor) BeginDisplayTransition();
        if (IsDisplayTransitionCovered)
        {
            _transitionLastResize = Environment.TickCount64;
            _transitionCover.Bounds = _panel.ClientRectangle;
            _transitionCover.BringToFront();
        }
        LayoutClient();
        QueueResize();
    }

    internal void BeginDisplayTransition()
    {
        if (_disposed || !_remoteDisplayReady || _operationStatus is not null || !IsVisible) return;
        _transitionStarted = _transitionLastResize = Environment.TickCount64;
        _transitionCover.Bounds = _panel.ClientRectangle;
        if (IsDisplayTransitionCovered) return;
        _transitionCover.Visible = true;
        _transitionCover.BringToFront();
        _transitionCover.Update();
        _transitionTimer.Start();
        TraceDisplay("Display transition covered");
    }

    private void CheckDisplayTransition(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        bool settled = now - _transitionStarted >= 400 && now - _transitionLastResize >= 160 &&
            !_displayRefreshPending && TryGetStableViewport(out var viewport) &&
            (!_loggedIn || !DisplayPolicy.AllowsRemoteResize(Profile) ||
                MatchesReportedDesktop(DisplayPolicy.RequestedDesktop(Profile, viewport), _desktop));
        if (settled || now - _transitionStarted >= 1600) EndDisplayTransition();
    }

    private void EndDisplayTransition()
    {
        _transitionTimer.Stop();
        _transitionCover.Visible = false;
    }

    private void LayoutClient()
    {
        if (_client is null || _disposed) return;
        var panel = PanelSize;
        if (panel.Width < 1 || panel.Height < 1) return;

        if (_clientUsesMultiMonitor)
        {
            _panel.AutoScroll = false;
            _panel.AutoScrollMinSize = Size.Empty;
            _client.Bounds = _panel.ClientRectangle;
            return;
        }

        _panel.AutoScroll = _remoteDisplayReady && _operationStatus is null && Profile.DisplayMode == DisplayMode.ActualSize;
        if (Profile.DisplayMode == DisplayMode.ActualSize)
        {
            _panel.AutoScrollMinSize = _panel.AutoScroll ? new Size(_desktop.Width, _desktop.Height) : Size.Empty;
            _client.Bounds = new Rectangle(_panel.AutoScrollPosition, new Size(_desktop.Width, _desktop.Height));
        }
        else
        {
            var bounds = Profile.DisplayMode == DisplayMode.ScaledFit || FitPendingDesktop
                ? DisplayPolicy.Fit(_desktop, panel)
                : new PixelRect(0, 0, panel.Width, panel.Height);
            _client.Bounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
    }

    private void QueueResize()
    {
        _resizeTimer.Stop();
        if (_clientUsesMultiMonitor || !_loggedIn || !_remoteDisplayReady || _operationStatus is not null || !DisplayPolicy.AllowsRemoteResize(Profile) ||
            _disposed || !IsVisible) return;
        _resizeTimer.Start();
    }

    private void SessionVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _resizeTimer.Stop();
        if (!IsVisible) EndDisplayTransition();
        if (IsVisible) RefreshDisplay();
    }

    private bool TryGetInitialViewport(out PixelSize viewport)
    {
        if (TryGetStableViewport(out viewport)) return true;
        // Background tabs have tiny/collapsed native hosts. They will occupy the
        // same workspace when selected, so use its arranged size for startup only.
        if (Parent is not FrameworkElement surface || !surface.IsMeasureValid || !surface.IsArrangeValid)
            return false;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(surface);
        viewport = new((int)Math.Round(surface.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(surface.ActualHeight * dpi.DpiScaleY));
        return viewport.Width >= 200 && viewport.Height >= 200;
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
                bool smartSizing = !_clientUsesMultiMonitor && (Profile.DisplayMode == DisplayMode.ScaledFit || FitPendingDesktop);
                _client.AdvancedSettings2.SmartSizing = smartSizing;
                if (smartSizing)
                {
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
        if (_clientUsesMultiMonitor || !_loggedIn || _client is null || _disposed || !IsVisible || !_remoteDisplayReady || _operationStatus is not null ||
            !DisplayPolicy.AllowsRemoteResize(Profile) || !TryGetStableViewport(out var viewport))
            return;

        var size = DisplayPolicy.RequestedDesktop(Profile, viewport);
        int dpiScale = DisplayPolicy.RemoteDesktopScale(Profile, _panel.DeviceDpi);
        if (size == _lastRequestedSize && dpiScale == _lastRequestedDpiScale) return;
        try
        {
            TraceDisplay($"Request remote resize {size.Width}x{size.Height}, DPI {dpiScale}");
            UpdateDisplaySettings(size, dpiScale);
            TraceDisplay("Remote resize call accepted");
            _lastRequestedSize = size;
            _lastRequestedDpiScale = dpiScale;
            RefreshDisplay();
        }
        catch (Exception ex)
        {
            TraceDisplay($"Remote resize rejected: 0x{ex.HResult:X8} {ex.Message}");
            SetStatus($"Connected · Remote resolution could not be changed.\n{ex.Message}");
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
        TraceDisplay("Transport connected");
        // FullScreen's getter reports the current state, not the pre-connect
        // request. Apply it again once the transport exists, with our container
        // already covering the physical monitor layout.
        if (_clientUsesMultiMonitor && _client?.Connected == 1 && !_client.FullScreen)
            _client.FullScreen = true;
        // A transport can connect while the guest is still rebooting, then drop
        // again before login. Keep recovery armed until the session is usable.
        if (_loggedIn || Profile.Kind == ConnectionKind.HyperV && !Profile.EnhancedSession)
            StopRestartReconnect();
        // Reveal on transport connection, before login, so basic Hyper-V boot/login screens remain usable.
        _remoteDisplayReady = true;
        _preserveOperationResult = false;
        SetStatus(_loggedIn ? DisplayDescription() : "Connected; signing in…");
        RefreshDisplay();
    }

    internal void RemoteDesktopSizeChanged(int width, int height)
    {
        TraceDisplay($"Server reported resolution {width}x{height}");
        if (_disposed || width <= 0 || height <= 0) return;
        var size = new PixelSize(width, height);
        // The renderer refresh can itself repeat the same notification. Do not feed
        // unchanged dimensions back into another native resize/refresh cycle.
        if (size == _desktop) return;
        _desktop = size;
        if (IsDisplayTransitionCovered) _transitionLastResize = Environment.TickCount64;
        // The server can start at a different resolution than we requested.
        // Do not let that stale request suppress fitting the final viewport.
        if (_lastRequestedSize is not { } requested || !MatchesReportedDesktop(requested, size))
            _lastRequestedSize = null;
        LayoutClient();
        RefreshDisplay();
        if (_loggedIn) SetStatus(DisplayDescription());
    }

    internal void ReportOperation(string status)
    {
        if (_disposed) return;
        StopRestartReconnect();
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
        // Extended reason 26 is a server restart (25 is shutdown). This needs a
        // fresh connection: the ActiveX auto-reconnect cookie cannot survive a reboot.
        if (_operationStatus is null && !_preserveOperationResult && !_reconnectQueued &&
            (extendedReason == 26 || (_restartDeadline is not null &&
                extendedReason is 0 or 1 or 7 && disconnectReason is not (1 or 2))))
        {
            _restartDeadline ??= DateTimeOffset.UtcNow.AddMinutes(5);
            ShowSessionStatus("Reconnecting…");
            _restartTimer.Start();
            return;
        }
        StopRestartReconnect();
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

    private void RetryAfterRestart(object? sender, EventArgs e)
    {
        if (_disposed || _restartDeadline is null) { StopRestartReconnect(); return; }
        // Leave a connected login screen available, even if the user takes longer
        // than the recovery deadline to sign in.
        if (_client?.Connected == 1) return;
        if (DateTimeOffset.UtcNow >= _restartDeadline)
        {
            StopRestartReconnect();
            DisposeClient();
            ShowSessionStatus("Automatic reconnect timed out after 5 minutes.\nUse Reconnect to try again.");
            return;
        }
        if (_reconnectQueued || (_client is not null && _client.Connected != 0)) return;
        ReconnectCore(Profile);
    }

    private void StopRestartReconnect()
    {
        _restartTimer.Stop();
        _restartDeadline = null;
    }

    private void SetStatus(string status)
    {
        if (_disposed) return;
        Status = _operationStatus ?? status;
        string text = $"{Profile.Name}\n\n{Status}";
        if (_statusScreen.Text != text) _statusScreen.Text = text;
        bool showStatus = !_remoteDisplayReady || _operationStatus is not null;
        if (showStatus) EndDisplayTransition();
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

    private void TraceDisplay(string message)
    {
        if (string.IsNullOrWhiteSpace(_displayTracePath)) return;
        try
        {
            System.IO.File.AppendAllText(_displayTracePath,
                $"{DateTimeOffset.Now:O} session={Profile.Id} kind={Profile.Kind} enhanced={Profile.EnhancedSession} mode={Profile.DisplayMode} " +
                $"loggedIn={_loggedIn} visible={IsVisible} viewport={PanelSize} desktop={_desktop} requested={_lastRequestedSize} " +
                $"{message}{Environment.NewLine}");
        }
        catch (System.IO.IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EndDisplayTransition();
        _transitionTimer.Tick -= CheckDisplayTransition;
        StopRestartReconnect();
        _restartTimer.Tick -= RetryAfterRestart;
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
