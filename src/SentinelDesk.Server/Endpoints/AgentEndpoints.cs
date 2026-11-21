using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Endpoints;

/// <summary>Agent-facing API (behind the gateway, mTLS + device HMAC).</summary>
public static class AgentEndpoints
{
    public static void MapAgentApi(this WebApplication app)
    {
        var g = app.MapGroup("/agent/v1").RequireRateLimiting("agent");

        g.MapPost("/enroll", Enroll).AllowAnonymous();

        var authed = g.MapGroup("").RequireAuthorization("Device");
        authed.MapPost("/heartbeat", Heartbeat);
        authed.MapPost("/telemetry", Upload).WithRequestTimeout(TimeSpan.FromSeconds(60));
        authed.MapGet("/policy", GetPolicy);
        authed.MapPost("/commands/{id:guid}/result", CommandResultEndpoint);
        authed.MapGet("/releases/{id:guid}/package", DownloadRelease);
    }

    private static Guid DeviceId(ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    private static async Task<IResult> Enroll(EnrollRequest req, AppDb db, ServerKeys keys, AuditService audit,
        HttpContext http, ILogger<AppDb> log)
    {
        var hash = HashToken(req.EnrollToken);
        var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token is null || token.Revoked || token.ExpiresAt < DateTime.UtcNow || token.UsesLeft <= 0)
        {
            log.LogWarning("Rejected enrollment from {Host} ({Ip}): invalid token", req.Hostname, http.Connection.RemoteIpAddress);
            return Results.Problem("Invalid or expired enrollment token", statusCode: 403);
        }

        // Re-enrollment of the same machine re-uses the device record (e.g. after reinstall)
        var device = await db.Devices.FirstOrDefaultAsync(d => d.MachineId == req.MachineId && !d.IsSimulated && d.Status != DeviceStatus.Retired);
        var isNew = device is null;
        device ??= new Device { MachineId = req.MachineId, EnrolledAt = DateTime.UtcNow };
        device.Hostname = req.Hostname;
        device.Domain = req.Domain;
        device.Serial = req.Serial;
        device.Os = req.Os;
        device.OsBuild = req.OsBuild;
        device.AgentVersion = req.AgentVersion;
        device.OrgUnitId ??= token.OrgUnitId;
        device.EnrollmentTokenId = token.Id;
        device.LastIp = http.Connection.RemoteIpAddress?.ToString();
        if (isNew) device.Status = token.AutoApprove ? DeviceStatus.Active : DeviceStatus.Pending;

        X509CertificateResult cert;
        try { cert = Issue(keys, req.CsrPem, device.Id); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Bad CSR from {Host}", req.Hostname);
            return Results.Problem("Invalid certificate signing request", statusCode: 400);
        }
        device.LastBatchSeq = 0; // fresh install → fresh local queue sequence
        device.CertThumbprint = cert.Thumbprint;
        device.CertExpiresAt = cert.NotAfter;
        if (isNew) db.Devices.Add(device);

        token.UsesLeft--;
        token.UsedCount++;
        db.Events.Add(new DeviceEvent
        {
            DeviceId = device.Id, Time = DateTime.UtcNow, Type = isNew ? "agent.enrolled" : "agent.reenrolled",
            Severity = "info", Message = $"Enrolled with token '{token.Name}' (agent {req.AgentVersion})"
        });
        await db.SaveChangesAsync();
        await audit.LogAsync("device.enroll", $"{device.Hostname} ({device.Id})", $"token={token.Name}");

        // A fresh install must never execute commands queued for the previous installation - above all a pending
        // remote "uninstall", which would otherwise remove the newly installed agent on its first heartbeat.
        if (!isNew)
        {
            var now = DateTime.UtcNow;
            var cancelled = await db.Commands.Where(c => c.DeviceId == device.Id && (c.Status == "pending" || c.Status == "sent"))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "cancelled").SetProperty(c => c.CompletedAt, now)
                    .SetProperty(c => c.Result, "Cancelled: the agent was reinstalled before this command was delivered"));
            if (cancelled > 0)
                await audit.LogAsync("device.commandsCancelled", $"{device.Hostname} ({device.Id})", $"{cancelled} pending command(s) cancelled on re-enrollment");
        }

        return Results.Ok(new EnrollResponse(device.Id, keys.DeviceSecret(device.Id), cert.Pem, keys.CaCertPem,
            keys.EncryptionPublicPem, keys.SigningPublicPem, device.Status.ToString().ToLowerInvariant()));
    }

    private sealed record X509CertificateResult(string Pem, string Thumbprint, DateTime NotAfter);

    private static X509CertificateResult Issue(ServerKeys keys, string csr, Guid id)
    {
        using var c = keys.IssueDeviceCertificate(csr, id);
        return new X509CertificateResult(c.ExportCertificatePem(), c.Thumbprint, c.NotAfter.ToUniversalTime());
    }

    private static async Task<IResult> Heartbeat(HeartbeatRequest hb, ClaimsPrincipal user, AppDb db, DeviceCache cache,
        HeartbeatService heartbeat, PolicyService policies, CommandService commands, PresenceStore presence, HttpContext http)
    {
        var id = DeviceId(user);
        var device = await db.Devices.FirstAsync(d => d.Id == id);
        await heartbeat.ApplyAsync(db, device, hb, http.Connection.RemoteIpAddress?.ToString(), presence.AgentConnection(id) is not null);

        var response = new HeartbeatResponse { Status = device.Status.ToString().ToLowerInvariant(), ServerTimeUtc = DateTime.UtcNow };
        if (device.Status != DeviceStatus.Active) return Results.Ok(response);

        response.PolicyVersion = (await policies.ResolveAsync(device)).EffectiveVersion;

        // Fallback command delivery when the live channel is down
        var pending = await db.Commands.Where(c => c.DeviceId == id && (c.Status == "pending" || c.Status == "sent") && c.ExpiresAt > DateTime.UtcNow)
            .OrderBy(c => c.CreatedAt).Take(10).ToListAsync();
        foreach (var c in pending) { response.Commands.Add(commands.ToSigned(c)); c.Status = "sent"; }

        // Staged rollout: deterministic bucket per device
        var release = await db.Releases.AsNoTracking().Where(r => r.RolloutPercent > 0).OrderByDescending(r => r.UploadedAt).FirstOrDefaultAsync();
        if (release is not null && IsNewer(release.Version, hb.AgentVersion) && Bucket(id) < release.RolloutPercent)
            response.Update = new AgentUpdateInfo
            {
                Version = release.Version, Url = $"/agent/v1/releases/{release.Id}/package", Sha256 = release.Sha256, Signature = release.Signature
            };
        await db.SaveChangesAsync();
        return Results.Ok(response);
    }

    public static int Bucket(Guid id) => (int)(BitConverter.ToUInt32(SHA256.HashData(id.ToByteArray()), 0) % 100);

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b) && a > b;

    private static async Task<IResult> Upload(TelemetryBatch batch, ClaimsPrincipal user, AppDb db, ServerKeys keys,
        IngestService ingest, ILogger<IngestService> log)
    {
        var id = DeviceId(user);
        var device = await db.Devices.FirstAsync(d => d.Id == id);
        if (device.Status != DeviceStatus.Active) return Results.Ok(new UploadAck { BatchSeq = batch.BatchSeq, Accepted = false });
        if (batch.BatchSeq <= device.LastBatchSeq)
            return Results.Ok(new UploadAck { BatchSeq = batch.BatchSeq, Accepted = true, Duplicate = true });

        TelemetryPayload? payload;
        try
        {
            var plain = PayloadCrypto.Decrypt(batch, keys.EncryptionKey);
            payload = Json.Deserialize<TelemetryPayload>(plain);
        }
        catch (CryptographicException ex)
        {
            log.LogWarning(ex, "Payload decryption failed for {Device}", id);
            return Results.Problem("decryption failed", statusCode: 400);
        }
        if (payload is null) return Results.BadRequest();

        await ingest.IngestAsync(id, payload);
        await db.Devices.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastBatchSeq, batch.BatchSeq));
        return Results.Ok(new UploadAck { BatchSeq = batch.BatchSeq, Accepted = true });
    }

    private static async Task<IResult> GetPolicy(ClaimsPrincipal user, AppDb db, PolicyService policies)
    {
        var device = await db.Devices.AsNoTracking().FirstAsync(d => d.Id == DeviceId(user));
        return Results.Ok(await policies.SignedForAsync(device));
    }

    private static async Task<IResult> CommandResultEndpoint(Guid id, CommandResult result, ClaimsPrincipal user, AppDb db)
    {
        var cmd = await db.Commands.FirstOrDefaultAsync(c => c.Id == id && c.DeviceId == DeviceId(user));
        if (cmd is null) return Results.NotFound();
        cmd.Status = result.Success ? "succeeded" : "failed";
        cmd.Result = result.Message?.Length > 500 ? result.Message[..500] : result.Message;
        cmd.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok();
    }

    private static async Task<IResult> DownloadRelease(Guid id, AppDb db, IConfiguration config)
    {
        var r = await db.Releases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return Results.NotFound();
        var path = Path.Combine(config["Storage:DataDir"] ?? "data", "releases", r.FileName);
        return File.Exists(path) ? Results.File(path, "application/zip", r.FileName) : Results.NotFound();
    }
}
