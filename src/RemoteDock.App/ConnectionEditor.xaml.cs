using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RemoteDock.Core;

namespace RemoteDock.App;

public partial class ConnectionEditor : Window
{
    private readonly ConnectionProfile _original;
    private readonly ConnectionCatalog _catalog;
    private readonly ConnectionGroup? _group;
    private readonly bool _hasSavedPassword;
    private bool _loading = true;
    private Guid? _previousGroup;
    public ConnectionProfile? Result { get; private set; }
    public ConnectionGroup? GroupResult { get; private set; }
    public bool RememberPassword => RememberPasswordBox.IsChecked == true;

    public ConnectionEditor(ConnectionProfile? profile = null, ConnectionCatalog? catalog = null,
        Guid? defaultGroup = null, bool hasSavedPassword = false)
        : this(profile ?? new ConnectionProfile { GroupId = defaultGroup,
            Inherit = defaultGroup is null ? InheritedSettings.None : InheritedSettings.All },
            catalog ?? new(), (ConnectionGroup?)null, hasSavedPassword) { }

    internal ConnectionEditor(ConnectionGroup group, ConnectionCatalog catalog, bool hasSavedPassword)
        : this(group.Defaults.Apply(catalog.GroupDefaults(group.ParentId)) with { Name = group.Name },
            catalog, group, hasSavedPassword) { }

    private ConnectionEditor(ConnectionProfile profile, ConnectionCatalog catalog, ConnectionGroup? group, bool hasSavedPassword)
    {
        _original = profile;
        _catalog = catalog;
        _group = group;
        _hasSavedPassword = hasSavedPassword;
        InitializeComponent();
        NameBox.Text = profile.Name;
        KindBox.SelectedIndex = (int)profile.Kind;
        HostBox.Text = group is null ? profile.Host : "";
        PortBox.Text = profile.Port.ToString();
        VmIdBox.Text = profile.VirtualMachineId?.ToString() ?? "";
        EnhancedBox.IsChecked = profile.EnhancedSession;
        AdministrativeBox.IsChecked = profile.AdministrativeSession;
        UserBox.Text = profile.UserName;
        DomainBox.Text = profile.Domain;
        var scales = new List<ResolutionScale> { new(1, 4), new(1, 3), new(1, 2), new(2, 3), new(3, 4), new(1, 1) };
        if (!scales.Contains(profile.EffectiveScale)) scales.Add(profile.EffectiveScale);
        ScaleBox.ItemsSource = scales;
        var dpiChoices = new List<DpiChoice> { new(0, "Same as local PC") };
        dpiChoices.AddRange(new[] { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 }
            .Select(percent => new DpiChoice(percent, $"{percent}%")));
        DpiBox.ItemsSource = dpiChoices;
        SetDisplayFields(profile);
        ClipboardBox.IsChecked = profile.RedirectClipboard;
        KeysBox.IsChecked = profile.RedirectWindowsKeys;
        RememberPasswordBox.IsChecked = hasSavedPassword;
        PasswordHint.Text = hasSavedPassword
            ? "Password saved. Leave blank to keep it, or uncheck to remove it."
            : group is null ? "A saved password here overrides the group password. Passwords are kept outside the connection file."
            : "This password can be shared by connections in this group and its subgroups.";

        var choices = new List<GroupChoice> { new(null, group is null ? "No group" : "Top level") };
        choices.AddRange(catalog.Groups.Where(g => group is null || !catalog.IsDescendant(g.Id, group.Id))
            .Select(g => new GroupChoice(g.Id, catalog.GroupPath(g.Id))).OrderBy(g => g.Label));
        GroupBox.ItemsSource = choices;
        _previousGroup = group is null ? profile.GroupId : group.ParentId;
        GroupBox.SelectedItem = choices.FirstOrDefault(g => g.Id == _previousGroup) ?? choices[0];
        var inherit = profile.Inherit;
        if (group is not null)
        {
            Title = "Group settings";
            Heading.Text = "Connection group";
            SaveButton.Content = "Save group";
            GroupLabel.Text = "Parent group";
            TargetFields.Visibility = VmPanel.Visibility = Visibility.Collapsed;
            InheritUserBox.Content = "Use inherited user name";
            InheritDomainBox.Content = "Use inherited domain";
            InheritPasswordBox.Content = "Use parent password if none is saved here";
            InheritPortBox.Content = "Use inherited port (or connection's own port)";
            InheritDisplayBox.Content = "Use inherited display settings";
            InheritClipboardBox.Content = "Use inherited clipboard setting";
            InheritKeysBox.Content = "Use inherited keyboard setting";
            inherit = InheritedSettings.None;
            if (group.Defaults.UserName is null) inherit |= InheritedSettings.UserName;
            if (group.Defaults.Domain is null) inherit |= InheritedSettings.Domain;
            if (group.InheritPassword) inherit |= InheritedSettings.Password;
            if (group.Defaults.Port is null) inherit |= InheritedSettings.Port;
            if (group.Defaults.DisplayMode is null && group.Defaults.Scale is null && group.Defaults.RemoteDpiScalePercent is null && group.Defaults.DesktopWidth is null && group.Defaults.DesktopHeight is null)
                inherit |= InheritedSettings.Display;
            if (group.Defaults.RedirectClipboard is null) inherit |= InheritedSettings.Clipboard;
            if (group.Defaults.RedirectWindowsKeys is null) inherit |= InheritedSettings.WindowsKeys;
        }
        SetInheritance(inherit);
        _loading = false;
        UpdateInheritedFields();
        Loaded += (_, _) => NameBox.Focus();
    }

