using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DragonCards.Networking;

public static class LocalNetworkAddress
{
    public static string PreferredIpv4Address()
    {
        var address = ActiveIpv4UnicastAddresses()
            .Select(unicast => unicast.Address)
            .FirstOrDefault();

        return (address ?? IPAddress.Loopback).ToString();
    }

    public static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        var broadcasts = new Dictionary<string, IPAddress>(StringComparer.Ordinal);
        foreach (var unicast in ActiveIpv4UnicastAddresses())
        {
            var address = unicast.Address.GetAddressBytes();
            var mask = unicast.IPv4Mask?.GetAddressBytes();
            if (address.Length != 4 || mask is null || mask.Length != 4)
            {
                continue;
            }

            var broadcastBytes = new byte[4];
            for (var index = 0; index < broadcastBytes.Length; index++)
            {
                broadcastBytes[index] = (byte)(address[index] | ~mask[index]);
            }

            var broadcast = new IPAddress(broadcastBytes);
            if (!broadcast.Equals(unicast.Address))
            {
                broadcasts.TryAdd(broadcast.ToString(), broadcast);
            }
        }

        return broadcasts.Values.ToArray();
    }

    private static IEnumerable<UnicastIPAddressInformation> ActiveIpv4UnicastAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .OrderBy(network => network.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => 0,
                NetworkInterfaceType.Ethernet => 1,
                _ => 2
            })
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(unicast =>
                unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(unicast.Address) &&
                !unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
}
