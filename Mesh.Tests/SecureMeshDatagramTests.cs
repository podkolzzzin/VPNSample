using VpnSample.Mesh;

namespace VpnSample.Mesh.Tests;

public sealed class SecureMeshDatagramTests
{
    [Fact]
    public void PersistsNodeIdentityAcrossRestarts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vpnsample-mesh-{Guid.NewGuid():N}.key");
        try
        {
            string firstPublicKey;
            using (var first = new MeshIdentity(path))
                firstPublicKey = first.PublicKey;
            using var second = new MeshIdentity(path);

            Assert.Equal(firstPublicKey, second.PublicKey);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EncryptsAuthenticatesAndDecryptsForPeer()
    {
        using var alice = new MeshIdentity();
        using var bob = new MeshIdentity();
        byte[] aliceKey = alice.DerivePeerKey(bob.PublicKey);
        byte[] bobKey = bob.DerivePeerKey(alice.PublicKey);
        byte[] plaintext = [1, 2, 3, 4];

        byte[] datagram = SecureMeshDatagram.Encrypt(
            MeshDatagramType.Data,
            "alice",
            aliceKey,
            alice.NoncePrefix,
            7,
            plaintext);

        Assert.Equal(aliceKey, bobKey);
        Assert.True(SecureMeshDatagram.TryDecrypt(
            datagram,
            "alice",
            bobKey,
            out MeshDatagramType type,
            out _,
            out uint sequence,
            out byte[] decrypted));
        Assert.Equal(MeshDatagramType.Data, type);
        Assert.Equal(7U, sequence);
        Assert.Equal(plaintext, decrypted);

        datagram[^1] ^= 1;
        Assert.False(SecureMeshDatagram.TryDecrypt(
            datagram,
            "alice",
            bobKey,
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void ReplayWindowAllowsReorderingButRejectsDuplicatesAndNonceChanges()
    {
        var replay = new ReplayWindow();

        Assert.True(replay.TryAccept(42, 10));
        Assert.True(replay.TryAccept(42, 12));
        Assert.True(replay.TryAccept(42, 11));
        Assert.False(replay.TryAccept(42, 11));
        Assert.False(replay.TryAccept(43, 13));
    }
}
