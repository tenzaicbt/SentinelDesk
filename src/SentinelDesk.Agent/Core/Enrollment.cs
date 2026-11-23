using System.Management;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Core;

/// <summary>Device key pair (TPM-backed when available) and client certificate handling.</summary>
public static class DeviceKeys
{
    private const string TpmProvider = "Microsoft Platform Crypto Provider";
    private const string SoftwareProvider = "Microsoft Software Key Storage Provider";

    public static ECDsa CreateKey(out string name, out string provider, out bool machine)
    {
        name = "SentinelDesk-Device-" + Guid.NewGuid().ToString("N")[..12];
        machine = AgentPaths.IsAdmin();
        foreach (var prov in new[] { TpmProvider, SoftwareProvider })
        {
            try
            {
                var p = new CngKeyCreationParameters
                {
                    Provider = new CngProvider(prov),
                    KeyCreationOptions = machine ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
                    ExportPolicy = CngExportPolicies.None, // private key never leaves the TPM / KSP
                    KeyUsage = CngKeyUsages.AllUsages,
                };
                var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, p);
                provider = prov;
                return new ECDsaCng(key);
            }
            catch (CryptographicException) { /* TPM missing or unsupported → fall back */ }
        }
        throw new CryptographicException("Unable to create device key");
    }

    public static ECDsa? OpenKey(AgentConfig c)
    {
        if (c.KeyName is null || c.KeyProvider is null) return null;
        var key = CngKey.Open(c.KeyName, new CngProvider(c.KeyProvider), c.KeyIsMachine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None);
        return new ECDsaCng(key);
    }

    public static X509Certificate2? LoadClientCertificate(AgentConfig c, ILogger log)
    {
        if (c.ClientCertPem is null) return null;
        try
        {
            using var pub = X509Certificate2.CreateFromPem(c.ClientCertPem);
            using var key = OpenKey(c);
            return key is null ? null : pub.CopyWithPrivateKey(key);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Device client certificate unavailable — continuing with HMAC authentication only");
            return null;
        }
    }
}

public static class Enrollment
{
    public static async Task<AgentConfig> EnrollAsync(string serverUrl, string token, string? pinnedSha256, bool trustOnFirstUse, ILogger log, CancellationToken ct, AgentConfig? target = null)
    {
        AgentPaths.EnsureDataDir();
        var config = target ?? AgentConfig.Load();
        var (oldUrl, oldPin) = (config.ServerUrl, config.PinnedServerCertSha256);
        try { return await EnrollCoreAsync(config, serverUrl, token, pinnedSha256, trustOnFirstUse, log, ct); }
        catch
        {
            config.ServerUrl = oldUrl;   // keep the working connection when re-enrolling fails
            config.PinnedServerCertSha256 = oldPin;
            throw;
        }
    }

    private static async Task<AgentConfig> EnrollCoreAsync(AgentConfig config, string serverUrl, string token, string? pinnedSha256, bool trustOnFirstUse, ILogger log, CancellationToken ct)
    {
        config.ServerUrl = serverUrl.TrimEnd('/');
        config.PinnedServerCertSha256 = pinnedSha256?.Replace(":", "").ToUpperInvariant() ?? config.PinnedServerCertSha256;

        using var key = DeviceKeys.CreateKey(out var keyName, out var provider, out var machine);
        log.LogInformation("Device key created in {Provider}", provider);
        var req = new CertificateRequest($"CN={Environment.MachineName}", key, HashAlgorithmName.SHA256);
        var csr = req.CreateSigningRequestPem();

        string? observedHash = null;
        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert is null) return false;
                    observedHash = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
                    if (errors == SslPolicyErrors.None) return true;
                    if (config.PinnedServerCertSha256 is { } pin) return string.Equals(pin, observedHash, StringComparison.OrdinalIgnoreCase);
                    return trustOnFirstUse;
                }
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(config.ServerUrl + "/"), Timeout = TimeSpan.FromSeconds(60) };
        var (serial, os, build, domain) = MachineFacts();
        var res = await http.PostAsJsonAsync("agent/v1/enroll", new EnrollRequest(token.Trim(), MachineId(), Environment.MachineName, domain, serial,
            os, build, AgentInfo.Version, csr), Json.Options, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Enrollment failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}");
        var body = (await res.Content.ReadFromJsonAsync<EnrollResponse>(Json.Options, ct))!;

        // TOFU pin: remember the server certificate when it isn't publicly trusted
        if (trustOnFirstUse && config.PinnedServerCertSha256 is null && observedHash is not null && config.ServerUrl.StartsWith("https"))
            config.PinnedServerCertSha256 = observedHash;

        config.DeviceId = body.DeviceId;
        config.SetSecret(body.DeviceSecret);
        config.ClientCertPem = body.ClientCertPem;
        config.CaCertPem = body.CaCertPem;
        config.ServerEncryptionKeyPem = body.ServerEncryptionKeyPem;
        config.ServerSigningKeyPem = body.ServerSigningKeyPem;
        config.KeyName = keyName;
        config.KeyProvider = provider;
        config.KeyIsMachine = machine;
        config.Status = body.Status;
        config.EnrolledAtUtc = DateTime.UtcNow;
        config.Save();
        return config;
    }

    public static string MachineId()
    {
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (k?.GetValue("MachineGuid") is string g) return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(g)))[..32];
        }
        catch { }
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName)))[..32];
    }

    public static (string? Serial, string Os, string Build, string? Domain) MachineFacts()
    {
        string? serial = null, domain = null;
        var os = "Windows";
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                foreach (var o in s.Get()) serial = o["SerialNumber"]?.ToString()?.Trim();
            using (var s = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                foreach (var o in s.Get()) os = o["Caption"]?.ToString()?.Replace("Microsoft ", "").Trim() ?? os;
            using (var s = new ManagementObjectSearcher("SELECT Domain, PartOfDomain FROM Win32_ComputerSystem"))
                foreach (var o in s.Get()) domain = o["PartOfDomain"] is true ? o["Domain"]?.ToString() : null;
        }
        catch { }
        return (serial, os, OsBuild(), domain);
    }

    public static string OsBuild()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return $"{k?.GetValue("CurrentBuildNumber")}.{k?.GetValue("UBR")}";
        }
        catch { return Environment.OSVersion.Version.ToString(); }
    }
}
