using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Btl.Whiteboard.Contracts;

int tcpPort = ReadIntArg(args, "--tcp", DiscoveryProtocol.DefaultTcpPort);
int discoveryPort = ReadIntArg(args, "--discovery", DiscoveryProtocol.DefaultDiscoveryPort);
string roomName = ReadStringArg(args, "--room", DiscoveryProtocol.DefaultRoomName);
string? roomPassword = TryReadPasswordArg(args, "--password");

WhiteboardServer server = new(
	serverName: Environment.MachineName,
	roomName: roomName,
	roomPasswordHash: roomPassword is not null ? WhiteboardServer.ComputePasswordHash(roomPassword) : null,
	tcpPort: tcpPort,
	discoveryPort: discoveryPort);

using CancellationTokenSource shutdownSource = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
	eventArgs.Cancel = true;
	shutdownSource.Cancel();
};

Console.WriteLine("========================================");
Console.WriteLine("BTL Collaborative Whiteboard Server");
Console.WriteLine($"Room: {roomName}");
Console.WriteLine($"Password: {(roomPassword is not null ? "enabled" : "none")}");
Console.WriteLine($"TCP Port: {tcpPort}");
Console.WriteLine($"Discovery UDP Port: {discoveryPort}");
Console.WriteLine($"Protocol Version: {DiscoveryProtocol.CurrentProtocolVersion}");
PrintLanEndpoints(tcpPort, discoveryPort);
Console.WriteLine("Press Ctrl+C to stop server.");
Console.WriteLine("========================================");

await server.RunAsync(shutdownSource.Token).ConfigureAwait(false);

return;

static string? TryReadPasswordArg(string[] args, string key)
{
	int keyIndex = Array.IndexOf(args, key);
	if (keyIndex < 0 || keyIndex + 1 >= args.Length)
	{
		return null;
	}

	string value = args[keyIndex + 1].Trim();
	return value.Length > 0 ? value : null;
}

static int ReadIntArg(string[] args, string key, int fallback)
{
	int keyIndex = Array.IndexOf(args, key);
	if (keyIndex < 0 || keyIndex + 1 >= args.Length)
	{
		return fallback;
	}

	if (int.TryParse(args[keyIndex + 1], out int value))
	{
		return value;
	}

	return fallback;
}

static string ReadStringArg(string[] args, string key, string fallback)
{
	int keyIndex = Array.IndexOf(args, key);
	if (keyIndex < 0 || keyIndex + 1 >= args.Length)
	{
		return fallback;
	}

	string value = args[keyIndex + 1].Trim();
	return string.IsNullOrWhiteSpace(value) ? fallback : value;
}

static void PrintLanEndpoints(int tcpPort, int discoveryPort)
{
	HashSet<string> addresses = new(StringComparer.Ordinal);

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
			if (unicastAddress.Address.AddressFamily != AddressFamily.InterNetwork ||
				IPAddress.IsLoopback(unicastAddress.Address))
			{
				continue;
			}

			byte[] bytes = unicastAddress.Address.GetAddressBytes();
			if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
			{
				continue;
			}

			addresses.Add(unicastAddress.Address.ToString());
		}
	}

	if (addresses.Count == 0)
	{
		Console.WriteLine("LAN endpoints: no active IPv4 adapter found.");
		return;
	}

	Console.WriteLine("LAN endpoints:");
	foreach (string address in addresses.OrderBy(value => value, StringComparer.Ordinal))
	{
		Console.WriteLine($"  - {address} (TCP {tcpPort}, UDP {discoveryPort})");
	}
}
