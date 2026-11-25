using System.Security.Cryptography;

namespace Ro3Emu.Patcher;

/// <summary>
/// RSA-3072 SHA-256 PKCS#1 v1.5 signing and verification for mutable <c>channels/&lt;name&gt;.json</c> pointers.
/// When a launcher pins <c>channelPublicKey</c>, a tampered or substituted channel file is rejected before any
/// manifest hash or launcher self-update metadata is trusted.
/// </summary>
public static class ChannelSignature
{
    public const int MinimumKeyBits = 3072;

    public static string SignatureRelative(string channel) => $"channels/{channel}.json.sig";

    public static (string PrivatePem, string PublicPem) GenerateKeyPair()
    {
        using var rsa = RSA.Create(MinimumKeyBits);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    public static byte[] Sign(ReadOnlySpan<byte> channelJson, string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        if (rsa.KeySize < MinimumKeyBits)
        {
            throw new CryptographicException($"Channel signing key must be at least {MinimumKeyBits} bits (got {rsa.KeySize}).");
        }
        return rsa.SignData(channelJson, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public static bool Verify(ReadOnlySpan<byte> channelJson, ReadOnlySpan<byte> signature, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem) || signature.IsEmpty)
        {
            return false;
        }
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            if (rsa.KeySize < MinimumKeyBits)
            {
                return false;
            }
            return rsa.VerifyData(channelJson, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
