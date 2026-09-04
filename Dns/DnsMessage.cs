using System.Buffers.Binary;

namespace VpnSample.Dns;

static class DnsMessage
{
    const ushort QueryResponse = 0x8000;
    const ushort AuthoritativeAnswer = 0x0400;
    const ushort RecursionDesired = 0x0100;
    const ushort NameError = 0x0003;
    const ushort InternetClass = 1;
    const ushort ARecord = 1;
    const ushort AaaaRecord = 28;
    const ushort AnyRecord = 255;

    public static byte[]? CreateResponse(ReadOnlySpan<byte> query, OverlayDnsRegistry registry)
    {
        if (query.Length < 12 || ReadUInt16(query, 4) != 1)
            return null;

        ushort queryFlags = ReadUInt16(query, 2);
        if ((queryFlags & QueryResponse) != 0 || (queryFlags & 0x7800) != 0)
            return null;

        if (!TryReadQuestion(query, out string questionName, out int questionEnd))
            return null;

        ushort questionType = ReadUInt16(query, questionEnd - 4);
        ushort questionClass = ReadUInt16(query, questionEnd - 2);
        bool found = registry.TryResolve(questionName, out OverlayDnsRecord? record);
        var answers = new List<(ushort Type, byte[] Address)>();
        if (found && questionClass == InternetClass)
        {
            if (questionType is ARecord or AnyRecord)
                answers.Add((ARecord, record!.Ipv4Address.GetAddressBytes()));
            if (questionType is AaaaRecord or AnyRecord)
                answers.Add((AaaaRecord, record!.Ipv6Address.GetAddressBytes()));
        }

        ushort responseFlags = (ushort)(QueryResponse | AuthoritativeAnswer |
            (queryFlags & RecursionDesired) | (found ? 0 : NameError));
        var response = new List<byte>(12 + questionEnd - 12 + answers.Count * 28);
        WriteUInt16(response, ReadUInt16(query, 0));
        WriteUInt16(response, responseFlags);
        WriteUInt16(response, 1);
        WriteUInt16(response, checked((ushort)answers.Count));
        WriteUInt16(response, 0);
        WriteUInt16(response, 0);
        response.AddRange(query[12..questionEnd].ToArray());

        foreach ((ushort type, byte[] address) in answers)
        {
            WriteUInt16(response, 0xc00c);
            WriteUInt16(response, type);
            WriteUInt16(response, InternetClass);
            WriteUInt32(response, 30);
            WriteUInt16(response, checked((ushort)address.Length));
            response.AddRange(address);
        }

        return response.ToArray();
    }

    static bool TryReadQuestion(
        ReadOnlySpan<byte> query,
        out string questionName,
        out int questionEnd)
    {
        var labels = new List<string>();
        int offset = 12;
        while (offset < query.Length)
        {
            int length = query[offset++];
            if (length == 0)
                break;
            if (length > 63 || offset + length > query.Length)
            {
                questionName = string.Empty;
                questionEnd = 0;
                return false;
            }

            ReadOnlySpan<byte> label = query.Slice(offset, length);
            if (label.ContainsAnyExceptInRange((byte)0x20, (byte)0x7e))
            {
                questionName = string.Empty;
                questionEnd = 0;
                return false;
            }
            labels.Add(System.Text.Encoding.ASCII.GetString(label));
            offset += length;
        }

        if (labels.Count == 0 || offset + 4 > query.Length)
        {
            questionName = string.Empty;
            questionEnd = 0;
            return false;
        }

        questionName = string.Join('.', labels);
        questionEnd = offset + 4;
        return true;
    }

    static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);

    static void WriteUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    static void WriteUInt32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}
