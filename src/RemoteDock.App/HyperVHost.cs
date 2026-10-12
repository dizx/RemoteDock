using System;
using System.Management;
using System.Security.Principal;
using System.Threading;
using RemoteDock.Core;

namespace RemoteDock.App;

internal static class HyperVHost
{
    internal static bool IsLocal(string host)
    {
        host = host.Trim();
        if (host == ".") return true;
        host = host.TrimEnd('.');
        return host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1";
    }

    internal static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static void CheckEmptyInventory(ConnectionProfile profile, bool? hasManagementRole = null)
    {
        if (!IsLocal(profile.Host)) return;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        bool allowed = hasManagementRole ?? (principal.IsInRole(WindowsBuiltInRole.Administrator) ||
            principal.IsInRole(new SecurityIdentifier("S-1-5-32-578")));
        if (!allowed) throw new HyperVElevationRequiredException();
    }

    internal static bool ShouldOfferElevation(ConnectionProfile profile, Exception error, bool? elevated = null) =>
        profile.Kind == ConnectionKind.HyperV && IsLocal(profile.Host) && !(elevated ?? IsElevated) &&
        (error is UnauthorizedAccessException || error is ManagementException { ErrorCode: ManagementStatus.AccessDenied } ||
         error.HResult == unchecked((int)0x80070005));

    internal static ManagementScope Connect(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        bool local = IsLocal(profile.Host);
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

internal sealed class HyperVElevationRequiredException : UnauthorizedAccessException
{
    internal HyperVElevationRequiredException() : base("Local Hyper-V management requires administrator access or an active Hyper-V Administrators membership.") { }
}
