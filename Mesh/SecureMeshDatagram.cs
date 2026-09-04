using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VpnSample.Mesh;

enum MeshDatagramType : byte
{
    Probe = 1,
    ProbeAcknowledgement = 2,
    Data = 3,
    Keepalive = 4
}

static class SecureMeshDatagram
{
    const int FixedHeaderLength = 20;
    const int TagLength = 16;
    static ReadOnlySpan<byte> Magic => "SVD1"u8;

    public static byte[] Encrypt(
        MeshDatagramType type,
        string senderName,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> noncePrefix,
        uint sequence,
        ReadOnlySpan<byte> plaintext)
    {
        byte[] sender = Encoding.ASCII.GetBytes(senderName);
        if (sender.Length is 0 or > 63)
            throw new ArgumentException("The mesh sender name is invalid.", nameof(senderName));
        if (noncePrefix.Length != 8)
            throw new ArgumentException("The nonce prefix must contain eight bytes.", nameof(noncePrefix));

        int headerLength = FixedHeaderLength + sender.Length;
        var datagram = new byte[headerLength + plaintext.Length + TagLength];
        Magic.CopyTo(datagram);
        datagram[4] = 1;
        datagram[5] = (byte)type;
        datagram[6] = checked((byte)sender.Length);
        datagram[7] = 0;
        noncePrefix.CopyTo(datagram.AsSpan(8, 8));
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(16, 4), sequence);
        sender.CopyTo(datagram, FixedHeaderLength);

        ReadOnlySpan<byte> nonce = datagram.AsSpan(8, 12);
        ReadOnlySpan<byte> associatedData = datagram.AsSpan(0, headerLength);
        Span<byte> ciphertext = datagram.AsSpan(headerLength, plaintext.Length);
        Span<byte> tag = datagram.AsSpan(headerLength + plaintext.Length, TagLength);
        using var cipher = new AesGcm(key, TagLength);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return datagram;
    }

    public static bool TryReadSender(
        ReadOnlySpan<byte> datagram,
        out string senderName,
        out MeshDatagramType type)
    {
        if (datagram.Length < FixedHeaderLength + 1 + TagLength ||
            !datagram[..4].SequenceEqual(Magic) ||
            datagram[4] != 1 ||
            datagram[6] is 0 or > 63)
        {
            senderName = string.Empty;
            type = default;
            return false;
        }

        int headerLength = FixedHeaderLength + datagram[6];
        if (datagram.Length < headerLength + TagLength ||
            !Enum.IsDefined((MeshDatagramType)datagram[5]))
        {
            senderName = string.Empty;
            type = default;
            return false;
        }

        ReadOnlySpan<byte> sender = datagram.Slice(FixedHeaderLength, datagram[6]);
        if (sender.ContainsAnyExceptInRange((byte)0, (byte)0x7f))
        {
            senderName = string.Empty;
            type = default;
            return false;
        }

        senderName = Encoding.ASCII.GetString(sender);
        type = (MeshDatagramType)datagram[5];
        return true;
    }

    public static bool TryDecrypt(
        ReadOnlySpan<byte> datagram,
        string expectedSender,
        ReadOnlySpan<byte> key,
        out MeshDatagramType type,
        out ulong noncePrefix,
        out uint sequence,
        out byte[] plaintext)
    {
        if (!TryReadSender(datagram, out string senderName, out type) ||
            !StringComparer.Ordinal.Equals(senderName, expectedSender))
        {
            noncePrefix = 0;
            sequence = 0;
            plaintext = [];
            return false;
        }

        int headerLength = FixedHeaderLength + datagram[6];
        int plaintextLength = datagram.Length - headerLength - TagLength;
        noncePrefix = BinaryPrimitives.ReadUInt64BigEndian(datagram.Slice(8, 8));
        sequence = BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(16, 4));
        plaintext = new byte[plaintextLength];
        try
        {
            using var cipher = new AesGcm(key, TagLength);
            cipher.Decrypt(
                datagram.Slice(8, 12),
                datagram.Slice(headerLength, plaintextLength),
                datagram[^TagLength..],
                plaintext,
                datagram[..headerLength]);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = [];
            return false;
        }
    }
}

sealed class ReplayWindow
{
    ulong noncePrefix;
    uint highestSequence;
    ulong seen;
    bool initialized;

    public bool TryAccept(ulong candidatePrefix, uint sequence)
    {
        if (sequence == 0)
            return false;
        if (!initialized)
        {
            initialized = true;
            noncePrefix = candidatePrefix;
            highestSequence = sequence;
            seen = 1;
            return true;
        }
        if (candidatePrefix != noncePrefix)
            return false;

        if (sequence > highestSequence)
        {
            uint shift = sequence - highestSequence;
            seen = shift >= 64 ? 1 : (seen << (int)shift) | 1;
            highestSequence = sequence;
            return true;
        }

        uint age = highestSequence - sequence;
        if (age >= 64)
            return false;
        ulong bit = 1UL << (int)age;
        if ((seen & bit) != 0)
            return false;
        seen |= bit;
        return true;
    }
}
