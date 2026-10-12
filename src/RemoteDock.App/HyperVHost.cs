using System;
using System.Management;
using System.Threading;
using RemoteDock.Core;

namespace RemoteDock.App;

internal static class HyperVHost
{
    internal static ManagementScope Connect(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        bool local = profile.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            profile.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || profile.Host is "." or "127.0.0.1" or "::1";
        var options = new ConnectionOptions
        {
            Authentication = AuthenticationLevel.PacketPrivacy,
            Impersonation = ImpersonationLevel.Impersonate,
            Timeout = TimeSpan.FromSeconds(15)
        };
        var scope = new ManagementScope($@"\\{(local ? "." : profile.Host)}\root\virtualization\v2", options);
        cancellationToken.ThrowIfCancellationRequested();
        scope.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        return scope;
    }
}
