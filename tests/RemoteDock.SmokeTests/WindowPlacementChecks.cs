using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RemoteDock.App;
using RemoteDock.Core;

namespace RemoteDock.SmokeTests;

internal static class WindowPlacementChecks
{
    internal static async Task Run()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "opencode", "RemoteDock.Placement", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(sandbox, "connections.json");
        var store = new ConnectionStore(path);
        store.SaveCatalog(new());
        MainWindow? window = null;
        try
        {
            double width = Math.Clamp(SystemParameters.WorkArea.Width - 80, 780, 1100);
            double height = Math.Clamp(SystemParameters.WorkArea.Height - 80, 480, 720);
            window = new MainWindow(store) { Width = width, Height = height,
                Left = SystemParameters.WorkArea.Left + 30, Top = SystemParameters.WorkArea.Top + 30 };
            window.Show();
            await Task.Delay(100);
            window.Close();
            var saved = new ConnectionStore(path).LoadCatalog().WindowPlacement;
            Require(saved is { IsValid: true, Maximized: false } && Near(saved.Width, width) && Near(saved.Height, height),
                "Normal window size was not saved.");

            window = new MainWindow(new ConnectionStore(path));
            Require(Near(window.Width, width) && Near(window.Height, height) && Near(window.Left, saved!.Left) && Near(window.Top, saved.Top),
                "Window size and position were not restored.");
            window.Show();
            window.WindowState = WindowState.Maximized;
            await Task.Delay(100);
            window.WindowState = WindowState.Minimized;
            await Task.Delay(50);
            window.Close();
            saved = new ConnectionStore(path).LoadCatalog().WindowPlacement;
            Require(saved is { Maximized: true } && Near(saved.Width, width) && Near(saved.Height, height),
                "Minimizing a maximized window lost its normal bounds or maximized state.");

            window = new MainWindow(new ConnectionStore(path));
            window.Show();
            Require(window.WindowState == WindowState.Maximized, "Maximized state was not restored.");
            ((Button)window.FindName("FullscreenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            window.Close();
            saved = new ConnectionStore(path).LoadCatalog().WindowPlacement;
            Require(saved is { Maximized: true } && Near(saved.Width, width) && Near(saved.Height, height),
                "Closing in fullscreen overwrote the previous window placement.");

            new ConnectionStore(path).SaveCatalog(new() { WindowPlacement = new(100000, 100000, width, height) });
            window = new MainWindow(new ConnectionStore(path));
            Require(window.WindowStartupLocation == WindowStartupLocation.CenterScreen, "An off-screen saved window was not recovered.");
        }
        finally
        {
            window?.Close();
            Directory.Delete(sandbox, recursive: true);
        }
        Console.WriteLine("PASS: window size/position persistence; maximized/minimized restore; fullscreen preserves normal bounds; off-screen recovery.");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
