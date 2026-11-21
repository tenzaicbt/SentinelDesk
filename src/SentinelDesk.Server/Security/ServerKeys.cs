using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SentinelDesk.Server.Security;

/// <summary>
/// Server key material, generated on first run and stored in the data directory
/// (DPAPI machine-scope protected on Windows). In production move these to Vault / Key Vault / HSM.
///  - Internal device CA (ECDSA P-256) — issues per-device mTLS client certificates
///  - Payload encryption key (RSA-3072) — unwraps per-batch AES-256-GCM keys
///  - Signing key (ECDSA P-256) — signs policies, commands and update packages
///  - Master key (256-bit) — derives per-device HMAC secrets and JWT signing key
/// </summary>
public sealed class ServerKeys
{
    public X509Certificate2 CaCert { get; }
    public RSA EncryptionKey { get; }
    public ECDsa SigningKey { get; }
    public byte[] MasterKey { get; }
    public string CaCertPem { get; }
    public string EncryptionPublicPem { get; }
    public string SigningPublicPem { get; }

    public ServerKeys(string dataDir)
    {
        var dir = Path.Combine(dataDir, "keys");
        Directory.CreateDirectory(dir);

        MasterKey = LoadOrCreate(Path.Combine(dir, "master.key"), () => RandomNumberGenerator.GetBytes(32));

        var rsaPkcs8 = LoadOrCreate(Path.Combine(dir, "encryption.key"), () => RSA.Create(3072).ExportPkcs8PrivateKey());
        EncryptionKey = RSA.Create();
        EncryptionKey.ImportPkcs8PrivateKey(rsaPkcs8, out _);

        var ecPkcs8 = LoadOrCreate(Path.Combine(dir, "signing.key"), () => ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKey());
        SigningKey = ECDsa.Create();
        SigningKey.ImportPkcs8PrivateKey(ecPkcs8, out _);

        var caPfx = LoadOrCreate(Path.Combine(dir, "device-ca.pfx"), CreateCa);
        CaCert = X509CertificateLoader.LoadPkcs12(caPfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

        CaCertPem = CaCert.ExportCertificatePem();
        EncryptionPublicPem = EncryptionKey.ExportSubjectPublicKeyInfoPem();
        SigningPublicPem = SigningKey.ExportSubjectPublicKeyInfoPem();
    }

    public string DeviceSecret(Guid deviceId)
    {
        using var h = new HMACSHA256(MasterKey);
        return Convert.ToBase64String(h.ComputeHash(Encoding.ASCII.GetBytes("device-secret|" + deviceId.ToString("N"))));
    }

    public byte[] JwtKey()
    {
        using var h = new HMACSHA256(MasterKey);
        return h.ComputeHash("jwt-signing-key"u8.ToArray());
    }

    /// <summary>Encrypts an enrollment token for storage (AES-256-GCM, key derived from the master key): nonce|tag|ciphertext, base64.</summary>
    public string ProtectToken(string token)
    {
        var plain = Encoding.UTF8.GetBytes(token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(TokenKey(), 16)) aes.Encrypt(nonce, plain, cipher, tag, "enroll-token"u8);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Decrypts a value from <see cref="ProtectToken"/>; null if it was tampered with or the keys changed.</summary>
    public string? UnprotectToken(string protectedToken)
    {
        try
        {
            var blob = Convert.FromBase64String(protectedToken);
            if (blob.Length < 29) return null;
            var plain = new byte[blob.Length - 28];
            using var aes = new AesGcm(TokenKey(), 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, "enroll-token"u8);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    private byte[] TokenKey()
    {
        using var h = new HMACSHA256(MasterKey);
        return h.ComputeHash("enroll-token-key"u8.ToArray());
    }

    /// <summary>Signs a device CSR and returns a 1-year client-auth certificate.</summary>
    public X509Certificate2 IssueDeviceCertificate(string csrPem, Guid deviceId)
    {
        var req = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        var issued = new CertificateRequest(new X500DistinguishedName($"CN={deviceId:N}, OU=Devices, O=SentinelDesk"),
            req.PublicKey, HashAlgorithmName.SHA256);
        issued.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        issued.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        issued.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        issued.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issued.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var now = DateTimeOffset.UtcNow;
        using var caKey = CaCert.GetECDsaPrivateKey()!;
        var gen = X509SignatureGenerator.CreateForECDsa(caKey);
        return issued.Create(CaCert.SubjectName, gen, now.AddMinutes(-5), now.AddYears(1), serial);
    }

    /// <summary>Validates that a presented client certificate chains to the device CA.</summary>
    public bool ValidateDeviceCertificate(X509Certificate2 cert)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(CaCert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // revocation = device status in DB
        return chain.Build(cert);
    }

    private static byte[] CreateCa()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=SentinelDesk Device CA, O=SentinelDesk", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return cert.Export(X509ContentType.Pfx);
    }

    private static byte[] LoadOrCreate(string path, Func<byte[]> create)
    {
        if (File.Exists(path)) return Unprotect(File.ReadAllBytes(path));
        var data = create();
        File.WriteAllBytes(path, Protect(data));
        return data;
    }

    private static byte[] Protect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine) : data;

    private static byte[] Unprotect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine) : data;

    private static readonly byte[] Entropy = "SentinelDesk.Server.Keys.v1"u8.ToArray();
}