    private Guid? SelectedGroup => (GroupBox.SelectedItem as GroupChoice)?.Id;
    private InheritedSettings Inheritance =>
        (InheritUserBox.IsChecked == true ? InheritedSettings.UserName : 0) |
        (InheritDomainBox.IsChecked == true ? InheritedSettings.Domain : 0) |
        (InheritPasswordBox.IsChecked == true ? InheritedSettings.Password : 0) |
        (InheritPortBox.IsChecked == true ? InheritedSettings.Port : 0) |
        (InheritDisplayBox.IsChecked == true ? InheritedSettings.Display : 0) |
        (InheritClipboardBox.IsChecked == true ? InheritedSettings.Clipboard : 0) |
        (InheritKeysBox.IsChecked == true ? InheritedSettings.WindowsKeys : 0);

    private void SetInheritance(InheritedSettings inherit)
    {
        InheritUserBox.IsChecked = inherit.HasFlag(InheritedSettings.UserName);
        InheritDomainBox.IsChecked = inherit.HasFlag(InheritedSettings.Domain);
        InheritPasswordBox.IsChecked = inherit.HasFlag(InheritedSettings.Password);
        InheritPortBox.IsChecked = inherit.HasFlag(InheritedSettings.Port);
        InheritDisplayBox.IsChecked = inherit.HasFlag(InheritedSettings.Display);
        InheritClipboardBox.IsChecked = inherit.HasFlag(InheritedSettings.Clipboard);
        InheritKeysBox.IsChecked = inherit.HasFlag(InheritedSettings.WindowsKeys);
    }

    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (_group is null && _previousGroup is null && SelectedGroup is not null) SetInheritance(InheritedSettings.All);
        _previousGroup = SelectedGroup;
        UpdateInheritedFields();
    }

    private void Inheritance_Changed(object sender, RoutedEventArgs e) => UpdateInheritedFields();

    private void UpdateInheritedFields()
    {
        if (_loading) return;
        bool canInherit = _group is not null || SelectedGroup is not null;
        InheritUserBox.IsEnabled = InheritDomainBox.IsEnabled = InheritPasswordBox.IsEnabled = InheritPortBox.IsEnabled =
            InheritDisplayBox.IsEnabled = InheritClipboardBox.IsEnabled = InheritKeysBox.IsEnabled = canInherit;
        if (!canInherit) SetInheritance(InheritedSettings.None);
        var defaults = _group is not null ? _catalog.GroupDefaults(SelectedGroup) :
            _catalog.Resolve(_original with { GroupId = SelectedGroup, Inherit = InheritedSettings.All,
                Port = int.TryParse(PortBox.Text, out int port) ? port : _original.Port });
        var inherit = Inheritance;
        UserBox.IsEnabled = !inherit.HasFlag(InheritedSettings.UserName);
        DomainBox.IsEnabled = !inherit.HasFlag(InheritedSettings.Domain);
        PortBox.IsEnabled = !inherit.HasFlag(InheritedSettings.Port);
        DisplayFields.IsEnabled = !inherit.HasFlag(InheritedSettings.Display);
        ClipboardBox.IsEnabled = !inherit.HasFlag(InheritedSettings.Clipboard);
        KeysBox.IsEnabled = !inherit.HasFlag(InheritedSettings.WindowsKeys);
        if (!UserBox.IsEnabled) UserBox.Text = defaults.UserName;
        if (!DomainBox.IsEnabled) DomainBox.Text = defaults.Domain;
        if (!PortBox.IsEnabled) PortBox.Text = defaults.Port.ToString();
        if (!DisplayFields.IsEnabled) SetDisplayFields(defaults);
        if (!ClipboardBox.IsEnabled) ClipboardBox.IsChecked = defaults.RedirectClipboard;
        if (!KeysBox.IsEnabled) KeysBox.IsChecked = defaults.RedirectWindowsKeys;
    }

    private void SetDisplayFields(ConnectionProfile profile)
    {
        DisplayBox.SelectedIndex = (int)profile.DisplayMode;
        var scales = (List<ResolutionScale>)ScaleBox.ItemsSource;
        if (!scales.Contains(profile.EffectiveScale))
        {
            scales.Add(profile.EffectiveScale);
            ScaleBox.Items.Refresh();
        }
        ScaleBox.SelectedItem = profile.EffectiveScale;
        var dpiChoices = (List<DpiChoice>)DpiBox.ItemsSource;
        if (!dpiChoices.Any(choice => choice.Percent == profile.RemoteDpiScalePercent))
        {
            dpiChoices.Add(new(profile.RemoteDpiScalePercent, $"{profile.RemoteDpiScalePercent}%"));
            DpiBox.Items.Refresh();
        }
        DpiBox.SelectedItem = dpiChoices.First(choice => choice.Percent == profile.RemoteDpiScalePercent);
        WidthBox.Text = profile.DesktopWidth.ToString();
        HeightBox.Text = profile.DesktopHeight.ToString();
    }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VmPanel is null || PortBox is null || _group is not null) return;
        bool hyperV = KindBox.SelectedIndex == 1;
        VmPanel.Visibility = hyperV ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrEmpty(PortBox.Text) || PortBox.Text is "3389" or "2179") PortBox.Text = hyperV ? "2179" : "3389";
    }

    public string TakePassword()
    {
        string value = PasswordInput.Password;
        PasswordInput.Clear();
        return value;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(PortBox.Text, out int port) || !int.TryParse(WidthBox.Text, out int width) || !int.TryParse(HeightBox.Text, out int height))
                throw new ArgumentException("Port and resolution must be whole numbers.");
            if (ScaleBox.SelectedItem is not ResolutionScale scale) throw new ArgumentException("Select a resolution scale.");
            if (DpiBox.SelectedItem is not DpiChoice dpi) throw new ArgumentException("Select a remote DPI scale.");
            if (RememberPassword && !_hasSavedPassword && PasswordInput.Password.Length == 0)
                throw new ArgumentException("Enter a password to save, or uncheck the saved-password option.");
            if (_group is not null)
            {
                var inherit = Inheritance;
                var result = _group with
                {
                    Name = NameBox.Text.Trim(), ParentId = SelectedGroup, InheritPassword = inherit.HasFlag(InheritedSettings.Password),
                    Defaults = new()
                    {
                        UserName = inherit.HasFlag(InheritedSettings.UserName) ? null : UserBox.Text.Trim(),
                        Domain = inherit.HasFlag(InheritedSettings.Domain) ? null : DomainBox.Text.Trim(),
                        Port = inherit.HasFlag(InheritedSettings.Port) ? null : port,
                        DisplayMode = inherit.HasFlag(InheritedSettings.Display) ? null : (DisplayMode)DisplayBox.SelectedIndex,
                        Scale = inherit.HasFlag(InheritedSettings.Display) ? null : scale,
                        RemoteDpiScalePercent = inherit.HasFlag(InheritedSettings.Display) ? null : dpi.Percent,
                        DesktopWidth = inherit.HasFlag(InheritedSettings.Display) ? null : width,
                        DesktopHeight = inherit.HasFlag(InheritedSettings.Display) ? null : height,
                        RedirectClipboard = inherit.HasFlag(InheritedSettings.Clipboard) ? null : ClipboardBox.IsChecked == true,
                        RedirectWindowsKeys = inherit.HasFlag(InheritedSettings.WindowsKeys) ? null : KeysBox.IsChecked == true
                    }
                };
                (_catalog with { Groups = _catalog.Groups.Where(g => g.Id != result.Id).Append(result).ToArray() }).Validate();
                GroupResult = result;
            }
            else
            {
                Guid? vmId = null;
                if (KindBox.SelectedIndex == 1)
                {
                    if (!Guid.TryParse(VmIdBox.Text, out var parsed)) throw new ArgumentException("Enter a valid virtual machine ID (GUID).");
                    vmId = parsed;
                }
                var result = _original with
                {
                    Name = NameBox.Text.Trim(), Host = HostBox.Text.Trim(), Port = port,
                    GroupId = SelectedGroup, Inherit = Inheritance,
                    Kind = (ConnectionKind)KindBox.SelectedIndex, VirtualMachineId = vmId,
                    EnhancedSession = EnhancedBox.IsChecked == true,
                    AdministrativeSession = AdministrativeBox.IsChecked == true,
                    UserName = UserBox.Text.Trim(), Domain = DomainBox.Text.Trim(),
                    DisplayMode = (DisplayMode)DisplayBox.SelectedIndex, Scale = scale,
                    RemoteDpiScalePercent = dpi.Percent,
                    DesktopWidth = width, DesktopHeight = height,
                    RedirectClipboard = ClipboardBox.IsChecked == true, RedirectWindowsKeys = KeysBox.IsChecked == true
                };
                result.Validate();
                _catalog.Resolve(result).Validate();
                Result = result;
            }
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { ErrorText.Text = ex.Message; }
    }

    private sealed record GroupChoice(Guid? Id, string Label);
    private sealed record DpiChoice(int Percent, string Label);
}
