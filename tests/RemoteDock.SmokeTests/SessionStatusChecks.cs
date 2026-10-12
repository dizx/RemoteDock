using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms.Integration;
using RemoteDock.App;
using RemoteDock.Core;
using Forms = System.Windows.Forms;

namespace RemoteDock.SmokeTests;

internal static class SessionStatusChecks
{
    internal static async Task Run(SessionView session)
    {
        var panel = ((WindowsFormsHost)session.Content).Child;
        var client = panel.Controls.OfType<Forms.AxHost>().Single();
        var screen = panel.Controls.OfType<Forms.Label>().Single();
        string originalStatus = session.Status;
        Require(client.IsHandleCreated, "The status screen prevented the native RDP window from being created before connecting.");
        Require(screen.Visible && client.Visible && panel.Controls.GetChildIndex(screen) < panel.Controls.GetChildIndex(client) && panel.BackColor == Color.Black,
            "An idle session exposed the white ActiveX surface.");
        Require(screen.ForeColor.G > 200 && screen.Font.Size >= 24 && screen.Text.Contains(originalStatus),
            "The session status was not shown in large green text.");

        session.ShowRemoteDisplay();
        Require(client.Visible && !screen.Visible, "Connecting did not reveal the remote login/boot screen.");
        await Task.Delay(50);
        int width = (int)((dynamic)client).DesktopWidth;
        int height = (int)((dynamic)client).DesktopHeight;
        int refreshResizes = 0;
        EventHandler repeatRemoteSize = (_, _) =>
        {
            refreshResizes++;
            session.RemoteDesktopSizeChanged(width, height);
        };
        client.SizeChanged += repeatRemoteSize;
        try
        {
            session.RefreshDisplay();
            await Task.Delay(100);
            Require(refreshResizes is >= 2 and <= 4, "Native refresh entered a repeated resize feedback loop.");
            int before = refreshResizes;
            for (int i = 0; i < 10; i++) session.RemoteDesktopSizeChanged(width, height);
            await Task.Delay(50);
            Require(refreshResizes == before, "Unchanged remote dimensions refreshed the native renderer again.");
        }
        finally { client.SizeChanged -= repeatRemoteSize; }
        session.ReportOperation("Saving state… 42%");
        Require(screen.Visible && client.Visible && screen.Text.Contains("42%"), "VM progress did not cover the active native viewport.");
        await CheckProgressCoverage(session, screen, panel);
        session.HandleDisconnected("Disconnected during save");
        Require(screen.Text.Contains("42%"), "A transport disconnect overwrote in-progress saving.");
        await CheckProgressCoverage(session, screen, panel);
        session.FinishOperation("Saved · 100%\nResume to continue working.");
        Require(screen.Text.Contains("100%") && screen.Visible, "Save completion disappeared from the session area.");
        session.HandleDisconnected("Late disconnect after saving");
        Require(screen.Text.Contains("Saved · 100%"), "A delayed console disconnect overwrote the saved VM state.");
        const string error = "Cannot connect: the remote host is unavailable.\r\n\r\nTry connecting again.";
        session.ShowSessionStatus(error);
        Require(screen.Visible && screen.Text.Contains("Cannot connect"), "Connection error did not cover the white native surface.");
        var statusBar = (System.Windows.Controls.TextBlock)System.Windows.Window.GetWindow(session)!.FindName("StatusText");
        Require(statusBar.Text == "Cannot connect: the remote host is unavailable." && (string)statusBar.ToolTip == error,
            "The bottom status bar did not keep a single-line summary with full details in its tooltip.");

        await Task.Delay(50);
        using (var image = new Bitmap(screen.Width, screen.Height))
        {
            screen.DrawToBitmap(image, screen.ClientRectangle);
            Require(image.GetPixel(0, 0).ToArgb() == Color.Black.ToArgb(), "Status screen did not render a black background.");
            int green = 0;
            for (int y = 0; y < image.Height; y += 2)
                for (int x = 0; x < image.Width; x += 2)
                {
                    var pixel = image.GetPixel(x, y);
                    if (pixel.G > 150 && pixel.G > pixel.R * 1.5 && pixel.G > pixel.B * 1.5) green++;
                }
            Require(green > 100, "The native status screen did not render visible green text.");
            Directory.CreateDirectory("artifacts/smoke");
            image.Save("artifacts/smoke/session-status.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        session.ShowSessionStatus(originalStatus);
        Console.WriteLine("PASS: black native session surface; green status/errors; single-line status bar; remote login visibility; bounded native refresh; progress updates never shrink the status cover before/after transport disconnect; rendered status screenshot.");
    }

    private static async Task CheckProgressCoverage(SessionView session, Forms.Control screen, Forms.Control panel)
    {
        Require(screen.Bounds == panel.ClientRectangle, "The progress screen did not fill the native viewport.");
        string? uncovered = null;
        int changes = 0;
        EventHandler checkBounds = (_, _) =>
        {
            changes++;
            if (screen.Bounds != panel.ClientRectangle) uncovered ??= $"{screen.Bounds} instead of {panel.ClientRectangle}";
        };
        screen.SizeChanged += checkBounds;
        try
        {
            for (int percent = 43; percent < 50; percent++)
            {
                session.ReportOperation($"Saving state… {percent}%");
                await Task.Delay(30);
            }
            Require(uncovered is null && changes == 0, $"Progress briefly uncovered the native RDP surface: {uncovered}; {changes} status-screen resizes.");
        }
        finally
        {
            screen.SizeChanged -= checkBounds;
            session.ReportOperation("Saving state… 42%");
        }
    }

    internal static async Task CheckProgress()
    {
        var profile = new ConnectionProfile { Kind = ConnectionKind.HyperV, Host = "offline.invalid", VirtualMachineId = Guid.NewGuid() };
        var pending = new TaskCompletionSource();
        IProgress<int>? provider = null;
        string status = "";
        var actions = new HyperVSessionActions
        {
            ChangeAsync = (_, _, progress) => { provider = progress; return pending.Task; }
        };
        var operation = actions.ChangeWithProgressAsync(profile, HyperVAction.Save, "Saving state", value => status = value);
        Require(status == "Saving state… 0%" && !operation.IsCompleted, "Saving did not display initial progress.");
        provider!.Report(42);
        await Task.Delay(30);
        Require(status == "Saving state… 42%", "The provider percentage was not displayed.");
        provider.Report(100);
        await Task.Delay(30);
        Require(status == "Saving state… 99%" && !operation.IsCompleted, "Progress claimed success before state verification.");
        pending.SetResult();
        await operation;
        Require(status == "Saving state… 100%", "Verified completion did not show 100%.");
        provider.Report(12);
        await Task.Delay(30);
        Require(status == "Saving state… 100%", "A late progress notification overwrote completion.");

        pending = new();
        operation = actions.ChangeWithProgressAsync(profile, HyperVAction.Start, "Resuming", value => status = value);
        provider!.Report(67);
        await Task.Delay(30);
        Require(status == "Resuming… 67%", "Resume did not show its provider percentage.");
        pending.SetException(new InvalidOperationException("Simulated failure"));
        try { await operation; throw new Exception("Failed resume was reported as successful."); }
        catch (InvalidOperationException) { }
        provider.Report(100);
        await Task.Delay(30);
        Require(status == "Resuming… 67%", "A failed operation reported 100% or applied stale progress.");
        Console.WriteLine("PASS: provider save/resume percentages; completion waits for state verification; late progress ignored after success/failure.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
