using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Loadout.Bay;

/// <summary>A Loadout Bay that answered on the network.</summary>
public sealed record FoundBay(IPAddress Address, string Version, uint ProtocolVersion, bool HasHardDisk);

/// <summary>An IPv4 address of this PC and the length of its subnet's prefix.</summary>
public sealed record LocalNetwork(IPAddress Address, int PrefixLength);

/// <summary>
/// The addresses a sweep greets for one local address: <paramref name="First"/> to <paramref name="Last"/>
/// in <paramref name="Network"/>/<paramref name="PrefixLength"/>. <paramref name="Cut"/> is true when that
/// is only the /24 around the PC in a larger subnet.
/// </summary>
public sealed record SweepRange(IPAddress Network, int PrefixLength, IPAddress First, IPAddress Last, bool Cut);

/// <summary>How far a sweep is: the addresses greeted so far, of all it greets.</summary>
public sealed record SweepProgress(int Checked, int Total);

/// <summary>
/// Finds Loadout Bay without its address. First a UDP probe to each local subnet's broadcast address,
/// which Loadout Bay answers (<c>ps2/src/discovery.c</c>); if none answers, a sweep that greets every
/// address of the local subnets on the control port, which also finds a Loadout Bay older than the probe.
/// <c>docs/bay-protocol.md</c> describes the probe.
/// </summary>
public static class BayDiscovery
{
    public const int Port = 45060;

    public const uint Magic = 0x3144424C; // "LBD1"

    public const int ProbeBytes = 8;

    public const int AnswerBytes = 12 + BayWire.VersionBytes;

    private const uint AnswerFlagDisk = 0x1;

    // A larger subnet is swept only in the /24 around the PC's own address.
    private const int SweepPrefixMin = 22;

    private const int SweepParallel = 512;

    private const int SweepReportEvery = 8;

    public static readonly TimeSpan ProbeWait = TimeSpan.FromSeconds(1.5);

    // The probe goes out three times in its wait, since one datagram can be lost.
    private static readonly TimeSpan[] ProbeSends = [TimeSpan.Zero, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(800)];

    private static readonly TimeSpan SweepHostTime = TimeSpan.FromSeconds(1.5);

    /// <summary>Probes the local subnets, and sweeps them when no Loadout Bay answers the probe.</summary>
    public static Task<IReadOnlyList<FoundBay>> FindAsync(CancellationToken cancellationToken = default) =>
        FindAsync(LocalNetworks(), null, cancellationToken);

    /// <summary>
    /// Probes the given subnets, and sweeps them when no Loadout Bay answers the probe. The sweep reports
    /// its progress, first with nothing checked as it starts.
    /// </summary>
    public static async Task<IReadOnlyList<FoundBay>> FindAsync(
        IReadOnlyList<LocalNetwork> networks,
        IProgress<SweepProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var found = await ProbeAsync(ProbeTargets(networks), Port, ProbeWait, cancellationToken).ConfigureAwait(false);
        return found.Count > 0 ? found : await SweepAsync(networks, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the probe goes from each local address: the subnet's broadcast address and 255.255.255.255,
    /// which reaches a Loadout Bay whose netmask differs from the PC's (PCSX2's DHCP hands out one).
    /// </summary>
    public static IReadOnlyList<(IPAddress Local, IPAddress Target)> ProbeTargets(IReadOnlyList<LocalNetwork> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        return networks
            .SelectMany(network => new[] { Broadcast(network), IPAddress.Broadcast }.Select(target => (network.Address, target)))
            .ToList();
    }

    /// <summary>The IPv4 addresses of this PC's network adapters that are up, without loopback and link-local ones.</summary>
    public static IReadOnlyList<LocalNetwork> LocalNetworks() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork
                && !IsLinkLocal(unicast.Address)
                && unicast.PrefixLength is > 0 and <= 32)
            .Select(unicast => new LocalNetwork(unicast.Address, unicast.PrefixLength))
            .ToList();

    /// <summary>
    /// Sends the probe from each local address to its target and collects the answers that arrive
    /// within <paramref name="wait"/>, one per Loadout Bay.
    /// </summary>
    public static async Task<IReadOnlyList<FoundBay>> ProbeAsync(
        IReadOnlyList<(IPAddress Local, IPAddress Target)> targets,
        int port,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var found = new ConcurrentDictionary<IPAddress, FoundBay>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(wait);
        var probes = targets
            .GroupBy(target => target.Local)
            .Select(group => ProbeFromAsync(group.Key, group.Select(target => target.Target).Distinct().ToList(), port, found, window.Token));
        await Task.WhenAll(probes).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Sorted(found.Values);
    }

    private static async Task ProbeFromAsync(
        IPAddress local,
        IReadOnlyList<IPAddress> targets,
        int port,
        ConcurrentDictionary<IPAddress, FoundBay> found,
        CancellationToken window)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
        }
        catch (SocketException)
        {
            // An adapter that went away since it was listed has nothing to find.
            return;
        }

        using (udp)
        {
            var sends = SendProbesAsync(udp, targets, port, window);
            while (!window.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(window).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows reports a target with no listener on the next receive; other answers may still come.
                    continue;
                }
                catch (SocketException)
                {
                    break;
                }

                if (ReadAnswer(received.Buffer, received.RemoteEndPoint.Address) is { } bay)
                {
                    found.TryAdd(bay.Address, bay);
                }
            }

