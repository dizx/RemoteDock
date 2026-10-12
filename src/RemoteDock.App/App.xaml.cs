using System;
using System.IO;
using System.Windows;

namespace RemoteDock.App;

public partial class App : Application
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDock");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ElevationRetry? retry = null;
        if (e.Args.Length == 2 && e.Args[0] == ElevationRestart.Argument)
        {
            try
            {
                if (!HyperVHost.IsElevated) throw new InvalidOperationException("Windows did not grant administrator access.");
                retry = ElevationRestart.Decode(e.Args[1]);
                await ElevationRestart.WaitForPreviousProcessAsync(retry);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Cannot resume elevated operation", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }
        MainWindow ??= new MainWindow(retry);
        MainWindow.Show();
    }
}
