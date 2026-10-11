using System;
using System.IO;
using System.Windows;

namespace RemoteDock.App;

public partial class App : Application
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDock");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow ??= new MainWindow();
        MainWindow.Show();
    }
}
