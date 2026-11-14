using System.Net;
using System.Net.Sockets;

namespace Hearth.Core;

public static class HostNetworkAddress
{
    // Callers supply the actual connection peer, never a client-controlled forwarded header.
    public static bool ShouldConfirmInternet(bool isHttps, IPAddress? directPeer) => isHttps && IsPublicIPv4(directPeer);

    /// <summary>Conservative globally routable IPv4 classification, including mapped IPv4 peers.</summary>
    public static bool IsPublicIPv4(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
            && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 192 && b[1] == 168) && !(b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
            && !(b[0] == 198 && b[1] is 18 or 19) && !(b[0] == 198 && b[1] == 51 && b[2] == 100)
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113) && !(b[0] == 192 && b[1] == 88 && b[2] == 99);
    }
}
