namespace VpnSample.Dns;

public static class DnsName
{
    public const string DefaultZone = "vpn";

    public static string NormalizeNodeName(string nodeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        string normalized = nodeName.ToLowerInvariant();
        if (normalized.Length > 63 ||
            !IsLetterOrDigit(normalized[0]) ||
            !IsLetterOrDigit(normalized[^1]) ||
            normalized.Any(character => !IsLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "A node name must be a 1-63 character DNS label containing letters, digits, or hyphens.",
                nameof(nodeName));
        }

        return normalized;
    }

    public static string GetFullName(string nodeName, string zone = DefaultZone) =>
        $"{NormalizeNodeName(nodeName)}.{NormalizeZone(zone)}";

    internal static string NormalizeQuestionName(string questionName) =>
        questionName.TrimEnd('.').ToLowerInvariant();

    internal static string NormalizeZone(string zone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        string normalized = zone.TrimEnd('.').ToLowerInvariant();
        if (normalized.Length > 253 || normalized.Split('.').Any(label =>
                label.Length is 0 or > 63 ||
                !IsLetterOrDigit(label[0]) ||
                !IsLetterOrDigit(label[^1]) ||
                label.Any(character => !IsLetterOrDigit(character) && character != '-')))
        {
            throw new ArgumentException("The DNS zone is invalid.", nameof(zone));
        }

        return normalized;
    }

    static bool IsLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
