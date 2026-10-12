using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms.Integration;
using RemoteDock.App;
using RemoteDock.Core;
using Forms = System.Windows.Forms;

namespace RemoteDock.SmokeTests;

internal static class SessionConnectionChecks
{
    internal static async Task Run(MainWindow window)
    {
        // Exercise the real ActiveX Connect/event path against a local disposable endpoint.
        // No remote machine, authentication, or VM state change is involved.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var session = window.AddSession(new ConnectionProfile { Name = "Local connection probe", Host = "127.0.0.1", Port = port }, "");
        try
        {
            Require(await Task.WhenAny(accept, Task.Delay(5000)) == accept, "The native RDP control did not initiate a connection.");
            using var peer = await accept;
            var panel = ((WindowsFormsHost)session.Content).Child;
            var client = panel.Controls.OfType<Forms.AxHost>().Single();
            var screen = panel.Controls.OfType<Forms.Label>().Single();
            Require(client.Visible && client.IsHandleCreated,
                "The connection was started with a hidden/deactivated ActiveX control behind the status screen.");
            Require(screen.Visible && panel.Controls.GetChildIndex(screen) < panel.Controls.GetChildIndex(client),
                "The black status screen did not cover the connecting native control.");

            int visibilityChanges = 0, nativeResizes = 0;
            client.VisibleChanged += (_, _) => visibilityChanges++;
            client.SizeChanged += (_, _) => nativeResizes++;
            for (int i = 0; i < 10; i++) session.ReportOperation($"Waiting for connection… {i}%");
            session.FinishOperation();
            Require(visibilityChanges == 0 && nativeResizes == 0,
                "Text-only progress toggled or resized the connecting ActiveX window.");

            peer.Client.LingerState = new LingerOption(true, 0);
            peer.Close();
            listener.Stop();
            for (int i = 0; i < 100 && !session.Status.StartsWith("Disconnected:"); i++) await Task.Delay(50);
            Require(session.Status.StartsWith("Disconnected:") && screen.Visible,
                $"A failed connection stayed stuck instead of displaying its error: {session.Status}");
            Console.WriteLine("PASS: real loopback RDP connection; native control stays active beneath status screen; progress does not change native visibility/size; connection failure reaches the green error screen.");
        }
        finally { await window.RequestCloseSessionAsync(session); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
