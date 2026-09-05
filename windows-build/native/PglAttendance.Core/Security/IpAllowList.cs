using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace PglAttendance.Core.Security;

/// <summary>
/// Allow-list of client addresses, accepting single IPs ("192.168.1.20") and
/// CIDR ranges ("192.168.1.0/24"), IPv4 or IPv6.
///
/// An empty list means "no restriction" — every caller still has to
/// authenticate, so an empty list is a safe default rather than an open door.
///
/// Kestrel reports clients on a dual-stack socket as IPv4-mapped IPv6
/// (::ffff:192.168.1.20), so addresses are normalised before comparison;
/// without that, a rule typed as plain IPv4 would never match.
/// </summary>
public sealed class IpAllowList
{
    private readonly List<(IPAddress Network, int Prefix)> _rules = new();

    public IpAllowList(IEnumerable<string>? entries)
    {
        if (entries is null) return;
        foreach (var raw in entries)
        {
            if (TryParseRule(raw, out var network, out var prefix))
                _rules.Add((network, prefix));
        }
    }

    /// <summary>True when no rules are configured — i.e. any address is allowed.</summary>
    public bool IsEmpty => _rules.Count == 0;

    public bool IsAllowed(IPAddress? address)
    {
        if (_rules.Count == 0) return true;
        if (address is null) return false;

        var candidate = Normalize(address);
        foreach (var (network, prefix) in _rules)
        {
            if (network.AddressFamily != candidate.AddressFamily) continue;
            if (Matches(candidate, network, prefix)) return true;
        }
        return false;
    }

    /// <summary>Validates user input for the settings UI without applying it.</summary>
    public static bool IsValidRule(string? entry) => TryParseRule(entry, out _, out _);

    private static bool TryParseRule(string? entry, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        if (string.IsNullOrWhiteSpace(entry)) return false;

        var text = entry.Trim();
        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            if (!IPAddress.TryParse(text, out var single)) return false;
            network = Normalize(single);
            prefix = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            return true;
        }

        var addrPart = text.Substring(0, slash);
        var prefixPart = text.Substring(slash + 1);
        if (!IPAddress.TryParse(addrPart, out var parsed)) return false;
        if (!int.TryParse(prefixPart, out var bits)) return false;

        network = Normalize(parsed);
        var max = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (bits < 0 || bits > max) return false;
        prefix = bits;
        return true;
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool Matches(IPAddress candidate, IPAddress network, int prefix)
    {
        var a = candidate.GetAddressBytes();
        var b = network.GetAddressBytes();
        if (a.Length != b.Length) return false;

        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
            if (a[i] != b[i]) return false;

        if (remainingBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }
}
