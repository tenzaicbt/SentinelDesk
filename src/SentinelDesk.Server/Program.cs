using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Endpoints;
using SentinelDesk.Server.Hubs;
using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;
using SentinelDesk.Shared;
using SentinelDesk.Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "SentinelDeskServer");

var configuredDir = builder.Configuration["Storage:DataDir"];
var dataDir = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDir) ? Path.Combine(AppContext.BaseDirectory, "data") : configuredDir);
Directory.CreateDirectory(dataDir);
builder.Configuration["Storage:DataDir"] = dataDir;

// ── Local recovery: `SentinelDesk.Server.exe reset-admin [username]`
//    Resets the password to Bootstrap:AdminPassword, unlocks the account and clears MFA. Requires access to the
//    server's data folder, so it is only usable by someone who already administers the server machine.
if (args.FirstOrDefault()?.Equals("reset-admin", StringComparison.OrdinalIgnoreCase) == true)
{
    var name = (args.ElementAtOrDefault(1) ?? "admin").Trim().ToLowerInvariant();
    var opts = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(dataDir, "sentineldesk.db")}").Options;
    await using var rdb = new AppDb(opts);
    var u = await rdb.AdminUsers.FirstOrDefaultAsync(x => x.Username == name);
    if (u is null) { Console.Error.WriteLine($"User '{name}' not found in {dataDir}"); return; }
    var pwd = builder.Configuration["Bootstrap:AdminPassword"] ?? "ChangeMe@2026";
    u.PasswordHash = Passwords.Hash(u, pwd);
    u.MustChangePassword = true;
    u.FailedLogins = 0;
    u.LockedUntil = null;
    u.Disabled = false;
    u.MfaEnabled = false;
    u.TotpSecret = null;
    rdb.Audit.Add(new AuditEntry { Actor = "local-console", Action = "user.resetAdmin", Target = u.Username, Details = Environment.UserName });
    await rdb.SaveChangesAsync();
    Console.WriteLine($"'{u.Username}' reset: unlocked, MFA cleared, password set to the bootstrap password (change required at next sign-in).");
    return;
}

// ── Kestrel: TLS 1.2+/1.3 with optional client certificates (validated against the device CA in DeviceAuthHandler)
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
    k.ConfigureHttpsDefaults(https =>
    {
        https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.AllowAnyClientCertificate();
    });
});

var keys = new ServerKeys(dataDir);
builder.Services.AddSingleton(keys);

builder.Services.AddDbContext<AppDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? $"Data Source={Path.Combine(dataDir, "sentineldesk.db")};Cache=Shared"));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 256 * 1024)
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddRequestTimeouts();

// ── Web console: Blazor Server components (SentinelDesk.Server.Web) that call this server's own API
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var dpKeys = builder.Services.AddDataProtection().SetApplicationName("SentinelDesk.Console")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys", "console")));
if (OperatingSystem.IsWindows()) dpKeys.ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddSingleton<LoopbackAddress>();
builder.Services.AddHttpClient("console-api").ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<LoopbackAddress>().CreateHandler());
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<Fmt>();
builder.Services.AddScoped<Session>();
builder.Services.AddScoped<LiveHub>();
builder.Services.AddScoped<Toasts>();
builder.Services.AddScoped<Dialogs>();

// ── Services
builder.Services.AddSingleton<PresenceStore>();
builder.Services.AddSingleton<DeviceCache>();
builder.Services.AddSingleton<DeviceCacheInvalidator>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<LiveStreamRegistry>();
builder.Services.AddSingleton<LiveStreamControl>();
builder.Services.AddScoped<HeartbeatService>();
builder.Services.AddScoped<IngestService>();
builder.Services.AddScoped<PolicyService>();
builder.Services.AddScoped<CommandService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<AccessScope>();
builder.Services.AddHostedService<AlertEngine>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHostedService<DemoSimulator>();
builder.Services.AddHostedService<LiveStreamRenewer>();

// ── Authentication: JWT for admins (console), HMAC + mTLS for devices
var jwt = new JwtService(keys, builder.Configuration);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = JwtService.Issuer, ValidAudience = JwtService.Issuer, IssuerSigningKey = jwt.Key,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = System.Security.Claims.ClaimTypes.Name, RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/hubs/console") && ctx.Request.Query.TryGetValue("access_token", out var t))
                    ctx.Token = t;
                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, DeviceAuthHandler>(DeviceAuth.Scheme, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Console", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireRole(Roles.All))
    .AddPolicy("Write", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireRole(Roles.SuperAdmin, Roles.BranchAdmin))
    .AddPolicy("Super", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireRole(Roles.SuperAdmin))
    .AddPolicy("Audit", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireRole(Roles.SuperAdmin, Roles.Auditor))
    .AddPolicy("MfaPending", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireRole("MfaPending"))
    .AddPolicy("Device", p => p.AddAuthenticationSchemes(DeviceAuth.Scheme).RequireAuthenticatedUser());

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "-",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("agent", ctx => RateLimitPartition.GetTokenBucketLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "-",
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 600, TokensPerPeriod = 300, ReplenishmentPeriod = TimeSpan.FromSeconds(10), QueueLimit = 0 }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await Seeder.SeedAsync(db, keys, app.Configuration, app.Logger);
}

app.UseForwardedHeaders();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data: https://server.arcgisonline.com https://tile.openstreetmap.org; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; " +
        "connect-src 'self' ws: wss:; frame-ancestors 'none'";
    await next();
});
if (!app.Environment.IsDevelopment()) app.UseHsts();

app.UseStaticFiles();
app.UseRateLimiter();
app.UseRequestTimeouts();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));
app.MapAgentApi();
app.MapAuthApi();
app.MapDashboardApi();
app.MapDeviceApi();
app.MapManagementApi();
app.MapBlockingApi();
app.MapReportApi();
app.MapHub<ConsoleHub>("/hubs/console");
app.MapHub<AgentHub>("/hubs/agent");

// web console (Blazor Server)
app.MapStaticAssets();
app.MapRazorComponents<SentinelDesk.Server.Web.App>().AddInteractiveServerRenderMode();

app.MapFallback(ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/agent") || ctx.Request.Path.StartsWithSegments("/hubs")
        || ctx.Request.Path.StartsWithSegments("/_framework") || Path.HasExtension(ctx.Request.Path.Value))
        ctx.Response.StatusCode = 404;
    else
        ctx.Response.Redirect("/");   // unknown console address: back to the overview
    return Task.CompletedTask;
});

app.Run();
