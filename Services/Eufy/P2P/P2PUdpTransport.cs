// Ported from eufy-sdk src/transport/p2p/p2p-session.ts (socket, local-IP probe and lookup-server resolution)
// @ 5f92bea5 (Apache-2.0), modified for RIoT2. The UDP sockets sit behind an interface so tests can fake a station.

using System.Net;
using System.Net.Sockets;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>One received datagram.</summary>
    internal readonly record struct P2PDatagram(byte[] Data, P2PAddress Remote);

    /// <summary>A bound UDP socket a session sends and receives on.</summary>
    internal interface IP2PUdpSocket : IDisposable
    {
        /// <summary>The local port the socket is bound to.</summary>
        int LocalPort { get; }

        /// <summary>Sends one datagram; failures are reported by throwing.</summary>
        void Send(byte[] datagram, P2PAddress remote);

        /// <summary>The next datagram; throws once the socket is disposed.</summary>
        ValueTask<P2PDatagram> ReceiveAsync(CancellationToken cancellationToken);

        /// <summary>Asks the OS for a receive buffer of <paramref name="bytes"/>; answers what was granted, 0 when refused.</summary>
        int RequestReceiveBuffer(int bytes);
    }

    /// <summary>Creates sockets and answers the network facts a session needs.</summary>
    internal interface IP2PUdpTransport
    {
        /// <summary>A socket bound to an ephemeral port, broadcast-capable when asked.</summary>
        IP2PUdpSocket Bind(bool broadcast);

        /// <summary>The host's own LAN address (the one a route to the internet leaves from), or null.</summary>
        Task<string?> DetectLocalIpAsync(CancellationToken cancellationToken);

        /// <summary>The lookup servers with each hostname resolved once to IPv4; a hostname that does not resolve is dropped.</summary>
        Task<IReadOnlyList<P2PAddress>> ResolveAsync(IReadOnlyList<P2PAddress> addresses, CancellationToken cancellationToken);
    }

    /// <summary>The real UDP transport over <see cref="Socket"/>.</summary>
    internal sealed class SocketP2PUdpTransport : IP2PUdpTransport
    {
        public static readonly SocketP2PUdpTransport Instance = new();

        private static readonly object LocalIpGate = new();
        private static Task<string?>? localIp;

        public IP2PUdpSocket Bind(bool broadcast) => new SocketUdp(broadcast);

        /// <summary>Probed once per process and cached; a failed probe is not cached, so the next session asks again.</summary>
        public async Task<string?> DetectLocalIpAsync(CancellationToken cancellationToken)
        {
            Task<string?> probe;
            lock (LocalIpGate) probe = localIp ??= Task.Run(ProbeLocalIp, CancellationToken.None);
            string? result = null;
            try
            {
                result = await probe.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                if (result is null)
                    lock (LocalIpGate) if (localIp == probe) localIp = null;
            }
        }

        private static string? ProbeLocalIp()
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(IPAddress.Parse("8.8.8.8"), 53);
                return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
            }
            catch (SocketException)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<P2PAddress>> ResolveAsync(IReadOnlyList<P2PAddress> addresses, CancellationToken cancellationToken)
        {
            var resolved = await Task.WhenAll(addresses.Select(async addr =>
            {
                if (IPAddress.TryParse(addr.Host, out _)) return (P2PAddress?)addr;
                try
                {
                    var hosts = await Dns.GetHostAddressesAsync(addr.Host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
                    return hosts.Length > 0 ? new P2PAddress(hosts[0].ToString(), addr.Port) : null;
                }
                catch (SocketException)
                {
                    return null;
                }
            })).ConfigureAwait(false);
            return resolved.Where(a => a.HasValue).Select(a => a!.Value).ToList();
        }

        private sealed class SocketUdp : IP2PUdpSocket
        {
            // SIO_UDP_CONNRESET: stop Windows reporting an ICMP port-unreachable as a receive error.
            private const int SioUdpConnReset = -1744830452;

            private readonly Socket socket;
            private readonly byte[] buffer = new byte[65536];

            public SocketUdp(bool broadcast)
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows())
                {
                    try { socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); }
                    catch (SocketException) { }
                }
                if (broadcast)
                {
                    try { socket.EnableBroadcast = true; }
                    catch (SocketException) { }
                }
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            }

            public int LocalPort => (socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;

            public void Send(byte[] datagram, P2PAddress remote) =>
                socket.SendTo(datagram, new IPEndPoint(IPAddress.Parse(remote.Host), remote.Port));

            public async ValueTask<P2PDatagram> ReceiveAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    try
                    {
                        var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken).ConfigureAwait(false);
                        var remote = (IPEndPoint)result.RemoteEndPoint;
                        return new P2PDatagram(buffer.AsSpan(0, result.ReceivedBytes).ToArray(), new P2PAddress(remote.Address.MapToIPv4().ToString(), remote.Port));
                    }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.MessageSize)
                    {
                        // A late ICMP error or an oversized datagram: not the end of the socket.
                    }
                }
            }

            public int RequestReceiveBuffer(int bytes)
            {
                try
                {
                    socket.ReceiveBufferSize = bytes;
                    return socket.ReceiveBufferSize;
                }
                catch (SocketException)
                {
                    return 0;
                }
            }

            public void Dispose() => socket.Dispose();
        }
    }
}
