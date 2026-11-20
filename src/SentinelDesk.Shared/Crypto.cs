using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SentinelDesk.Shared;

/// <summary>
/// Defence-in-depth payload encryption (on top of TLS):
/// per-batch random AES-256-GCM key, wrapped with the server's RSA-3072 public key (OAEP-SHA256).
/// </summary>
public static class PayloadCrypto
{
    public static TelemetryBatch Encrypt(long seq, byte[] plaintextJson, RSA serverPublicKey)
    {
        var compressed = Gzip(plaintextJson);
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[compressed.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, compressed, cipher, tag, AssociatedData(seq));
        var wrapped = serverPublicKey.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        CryptographicOperations.ZeroMemory(key);
        return new TelemetryBatch
        {
            BatchSeq = seq,
            WrappedKey = Convert.ToBase64String(wrapped),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Ciphertext = Convert.ToBase64String(cipher)
        };
    }

    public static byte[] Decrypt(TelemetryBatch batch, RSA serverPrivateKey)
    {
        var key = serverPrivateKey.Decrypt(Convert.FromBase64String(batch.WrappedKey), RSAEncryptionPadding.OaepSHA256);
        try
        {
            var cipher = Convert.FromBase64String(batch.Ciphertext);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(batch.Nonce), cipher, Convert.FromBase64String(batch.Tag), plain, AssociatedData(batch.BatchSeq));
            return Gunzip(plain);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] AssociatedData(long seq) => Encoding.ASCII.GetBytes($"sentineldesk.v1|{seq}");

    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) gz.Write(data);
        return ms.ToArray();
    }

    public static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>Local symmetric encryption for at-rest buffers (AES-256-GCM, nonce||tag||cipher).</summary>
public static class LocalCrypto
{
    public static byte[] Seal(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        var output = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, 12);
        cipher.CopyTo(output, 28);
        return output;
    }

    public static byte[] Open(byte[] sealedData, byte[] key)
    {
        var nonce = sealedData.AsSpan(0, 12);
        var tag = sealedData.AsSpan(12, 16);
        var cipher = sealedData.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

/// <summary>ECDSA P-256 signatures for policies, commands and update packages.</summary>
public static class Signing
{
    public static string Sign(ECDsa key, string data) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(data), HashAlgorithmName.SHA256));

    public static bool Verify(ECDsa publicKey, string data, string signatureBase64)
    {
        try
        {
            return publicKey.VerifyData(Encoding.UTF8.GetBytes(data), Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }
}

/// <summary>Device request authentication: "Device {deviceId}.{unixTs}.{nonce}.{hmac}".</summary>
public static class DeviceAuth
{
    public const string Scheme = "Device";

    public static string CreateToken(Guid deviceId, string secretBase64)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var sig = Compute(deviceId, ts, nonce, secretBase64);
        return $"{deviceId:N}.{ts}.{nonce}.{sig}";
    }

    public static string Compute(Guid deviceId, long ts, string nonce, string secretBase64)
    {
        using var h = new HMACSHA256(Convert.FromBase64String(secretBase64));
        return Convert.ToHexString(h.ComputeHash(Encoding.ASCII.GetBytes($"{deviceId:N}.{ts}.{nonce}")));
    }
}

/// <summary>Password/key hashing (PBKDF2-HMAC-SHA256, 310k iterations) — used for uninstall keys.</summary>
public static class KeyHash
{
    private const int Iterations = 310_000;

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string secret, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        var iterations = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
