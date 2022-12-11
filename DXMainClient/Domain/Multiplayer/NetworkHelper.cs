﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ClientCore;

namespace DTAClient.Domain.Multiplayer;

internal static class NetworkHelper
{
    private const string PingHost = "cncnet.org";
    private const int PingTimeout = 1000;

    private static readonly IReadOnlyCollection<AddressFamily> SupportedAddressFamilies = new[]
    {
        AddressFamily.InterNetwork,
        AddressFamily.InterNetworkV6
    }.AsReadOnly();

    [SupportedOSPlatform("windows")]
    public static IEnumerable<(IPAddress IpAddress, PrefixOrigin PrefixOrigin, SuffixOrigin SuffixOrigin)> GetWindowsPublicIpAddresses()
        => GetUniCastIpAddresses()
        .Where(q => !IsPrivateIpAddress(q.Address))
        .Select(q => (q.Address, q.PrefixOrigin, q.SuffixOrigin));

    public static IEnumerable<IPAddress> GetLocalAddresses()
        => GetUniCastIpAddresses()
        .Select(q => q.Address);

    public static IEnumerable<IPAddress> GetPublicIpAddresses()
        => GetLocalAddresses()
        .Where(q => !IsPrivateIpAddress(q));

    public static IEnumerable<IPAddress> GetPrivateIpAddresses()
        => GetLocalAddresses()
        .Where(IsPrivateIpAddress);

    public static IEnumerable<UnicastIPAddressInformation> GetUniCastIpAddresses()
        => NetworkInterface.GetAllNetworkInterfaces()
        .Where(q => q.OperationalStatus is OperationalStatus.Up)
        .Select(q => q.GetIPProperties())
        .Where(q => q.GatewayAddresses.Any())
        .SelectMany(q => q.UnicastAddresses)
        .Where(q => SupportedAddressFamilies.Contains(q.Address.AddressFamily));

    public static IPAddress GetIpV4BroadcastAddress(UnicastIPAddressInformation unicastIpAddressInformation)
    {
        uint ipAddress = BitConverter.ToUInt32(unicastIpAddressInformation.Address.GetAddressBytes(), 0);
        uint ipMaskV4 = BitConverter.ToUInt32(unicastIpAddressInformation.IPv4Mask.GetAddressBytes(), 0);
        uint broadCastIpAddress = ipAddress | ~ipMaskV4;

        return new IPAddress(BitConverter.GetBytes(broadCastIpAddress));
    }

    public static async ValueTask<IPAddress> DetectPublicIpV4Address(CancellationToken cancellationToken)
    {
        IPAddress[] ipAddresses = await Dns.GetHostAddressesAsync(PingHost, cancellationToken).ConfigureAwait(false);
        using var ping = new Ping();

        foreach (IPAddress ipAddress in ipAddresses.Where(q => q.AddressFamily is AddressFamily.InterNetwork))
        {
            PingReply pingReply = await ping.SendPingAsync(ipAddress, PingTimeout).ConfigureAwait(false);

            if (pingReply.Status is not IPStatus.Success)
                continue;

            IPAddress pingIpAddress = null;
            int ttl = 1;

            while (!ipAddress.Equals(pingIpAddress))
            {
                pingReply = await ping.SendPingAsync(ipAddress, PingTimeout, Array.Empty<byte>(), new(ttl++, false)).ConfigureAwait(false);
                pingIpAddress = pingReply.Address;

                if (ipAddress.Equals(pingIpAddress))
                    break;

                if (!IsPrivateIpAddress(pingReply.Address))
                    return pingReply.Address;
            }
        }

        return null;
    }

    public static async ValueTask<long?> PingAsync(IPAddress ipAddress)
    {
        if ((ipAddress.AddressFamily is AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            || (ipAddress.AddressFamily is AddressFamily.InterNetwork && !Socket.OSSupportsIPv4))
        {
            return null;
        }

        using var ping = new Ping();

        try
        {
            PingReply pingResult = await ping.SendPingAsync(ipAddress, PingTimeout);

            if (pingResult.Status is IPStatus.Success)
                return pingResult.RoundtripTime;
        }
        catch (PingException ex)
        {
            ProgramConstants.LogException(ex);
        }

        return null;
    }

    /// <summary>
    /// Returns a free UDP port number above 1023.
    /// </summary>
    /// <param name="excludedPorts">List of UDP port numbers which are additionally excluded.</param>
    /// <param name="numberOfPorts">The number of free ports to return.</param>
    /// <returns>A free UDP port number on the current system.</returns>
    public static IEnumerable<ushort> GetFreeUdpPorts(IEnumerable<ushort> excludedPorts, ushort numberOfPorts)
    {
        IPEndPoint[] endPoints = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
        List<ushort> activePorts = endPoints.Select(q => (ushort)q.Port).ToArray().Concat(excludedPorts).ToList();
        ushort foundPortCount = 0;

        while (foundPortCount != numberOfPorts)
        {
            ushort foundPort = (ushort)new Random().Next(1024, IPEndPoint.MaxPort);

            if (!activePorts.Contains(foundPort))
            {
                activePorts.Add(foundPort);

                foundPortCount++;

                yield return foundPort;
            }
        }
    }

    private static bool IsPrivateIpAddress(IPAddress ipAddress)
        => ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetworkV6 => ipAddress.IsIPv6SiteLocal
                || ipAddress.IsIPv6UniqueLocal
                || ipAddress.IsIPv6LinkLocal,
            AddressFamily.InterNetwork => IsInRange("10.0.0.0", "10.255.255.255", ipAddress)
                || IsInRange("172.16.0.0", "172.31.255.255", ipAddress)
                || IsInRange("172.16.0.0", "172.31.255.255", ipAddress)
                || IsInRange("192.168.0.0", "192.168.255.255", ipAddress)
                || IsInRange("169.254.0.0", "169.254.255.255", ipAddress)
                || IsInRange("127.0.0.0", "127.255.255.255", ipAddress)
                || IsInRange("0.0.0.0", "0.255.255.255", ipAddress),
            _ => throw new ArgumentOutOfRangeException(nameof(ipAddress.AddressFamily), ipAddress.AddressFamily, null),
        };

    private static bool IsInRange(string startIpAddress, string endIpAddress, IPAddress address)
    {
        uint ipStart = BitConverter.ToUInt32(IPAddress.Parse(startIpAddress).GetAddressBytes().Reverse().ToArray(), 0);
        uint ipEnd = BitConverter.ToUInt32(IPAddress.Parse(endIpAddress).GetAddressBytes().Reverse().ToArray(), 0);
        uint ip = BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray(), 0);

        return ip >= ipStart && ip <= ipEnd;
    }
}