using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace SentinelDesk.Server.Services;

/// <summary>
/// Reads the agent package embedded in SentinelDesk-Setup.exe (FluentSetup, resource "agent.zip"): its version (the
/// setup's product version) and SHA-256. The server signs these into every office installer it hands out, so the
/// installer can upgrade an agent: the agent's Guard service only installs packages signed by this server.
/// Cached per file, so a new setup file dropped into the installer folder is picked up automatically.
/// </summary>
public static class SetupPackage
{
    public sealed record Info(string Version, string Sha256);

    private static readonly object Gate = new();
    private static (string Path, DateTime Written, long Length, Info? Info)? _cache;

    public static Info? Read(string setupPath)
    {
        var fi = new FileInfo(setupPath);
        lock (Gate)
        {
            if (_cache is { } c && c.Path == fi.FullName && c.Written == fi.LastWriteTimeUtc && c.Length == fi.Length) return c.Info;
            Info? info = null;
            try { info = ReadCore(fi.FullName); } catch { /* not a FluentSetup file: installers just can't upgrade */ }
            _cache = (fi.FullName, fi.LastWriteTimeUtc, fi.Length, info);
            return info;
        }
    }

    private static Info? ReadCore(string path)
    {
        var version = (FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "").Split('+')[0].Trim();
        if (version.Length == 0) return null;
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) return null;
        var md = pe.GetMetadataReader();
        foreach (var h in md.ManifestResources)
        {
            var res = md.GetManifestResource(h);
            if (!res.Implementation.IsNil || md.GetString(res.Name) != "agent.zip") continue;
            // managed resources: [int32 length][bytes] at CLI header ResourcesDirectory + offset
            var dir = pe.PEHeaders.CorHeader!.ResourcesDirectory;
            if (!pe.PEHeaders.TryGetDirectoryOffset(dir, out var start)) return null;
            fs.Position = start + res.Offset;
            Span<byte> len = stackalloc byte[4];
            fs.ReadExactly(len);
            var length = BitConverter.ToInt32(len);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long left = length;
            while (left > 0)
            {
                var n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) return null;
                sha.AppendData(buffer, 0, n);
                left -= n;
            }
            return new Info(version, Convert.ToHexString(sha.GetHashAndReset()));
        }
        return null;
    }
}
