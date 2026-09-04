using System.Net;
using System.Net.Sockets;

namespace VpnSample.Dns;

public sealed class OverlayDnsRegistry
{
    readonly object registrationsLock = new();
    readonly Dictionary<string, RegistrationEntry> registrations =
        new(StringComparer.OrdinalIgnoreCase);

    public OverlayDnsRegistry(string zone = DnsName.DefaultZone)
    {
        Zone = DnsName.NormalizeZone(zone);
    }

    public string Zone { get; }

    public OverlayDnsRegistration? TryRegister(
        string nodeName,
        IPAddress ipv4Address,
        IPAddress ipv6Address)
    {
        ArgumentNullException.ThrowIfNull(ipv4Address);
        ArgumentNullException.ThrowIfNull(ipv6Address);
        if (ipv4Address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("An IPv4 address is required.", nameof(ipv4Address));
        if (ipv6Address.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException("An IPv6 address is required.", nameof(ipv6Address));

        string normalizedName = DnsName.NormalizeNodeName(nodeName);
        string fullName = DnsName.GetFullName(normalizedName, Zone);
        var record = new OverlayDnsRecord(normalizedName, fullName, ipv4Address, ipv6Address);
        var leaseId = Guid.NewGuid();

        lock (registrationsLock)
        {
            if (registrations.ContainsKey(fullName))
                return null;
            registrations.Add(fullName, new RegistrationEntry(leaseId, record));
        }

        return new OverlayDnsRegistration(this, fullName, leaseId, record);
    }

    public bool TryResolve(string questionName, out OverlayDnsRecord? record)
    {
        string normalized = DnsName.NormalizeQuestionName(questionName);
        lock (registrationsLock)
        {
            if (registrations.TryGetValue(normalized, out RegistrationEntry? entry))
            {
                record = entry.Record;
                return true;
            }
        }

        record = null;
        return false;
    }

    internal void Unregister(string fullName, Guid leaseId)
    {
        lock (registrationsLock)
        {
            if (registrations.TryGetValue(fullName, out RegistrationEntry? entry) &&
                entry.LeaseId == leaseId)
            {
                registrations.Remove(fullName);
            }
        }
    }

    sealed record RegistrationEntry(Guid LeaseId, OverlayDnsRecord Record);
}

public sealed record OverlayDnsRecord(
    string NodeName,
    string FullName,
    IPAddress Ipv4Address,
    IPAddress Ipv6Address);

public sealed class OverlayDnsRegistration : IDisposable
{
    readonly OverlayDnsRegistry registry;
    readonly string fullName;
    readonly Guid leaseId;
    int isDisposed;

    internal OverlayDnsRegistration(
        OverlayDnsRegistry registry,
        string fullName,
        Guid leaseId,
        OverlayDnsRecord record)
    {
        this.registry = registry;
        this.fullName = fullName;
        this.leaseId = leaseId;
        Record = record;
    }

    public OverlayDnsRecord Record { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref isDisposed, 1) == 0)
            registry.Unregister(fullName, leaseId);
    }
}
