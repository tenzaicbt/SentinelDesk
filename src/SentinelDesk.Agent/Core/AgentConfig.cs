using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using SentinelDesk.Shared;

namespace SentinelDesk.Agent.Core;

public static class AgentPaths
{
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("SENTINELDESK_DATA") is { Length: > 0 } d
        ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SentinelDesk");

    public static string ConfigFile => Path.Combine(DataDir, "agent.json");
    public static string PolicyFile => Path.Combine(DataDir, "policy.json");
    public static string QueueDb => Path.Combine(DataDir, "queue.db");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string GuardEvents => Path.Combine(DataDir, "guard-events.jsonl");
    public static string UpdatesDir => Path.Combine(DataDir, "updates");
    public static string UninstallMarker => Path.Combine(DataDir, "uninstall.authorized");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string InstallDir => AppContext.BaseDirectory;
    public static string TrayExe => Path.Combine(InstallDir, "SentinelDesk.Tray.exe");

    public const string AgentServiceName = "SentinelDeskAgent";
    public const string GuardServiceName = "SentinelDeskGuard";

    /// <summary>Creates the data directory; when running as SYSTEM it is locked to SYSTEM only (it holds the device secret,
    /// queue key and uninstall-key hash, all readable by anyone who can read the folder).</summary>
    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(UpdatesDir);
        Directory.CreateDirectory(LogsDir);
        if (!OperatingSystem.IsWindows() || !IsSystem()) return;
        try { ServiceProtection.LockDataDir(DataDir); } catch { /* best effort */ }
    }

    public static bool IsSystem()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem;
    }

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Persistent agent identity &amp; trust material. Secrets are DPAPI (machine) protected.</summary>
public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "";
    public Guid DeviceId { get; set; }
    public string? ProtectedSecret { get; set; }
    public string? ClientCertPem { get; set; }
    public string? KeyName { get; set; }
    public string? KeyProvider { get; set; }
    public bool KeyIsMachine { get; set; }
    public string? CaCertPem { get; set; }
    public string? ServerEncryptionKeyPem { get; set; }
    public string? ServerSigningKeyPem { get; set; }
    /// <summary>SHA-256 thumbprint of the server TLS certificate to pin (when not publicly trusted).</summary>
    public string? PinnedServerCertSha256 { get; set; }
    public string? UninstallKeyHash { get; set; }
    public string? OneTimeUninstallHash { get; set; }
    public DateTime? OneTimeUninstallExpiresUtc { get; set; }
    public string Status { get; set; } = "unenrolled";
    public DateTime? EnrolledAtUtc { get; set; }

    public bool IsEnrolled => DeviceId != Guid.Empty && ProtectedSecret is not null && ServerEncryptionKeyPem is not null;

    private static readonly byte[] Entropy = "SentinelDesk.Agent.v1"u8.ToArray();
    private static readonly Lock FileLock = new();

    public string? GetSecret() => ProtectedSecret is null ? null : Unprotect(ProtectedSecret);
    public void SetSecret(string secret) => ProtectedSecret = Protect(secret);

    public static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine));

    public static string Unprotect(string value) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine));

    public static AgentConfig Load()
    {
        lock (FileLock)
        {
            if (!File.Exists(AgentPaths.ConfigFile)) return new AgentConfig();
            return Json.Deserialize<AgentConfig>(File.ReadAllText(AgentPaths.ConfigFile)) ?? new AgentConfig();
        }
    }

    public void Save()
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(AgentPaths.DataDir);
            var tmp = AgentPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(this));
            File.Move(tmp, AgentPaths.ConfigFile, true);
        }
    }

    public RSA ServerEncryptionKey()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(ServerEncryptionKeyPem);
        return rsa;
    }

    public ECDsa ServerSigningKey()
    {
        var ec = ECDsa.Create();
        ec.ImportFromPem(ServerSigningKeyPem);
        return ec;
    }
}

/// <summary>Local symmetric key for at-rest encryption of the offline queue (DPAPI machine-protected).</summary>
public static class LocalKey
{
    private static byte[]? _key;

    public static byte[] Get()
    {
        if (_key is not null) return _key;
        var path = Path.Combine(AgentPaths.DataDir, "queue.key");
        var entropy = "SentinelDesk.Queue.v1"u8.ToArray();
        if (File.Exists(path))
            return _key = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.LocalMachine);
        _key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, ProtectedData.Protect(_key, entropy, DataProtectionScope.LocalMachine));
        return _key;
    }
}
