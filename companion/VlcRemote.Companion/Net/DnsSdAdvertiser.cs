using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Interop;
using VlcRemote.Core.Protocol;

namespace VlcRemote.Companion.Net;

/// <summary>
/// Advertises the <c>_vlcremote._tcp</c> service through the built-in Windows DNS-SD responder
/// (<c>DnsServiceRegister</c>, dnsapi.dll). The registration lives as long as the process. Errors are not
/// fatal — the UDP beacon and manual address entry remain.
/// </summary>
public sealed class DnsSdAdvertiser : IDisposable
{
    private readonly ILogger _log;
    // The delegate is kept in a field: native code calls it asynchronously.
    private readonly NativeMethods.DnsServiceRegisterComplete _callback;
    private NativeMethods.DNS_SERVICE_REGISTER_REQUEST _request;
    private IntPtr _instance;
    private bool _registered;
    private readonly TaskCompletionSource<uint> _registerTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DnsSdAdvertiser(ILogger<DnsSdAdvertiser> log)
    {
        _log = log;
        _callback = OnComplete;
    }

    public bool IsRegistered => _registered;

    public async Task<bool> RegisterAsync(string machineName, int port, string installationId, CancellationToken ct)
    {
        try
        {
            var safeName = SanitizeLabel(machineName);
            var serviceName = $"{safeName}.{ProtocolConstants.DnsSdServiceType}.local";
            var hostName = $"{safeName}.local";
            string[] keys = ["v", "name", "id"];
            string[] values = [ProtocolConstants.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), machineName, installationId];

            _instance = NativeMethods.DnsServiceConstructInstance(serviceName, hostName, IntPtr.Zero, IntPtr.Zero,
                (ushort)port, 0, 0, (uint)keys.Length, keys, values);
            if (_instance == IntPtr.Zero)
            {
                _log.LogWarning("DnsServiceConstructInstance returned NULL");
                return false;
            }

            _request = new NativeMethods.DNS_SERVICE_REGISTER_REQUEST
            {
                Version = 1,
                InterfaceIndex = 0,
                pServiceInstance = _instance,
                pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_callback),
                pQueryContext = IntPtr.Zero,
                hCredentials = IntPtr.Zero,
                unicastEnabled = 0,
            };

            var rc = NativeMethods.DnsServiceRegister(ref _request, IntPtr.Zero);
            if (rc != NativeMethods.DNS_REQUEST_PENDING && rc != 0)
            {
                _log.LogWarning("DnsServiceRegister failed: {Status}", rc);
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            uint status;
            try
            {
                status = await _registerTcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("DnsServiceRegister: no completion callback within 5 s");
                return false;
            }
            if (status != 0)
            {
                _log.LogWarning("DnsServiceRegister completed with status {Status}", status);
                return false;
            }
            _registered = true;
            _log.LogInformation("DNS-SD registered {Service} on port {Port}", serviceName, port);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DNS-SD registration failed");
            return false;
        }
    }

    private void OnComplete(uint status, IntPtr ctx, IntPtr instance)
    {
        _registerTcs.TrySetResult(status);
        if (instance != IntPtr.Zero && instance != _instance)
            NativeMethods.DnsServiceFreeInstance(instance);
    }

    private static string SanitizeLabel(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray();
        var label = new string(chars);
        return string.IsNullOrEmpty(label) ? "VlcRemote" : label;
    }

    public void Dispose()
    {
        try
        {
            if (_registered)
            {
                _ = NativeMethods.DnsServiceDeRegister(ref _request, IntPtr.Zero); // best effort at exit
                _registered = false;
            }
            if (_instance != IntPtr.Zero)
            {
                NativeMethods.DnsServiceFreeInstance(_instance);
                _instance = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "DNS-SD deregister failed");
        }
    }
}
