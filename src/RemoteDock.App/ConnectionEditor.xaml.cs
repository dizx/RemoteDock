using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using RemoteDock.Core;

namespace RemoteDock.App;

public partial class ConnectionEditor : Window
{
    private readonly ConnectionProfile _original;
    public ConnectionProfile? Result { get; private set; }

    public ConnectionEditor(ConnectionProfile? profile = null)
    {
        InitializeComponent();
        _original = profile ?? new ConnectionProfile();
        KindBox.SelectedIndex = (int)_original.Kind;
        NameBox.Text = _original.Name;
        HostBox.Text = _original.Host;
        PortBox.Text = _original.Port.ToString();
        VmIdBox.Text = _original.VirtualMachineId?.ToString() ?? "";
        EnhancedBox.IsChecked = _original.EnhancedSession;
        UserBox.Text = _original.UserName;
        DomainBox.Text = _original.Domain;
        DisplayBox.SelectedIndex = (int)_original.DisplayMode;
        var scales = new List<ResolutionScale> { new(1, 4), new(1, 3), new(1, 2), new(2, 3), new(3, 4), new(1, 1) };
        if (!scales.Contains(_original.EffectiveScale)) scales.Add(_original.EffectiveScale);
        ScaleBox.ItemsSource = scales;
        ScaleBox.SelectedItem = _original.EffectiveScale;
        WidthBox.Text = _original.DesktopWidth.ToString();
        HeightBox.Text = _original.DesktopHeight.ToString();
        ClipboardBox.IsChecked = _original.RedirectClipboard;
        KeysBox.IsChecked = _original.RedirectWindowsKeys;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VmPanel is null || PortBox is null) return;
        bool hyperV = KindBox.SelectedIndex == 1;
        VmPanel.Visibility = hyperV ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrEmpty(PortBox.Text) || PortBox.Text is "3389" or "2179")
            PortBox.Text = hyperV ? "2179" : "3389";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(PortBox.Text, out int port) ||
                !int.TryParse(WidthBox.Text, out int width) || !int.TryParse(HeightBox.Text, out int height))
                throw new ArgumentException("Port and resolution must be whole numbers.");
            if (ScaleBox.SelectedItem is not ResolutionScale scale)
                throw new ArgumentException("Select a resolution scale.");
            Guid? vmId = null;
            if (KindBox.SelectedIndex == 1)
            {
                if (!Guid.TryParse(VmIdBox.Text, out var parsed))
                    throw new ArgumentException("Enter a valid virtual machine ID (GUID).");
                vmId = parsed;
            }
            var profile = _original with
            {
                Name = NameBox.Text.Trim(), Host = HostBox.Text.Trim(), Port = port,
                Kind = (ConnectionKind)KindBox.SelectedIndex, VirtualMachineId = vmId,
                EnhancedSession = EnhancedBox.IsChecked == true,
                UserName = UserBox.Text.Trim(), Domain = DomainBox.Text.Trim(),
                DisplayMode = (DisplayMode)DisplayBox.SelectedIndex,
                Scale = scale,
                DesktopWidth = width, DesktopHeight = height,
                RedirectClipboard = ClipboardBox.IsChecked == true,
                RedirectWindowsKeys = KeysBox.IsChecked == true
            };
            profile.Validate();
            Result = profile;
            DialogResult = true;
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
}
