namespace VlcRemote.Companion.Setup;

public enum NetworkCategory
{
    Public = 0,
    Private = 1,
    Domain = 2,
}

public sealed record ConnectedNetwork(string Name, NetworkCategory Category);

/// <summary>
/// Connected networks and their category (public / private / domain) through COM <c>INetworkListManager</c>, read-only.
/// Allow rules for private networks do not help while Windows considers the home network public.
/// </summary>
public static class NetworkCategories
{
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private const int NlmEnumNetworkConnected = 1;

    /// <summary>Connected networks; empty if the list cannot be read.</summary>
    public static IReadOnlyList<ConnectedNetwork> Connected()
    {
        var result = new List<ConnectedNetwork>();
        try
        {
            var type = Type.GetTypeFromCLSID(NetworkListManagerClsid, throwOnError: true)!;
            dynamic nlm = Activator.CreateInstance(type)!;
            foreach (var item in ComFirewallRuleSource.Enumerate(nlm.GetNetworks(NlmEnumNetworkConnected)))
            {
                dynamic net = item;
                result.Add(new ConnectedNetwork((string?)net.GetName() ?? "?", (NetworkCategory)(int)net.GetCategory()));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or TypeLoadException)
        {
            // Unknown: the window simply shows no hint.
        }
        return result;
    }
}
