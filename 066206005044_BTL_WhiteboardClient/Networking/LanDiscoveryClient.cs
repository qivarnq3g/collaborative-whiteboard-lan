using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Btl.Whiteboard.Contracts;

namespace Btl.Whiteboard.Client.Networking;

public sealed class LanDiscoveryClient
{
    private static readonly TimeSpan RoomCacheTtl = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, CachedRoom> roomCache = new(StringComparer.Ordinal);
    private readonly object roomCacheLock = new();

    public async Task<IReadOnlyList<DiscoveredRoom>> DiscoverAsync(
        int discoveryPort,
        string roomHint,
        CancellationToken cancellationToken)
    {
        using UdpClient udpClient = new();
        udpClient.EnableBroadcast = true;

        DiscoveryQueryPayload query = new(
            DiscoveryProtocol.ApplicationId,
            roomHint,
            DiscoveryProtocol.CurrentProtocolVersion);
        byte[] queryDatagram = DiscoveryProtocol.ToDatagram(query);

        IReadOnlyList<IPEndPoint> discoveryTargets = BuildBroadcastTargets(discoveryPort);
        foreach (IPEndPoint target in discoveryTargets)
        {
            try
            {
                await udpClient.SendAsync(queryDatagram, target, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
        }

        Dictionary<string, DiscoveredRoom> roomsByServerId = new(StringComparer.Ordinal);

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(700));

        while (!timeoutSource.Token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udpClient.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            bool parsed = DiscoveryProtocol.TryParseDatagram<DiscoveryResponsePayload>(
                received.Buffer,
                out DiscoveryResponsePayload? response);
            if (!parsed || response is null)
            {
                continue;
            }

            if (!string.Equals(response.AppId, DiscoveryProtocol.ApplicationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(roomHint) &&
                !string.Equals(roomHint, response.RoomName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            roomsByServerId[response.ServerId] = new DiscoveredRoom
            {
                ServerId = response.ServerId,
                ServerName = response.ServerName,
                RoomName = response.RoomName,
                Address = received.RemoteEndPoint.Address.ToString(),
                TcpPort = response.TcpPort,
                ProtocolVersion = response.ProtocolVersion,
                IsPasswordProtected = response.IsPasswordProtected,
                OwnerUserName = response.OwnerUserName,
                IsFirstUserMode = response.IsFirstUserMode
            };
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<DiscoveredRoom> mergedRooms;
        lock (roomCacheLock)
        {
            foreach ((string serverId, DiscoveredRoom room) in roomsByServerId)
            {
                roomCache[serverId] = new CachedRoom(room, now);
            }

            string[] expiredServerIds = roomCache
                .Where(pair => now - pair.Value.LastSeenUtc > RoomCacheTtl)
                .Select(pair => pair.Key)
                .ToArray();

            foreach (string expiredServerId in expiredServerIds)
            {
                roomCache.Remove(expiredServerId);
            }

            mergedRooms = roomCache.Values
                .Select(entry => entry.Room)
                .Where(room => string.IsNullOrWhiteSpace(roomHint)
                    || string.Equals(roomHint, room.RoomName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return mergedRooms
            .OrderBy(room => room.RoomName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(room => room.ServerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<IPEndPoint> BuildBroadcastTargets(int discoveryPort)
    {
        List<IPEndPoint> targets = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        AddTarget(IPAddress.Broadcast);

        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                !networkInterface.Supports(NetworkInterfaceComponent.IPv4))
            {
                continue;
            }

            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties interfaceProperties;
            try
            {
                interfaceProperties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicastAddress in interfaceProperties.UnicastAddresses)
            {
                if (unicastAddress.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                IPAddress localAddress = unicastAddress.Address;
                if (IPAddress.IsLoopback(localAddress) || IsIpv4LinkLocal(localAddress))
                {
                    continue;
                }

                IPAddress? subnetMask;
                try
                {
                    subnetMask = unicastAddress.IPv4Mask;
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                if (subnetMask is null || IsZeroMask(subnetMask))
                {
                    continue;
                }

                AddTarget(ComputeBroadcastAddress(localAddress, subnetMask));
            }
        }

        return targets;

        void AddTarget(IPAddress address)
        {
            string key = address.ToString();
            if (seen.Add(key))
            {
                targets.Add(new IPEndPoint(address, discoveryPort));
            }
        }
    }

    private static bool IsIpv4LinkLocal(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool IsZeroMask(IPAddress subnetMask)
    {
        byte[] maskBytes = subnetMask.GetAddressBytes();
        return maskBytes.Length != 4 || maskBytes.All(part => part == 0);
    }

    private static IPAddress ComputeBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        byte[] addressBytes = address.GetAddressBytes();
        byte[] maskBytes = subnetMask.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes.Length != 4)
        {
            return IPAddress.Broadcast;
        }

        byte[] broadcastBytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            broadcastBytes[i] = (byte)(addressBytes[i] | (maskBytes[i] ^ 255));
        }

        return new IPAddress(broadcastBytes);
    }

    private sealed record CachedRoom(DiscoveredRoom Room, DateTimeOffset LastSeenUtc);
    public async Task<DiscoveredRoom?> PingServerAsync(string address, int discoveryPort, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out IPAddress? ipAddress))
        {
            return null;
        }

        using UdpClient udpClient = new();

        DiscoveryQueryPayload query = new(
            DiscoveryProtocol.ApplicationId,
            string.Empty,
            DiscoveryProtocol.CurrentProtocolVersion);
        byte[] queryDatagram = DiscoveryProtocol.ToDatagram(query);

        try
        {
            await udpClient.SendAsync(queryDatagram, new IPEndPoint(ipAddress, discoveryPort), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return null;
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(500));

        while (!timeoutSource.Token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udpClient.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            bool parsed = DiscoveryProtocol.TryParseDatagram<DiscoveryResponsePayload>(received.Buffer, out DiscoveryResponsePayload? response);
            if (!parsed || response is null)
            {
                continue;
            }

            if (!string.Equals(response.AppId, DiscoveryProtocol.ApplicationId, StringComparison.Ordinal))
            {
                continue;
            }

            DiscoveredRoom freshRoom = new()
            {
                ServerId = response.ServerId,
                ServerName = response.ServerName,
                RoomName = response.RoomName,
                Address = received.RemoteEndPoint.Address.ToString(),
                TcpPort = response.TcpPort,
                ProtocolVersion = response.ProtocolVersion,
                IsPasswordProtected = response.IsPasswordProtected,
                OwnerUserName = response.OwnerUserName,
                IsFirstUserMode = response.IsFirstUserMode
            };

            lock (roomCacheLock)
            {
                roomCache[response.ServerId] = new CachedRoom(freshRoom, DateTimeOffset.UtcNow);
            }

            return freshRoom;
        }

        return null;
    }
}
