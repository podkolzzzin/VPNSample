using System.Security.Cryptography;
using System.Text;

namespace VpnSample.Mesh;

public sealed class MeshIdentity : IDisposable
{
    readonly ECDiffieHellman key;

    public MeshIdentity(string? privateKeyPath = null)
    {
        key = string.IsNullOrWhiteSpace(privateKeyPath)
            ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
            : LoadOrCreate(privateKeyPath);
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        RandomNumberGenerator.Fill(NoncePrefix);
    }

    public string PublicKey { get; }
    internal byte[] NoncePrefix { get; } = new byte[8];

    internal byte[] DerivePeerKey(string peerPublicKey)
    {
        byte[] encodedPeerKey;
        try
        {
            encodedPeerKey = Convert.FromBase64String(peerPublicKey);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("A peer supplied an invalid mesh public key.", error);
        }

        using ECDiffieHellman peer = ECDiffieHellman.Create();
        try
        {
            peer.ImportSubjectPublicKeyInfo(encodedPeerKey, out int bytesRead);
            if (bytesRead != encodedPeerKey.Length)
                throw new InvalidDataException("A peer public key has trailing data.");
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("A peer supplied an unsupported mesh public key.", error);
        }

        byte[] sharedSecret = key.DeriveKeyMaterial(peer.PublicKey);
        bool localFirst = StringComparer.Ordinal.Compare(PublicKey, peerPublicKey) <= 0;
        string first = localFirst
            ? PublicKey
            : peerPublicKey;
        string second = localFirst ? peerPublicKey : PublicKey;
        byte[] context = Encoding.ASCII.GetBytes($"VPNSample mesh v1|{first}|{second}");
        try
        {
            return HMACSHA256.HashData(sharedSecret, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    public void Dispose() => key.Dispose();

    static ECDiffieHellman LoadOrCreate(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            byte[] encoded = File.ReadAllBytes(fullPath);
            var existing = ECDiffieHellman.Create();
            try
            {
                existing.ImportPkcs8PrivateKey(encoded, out int bytesRead);
                if (bytesRead != encoded.Length)
                    throw new InvalidDataException("The mesh identity file has trailing data.");
                return existing;
            }
            catch
            {
                existing.Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var created = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] privateKey = created.ExportPkcs8PrivateKey();
        try
        {
            using (var file = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                file.Write(privateKey);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(fullPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}