            await sends.ConfigureAwait(false);
        }
    }

    private static async Task SendProbesAsync(UdpClient udp, IReadOnlyList<IPAddress> targets, int port, CancellationToken window)
    {
        var probe = Probe();
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            foreach (var at in ProbeSends)
            {
                var due = at - TimeProvider.System.GetElapsedTime(started);
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, window).ConfigureAwait(false);
                }

                foreach (var target in targets)
                {
                    try
                    {
                        await udp.SendAsync(probe, new IPEndPoint(target, port), window).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // A target this adapter cannot send to is skipped; the others still go out.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Greets every address of the local subnets on the control port and keeps the ones that answer as Loadout Bay.</summary>
    public static Task<IReadOnlyList<FoundBay>> SweepAsync(IReadOnlyList<LocalNetwork> networks, CancellationToken cancellationToken = default) =>
        SweepAsync(networks, null, cancellationToken);

    /// <summary>
    /// Greets every address of the local subnets on the control port and keeps the ones that answer as Loadout Bay,
    /// reporting how many are done at the start, every <see cref="SweepReportEvery"/> addresses and at the end.
    /// </summary>
    public static async Task<IReadOnlyList<FoundBay>> SweepAsync(
        IReadOnlyList<LocalNetwork> networks,
        IProgress<SweepProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var own = networks.Select(network => network.Address).ToHashSet();
        var hosts = networks.SelectMany(SweepHosts).Where(host => !own.Contains(host)).Distinct().ToList();
        var found = new ConcurrentDictionary<IPAddress, FoundBay>();
        var done = 0;
        progress?.Report(new SweepProgress(0, hosts.Count));
        using var gate = new SemaphoreSlim(SweepParallel);
        await Task.WhenAll(hosts.Select(async host =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await GreetAsync(host, cancellationToken).ConfigureAwait(false) is { } bay)
                {
                    found.TryAdd(host, bay);
                }
            }
            finally
            {
                gate.Release();
            }

            var checkedSoFar = Interlocked.Increment(ref done);
            if (checkedSoFar % SweepReportEvery == 0 || checkedSoFar == hosts.Count)
            {
                progress?.Report(new SweepProgress(checkedSoFar, hosts.Count));
            }
        })).ConfigureAwait(false);
        return Sorted(found.Values);
    }

    private static async Task<FoundBay?> GreetAsync(IPAddress host, CancellationToken cancellationToken)
    {
        using var time = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        time.CancelAfter(SweepHostTime);
        try
        {
            using var client = await LoadoutBayClient.ConnectForUpgradeAsync(host.ToString(), BayWire.Port, time.Token).ConfigureAwait(false);
            var hello = client.Hello;
            return new FoundBay(host, hello.Version, hello.ProtocolVersion, hello.HasHardDisk);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is OperationCanceledException or LoadoutBayException or IOException or SocketException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// The addresses a sweep greets for one local address: its whole subnet without the network and
    /// broadcast addresses, or the /24 around it when the subnet is larger than a /22.
    /// </summary>
    public static IEnumerable<IPAddress> SweepHosts(LocalNetwork network)
    {
        if (SweepRangeOf(network) is not { } range)
        {
            yield break;
        }

        for (var host = ToUInt32(range.First); host <= ToUInt32(range.Last); host++)
        {
            yield return FromUInt32(host);
        }
    }

    /// <summary>
    /// The range a sweep greets for one local address, as <see cref="SweepHosts"/> lists it, or null for
    /// a subnet too small to have other hosts.
    /// </summary>
    public static SweepRange? SweepRangeOf(LocalNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);
        var prefix = network.PrefixLength < SweepPrefixMin ? 24 : network.PrefixLength;
        if (prefix > 30)
        {
            return null;
        }

        var mask = uint.MaxValue << (32 - prefix);
        var start = ToUInt32(network.Address) & mask;
        return new SweepRange(
            FromUInt32(start),
            prefix,
            FromUInt32(start + 1),
            FromUInt32((ToUInt32(network.Address) | ~mask) - 1),
            prefix != network.PrefixLength);
    }

    /// <summary>The subnet's broadcast address, such as 192.168.1.255 for 192.168.1.20/24.</summary>
    public static IPAddress Broadcast(LocalNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);
        var mask = network.PrefixLength == 0 ? 0 : uint.MaxValue << (32 - network.PrefixLength);
        return FromUInt32(ToUInt32(network.Address) | ~mask);
    }

    /// <summary>The probe: the magic and the PC's protocol version.</summary>
    public static byte[] Probe()
    {
        var probe = new byte[ProbeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(probe, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(probe.AsSpan(4), BayWire.ProtocolVersion);
        return probe;
    }

    /// <summary>Reads Loadout Bay's answer to the probe, or null for any other datagram.</summary>
    public static FoundBay? ReadAnswer(ReadOnlySpan<byte> answer, IPAddress from)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (answer.Length != AnswerBytes || BinaryPrimitives.ReadUInt32LittleEndian(answer) != Magic)
        {
            return null;
        }

        var version = answer.Slice(12, BayWire.VersionBytes);
        var end = version.IndexOf((byte)0);
        return new FoundBay(
            from,
            Encoding.UTF8.GetString(end >= 0 ? version[..end] : version),
            BinaryPrimitives.ReadUInt32LittleEndian(answer[4..]),
            (BinaryPrimitives.ReadUInt32LittleEndian(answer[8..]) & AnswerFlagDisk) != 0);
    }

    private static List<FoundBay> Sorted(IEnumerable<FoundBay> bays) =>
        bays.OrderBy(bay => ToUInt32(bay.Address)).ToList();

    private static bool IsLinkLocal(IPAddress address) =>
        (ToUInt32(address) & 0xFFFF0000) == 0xA9FE0000;

    private static uint ToUInt32(IPAddress address) =>
        BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static IPAddress FromUInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
