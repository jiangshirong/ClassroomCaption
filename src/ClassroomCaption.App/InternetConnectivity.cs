using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ClassroomCaption.App;

public static class InternetConnectivity
{
    public static async Task<bool> IsOnlineAsync(Uri service, CancellationToken token = default)
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) return false;
        var internet = await Task.Run(WindowsInternetAccess, token);
        // Windows' Internet probe can be blocked on hotspots. Verify the actual
        // official service before treating NCSI's negative result as offline.
        return await ResolveSignalAsync(internet, () => CanReachServiceAsync(service, token));
    }
    private static Task<bool> ResolveSignalAsync(bool? windowsInternet, Func<Task<bool>> serviceProbe)
        => windowsInternet == false ? serviceProbe() : Task.FromResult(true);

    private static bool? WindowsInternetAccess()
    {
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) return null;
            manager = Activator.CreateInstance(type);
            return manager is not null ? (bool)((dynamic)manager).IsConnectedToInternet : null;
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
        finally { if (manager is not null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager); }
    }

    private static async Task<bool> CanReachServiceAsync(Uri service, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(service.IdnHost, 443, timeout.Token);
            using var tls = new SslStream(tcp.GetStream());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = service.IdnHost }, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
}
