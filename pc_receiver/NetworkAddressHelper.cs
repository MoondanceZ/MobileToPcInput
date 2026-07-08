using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace pc_receiver;

public static class NetworkAddressHelper
{
    public static string GetPreferredLocalIp()
    {
        var addresses = GetLocalIpCandidates().ToArray();

        return addresses.FirstOrDefault(address => address.InterfaceType == NetworkInterfaceType.Wireless80211)
                   ?.Address
               ?? addresses.FirstOrDefault(address => address.InterfaceType == NetworkInterfaceType.Ethernet)
                   ?.Address
               ?? addresses.FirstOrDefault()?.Address
               ?? "127.0.0.1";
    }

    public static string GetDisplayText()
    {
        var candidates = GetLocalIpCandidates().ToArray();
        if (candidates.Length == 0)
        {
            return "电脑 IP: 127.0.0.1";
        }

        var preferred = GetPreferredLocalIp();
        var all = string.Join(" / ", candidates.Select(item => $"{item.Address} ({item.Name})"));
        return $"电脑 IP: {preferred}\n可用地址: {all}";
    }

    private static IEnumerable<LocalIpCandidate> GetLocalIpCandidates()
    {
        var blockedNames = new[] { "vmware", "vethernet", "virtual", "loopback", "mihomo", "wsl" };

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .Where(item => item.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
                or NetworkInterfaceType.Ethernet)
            .Where(item =>
            {
                var name = item.Name.ToLowerInvariant();
                var description = item.Description.ToLowerInvariant();
                return !blockedNames.Any(blocked =>
                    name.Contains(blocked) || description.Contains(blocked));
            })
            .SelectMany(item => item.GetIPProperties()
                .UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => new LocalIpCandidate(
                    address.Address.ToString(),
                    item.Name,
                    item.NetworkInterfaceType)))
            .Where(item => !item.Address.StartsWith("127."))
            .ToArray();
    }

    private sealed record LocalIpCandidate(
        string Address,
        string Name,
        NetworkInterfaceType InterfaceType);
}
