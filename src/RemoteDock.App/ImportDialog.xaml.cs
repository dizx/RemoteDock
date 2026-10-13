using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using RemoteDock.Core;

namespace RemoteDock.App;

public partial class ImportDialog : Window
{
    private string[] _paths = [];
    public ConnectionImport? Result { get; private set; }

    public ImportDialog()
    {
        InitializeComponent();
        string defaultFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG", "confCons.xml");
        if (File.Exists(defaultFile)) SetPaths([defaultFile]);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Import remote connections", Multiselect = true,
            Filter = "Supported connections (*.json;*.xml;*.rdp)|*.json;*.xml;*.rdp|RemoteDock connections (*.json)|*.json|mRemoteNG XML (*.xml)|*.xml|Remote Desktop files (*.rdp)|*.rdp"
        };
        if (picker.ShowDialog(this) == true) SetPaths(picker.FileNames);
    }

    private void SetPaths(string[] paths)
    {
        _paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        PathsBox.Text = string.Join(Environment.NewLine, _paths);
        ErrorText.Text = "";
        try
        {
            var preview = ReadFiles(_paths, false, null, true);
            SummaryText.Text = $"{preview.Connections.Length} supported connections · {preview.Groups.Length} groups · {preview.SkippedCount} items skipped";
        }
        catch (Exception ex) { SummaryText.Text = ""; ErrorText.Text = ex.Message; }
    }

    internal static ConnectionImport ReadFiles(string[] paths, bool passwords, string? masterPassword, bool scaledFit)
    {
        var catalog = new ConnectionCatalog();
        var secrets = new List<ImportedPassword>();
        var warnings = new List<string>();
        int skipped = 0;
        foreach (string path in paths)
        {
            var batch = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".json" => RemoteDockImporter.Read(path),
                ".rdp" => RdpFileImporter.Read(path, passwords, scaledFit),
                _ => MRemoteNgImporter.Read(path, passwords, masterPassword, scaledFit)
            };
            catalog = batch.MergeInto(catalog);
            secrets.AddRange(batch.Passwords);
            warnings.AddRange(batch.Warnings);
            skipped += batch.SkippedCount;
        }
        return new(catalog.Groups, catalog.Connections, secrets, warnings, skipped);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_paths.Length == 0) { ErrorText.Text = "Choose a connection file first."; return; }
        ImportButton.IsEnabled = false;
        ErrorText.Text = "";
        bool passwords = PasswordsBox.IsChecked == true;
        bool scaledFit = ScaledFitBox.IsChecked == true;
        string master = MasterPassword.Password;
        string[] paths = _paths;
        try
        {
            Result = await Task.Run(() => ReadFiles(paths, passwords, master, scaledFit));
            MasterPassword.Clear();
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { ImportButton.IsEnabled = true; }
    }
}
