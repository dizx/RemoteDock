using System;
using System.ComponentModel;
using System.Drawing;
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
    private readonly Forms.Panel _panel = new() { BackColor = Color.FromArgb(11, 16, 24) };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private AxMsRdpClient9NotSafeForScripting? _client;
    private PixelSize _desktop;
    private PixelSize? _lastRequestedSize;
    private string? _password;
    private bool _initialized;
    private bool _loggedIn;
    private bool _disposed;
    private readonly bool _connectOnLoad;

    public ConnectionProfile Profile { get; }
    public string Status { get; private set; } = "Preparing session";
    public event EventHandler? StatusChanged;

    public SessionView(ConnectionProfile profile, string password, bool connectOnLoad = true)
    {
        Profile = profile;
        _password = password;
        _connectOnLoad = connectOnLoad;
        _desktop = new(profile.DesktopWidth, profile.DesktopHeight);
        Content = _host;
        _host.Child = _panel;
        _panel.Resize += PanelResized;
        _resizeTimer.Tick += ResizeRemoteDesktop;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        try
        {
            Profile.Validate();
            _client = new AxMsRdpClient9NotSafeForScripting();
            ((ISupportInitialize)_client).BeginInit();
            _panel.Controls.Add(_client);
            ((ISupportInitialize)_client).EndInit();
            _client.CreateControl();

            _client.OnConnected += (_, _) => SetStatus("Connected; signing in…");
            _client.OnLoginComplete += (_, _) =>
            {
                _loggedIn = true;
                SetStatus(DisplayDescription());
                QueueResize();
            };
            _client.OnDisconnected += (_, args) =>
            {
                _loggedIn = false;
                _resizeTimer.Stop();
                if (_disposed) return;
                string reason = _client.GetErrorDescription((uint)args.discReason, (uint)_client.ExtendedDisconnectReason);
                SetStatus($"Disconnected: {reason} ({args.discReason})");
            };
            _client.OnFatalError += (_, args) => SetStatus($"RDP error {args.errorCode}");
            _client.OnRemoteDesktopSizeChange += (_, args) =>
            {
                if (args.width <= 0 || args.height <= 0 || _disposed) return;
                _desktop = new(args.width, args.height);
                LayoutClient();
                if (_loggedIn) SetStatus(DisplayDescription());
            };

            _desktop = DisplayPolicy.RequestedDesktop(Profile, PanelSize);
            _lastRequestedSize = _desktop;
            _client.Server = Profile.Host;
            _client.UserName = Profile.UserName;
            _client.Domain = Profile.Domain;
            _client.DesktopWidth = _desktop.Width;
            _client.DesktopHeight = _desktop.Height;
            _client.ColorDepth = 32;
            _client.AdvancedSettings2.RDPPort = Profile.Port;
            _client.AdvancedSettings2.SmartSizing = Profile.DisplayMode == DisplayMode.ScaledFit;
            if (Profile.DisplayMode == DisplayMode.ScaledFit)
            {
                // SmartSizing alone only shrinks on this control. EnableZoom also permits upscaling.
                object enableZoom = true;
                ((MSTSCLib.IMsRdpExtendedSettings)_client.GetOcx()!).set_Property("EnableZoom", ref enableZoom);
            }
            _client.AdvancedSettings7.EnableCredSspSupport = true;
            _client.AdvancedSettings5.AuthenticationLevel = 2;
            _client.AdvancedSettings6.RedirectClipboard = Profile.RedirectClipboard;
            _client.AdvancedSettings2.RedirectDrives = false;
            _client.AdvancedSettings2.RedirectPrinters = false;
            _client.AdvancedSettings3.EnableAutoReconnect = true;
            _client.AdvancedSettings3.MaxReconnectAttempts = 5;
            _client.SecuredSettings2.KeyboardHookMode = Profile.RedirectWindowsKeys ? 1 : 0;
            _client.SecuredSettings2.AudioRedirectionMode = 2;
            if (!string.IsNullOrEmpty(_password)) _client.AdvancedSettings2.ClearTextPassword = _password;
            _password = null;

            if (Profile.Kind == ConnectionKind.HyperV)
            {
                if (string.IsNullOrWhiteSpace(Profile.UserName))
                {
                    object disableDelegation = true;
                    ((MSTSCLib.IMsRdpExtendedSettings)_client.GetOcx()!).set_Property("DisableCredentialsDelegation", ref disableDelegation);
                }
                _client.AdvancedSettings7.AuthenticationServiceClass = "Microsoft Virtual Console Service";
                _client.AdvancedSettings8.NegotiateSecurityLayer = false;
                _client.AdvancedSettings7.PCB = Profile.VirtualMachineId!.Value.ToString("D") +
                    (Profile.EnhancedSession ? ";EnhancedMode=1" : "");
            }

            LayoutClient();
            if (_connectOnLoad)
            {
                SetStatus($"Connecting to {Profile.Host}:{Profile.Port}…");
                _client.Connect();
            }
            else
            {
                SetStatus("RDP control initialized (offline)");
            }
        }
        catch (Exception ex)
        {
            _password = null;
            SetStatus($"Cannot start session: {ex.Message}");
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

        _panel.AutoScroll = Profile.DisplayMode == DisplayMode.ActualSize;
        if (Profile.DisplayMode == DisplayMode.ActualSize)
        {
            _panel.AutoScrollMinSize = new Size(_desktop.Width, _desktop.Height);
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
        if (!_loggedIn || !DisplayPolicy.AllowsRemoteResize(Profile) || _disposed) return;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void ResizeRemoteDesktop(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        if (!_loggedIn || _client is null || _disposed || !IsVisible ||
            !DisplayPolicy.AllowsRemoteResize(Profile) || PanelSize.Width < 200 || PanelSize.Height < 200)
            return;

        var size = DisplayPolicy.RequestedDesktop(Profile, PanelSize);
        if (size == _lastRequestedSize) return;
        try
        {
            _client.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, 0, 0, 0, 100, 100);
            _lastRequestedSize = size;
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
            QueueResize();
            _client?.Focus();
        }), DispatcherPriority.Input);
    }

    private void SetStatus(string status)
    {
        if (_disposed) return;
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _password = null;
        _resizeTimer.Stop();
        _resizeTimer.Tick -= ResizeRemoteDesktop;
        Loaded -= OnLoaded;
        _panel.Resize -= PanelResized;
        try
        {
            if (_client?.Connected != 0) _client?.Disconnect();
        }
        catch (System.Runtime.InteropServices.COMException) { }
        finally
        {
            _host.Dispose();
            _client = null;
        }
    }
}
