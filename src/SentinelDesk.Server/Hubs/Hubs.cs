using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Server.Security;
using SentinelDesk.Server.Services;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Hubs;

/// <summary>Real-time channel for the web console.</summary>
[Authorize(Policy = "Console")]
public sealed class ConsoleHub(AccessScope scope) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "console");
        await base.OnConnectedAsync();
    }

    public async Task WatchDevice(Guid deviceId)
    {
        // HR viewers are reports-only: no live per-device activity (window titles etc.)
        if (scope.Role != Roles.HrViewer && await scope.CanAccessDeviceAsync(deviceId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"device:{deviceId}");
    }

    public Task UnwatchDevice(Guid deviceId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"device:{deviceId}");

    public async Task WatchLive()
    {
        if (scope.Role == Roles.HrViewer) return;
        var units = await scope.UnitsAsync();
        if (units is null) await Groups.AddToGroupAsync(Context.ConnectionId, "live:all");
        else foreach (var u in units) await Groups.AddToGroupAsync(Context.ConnectionId, $"live:{u}");
    }

    public async Task UnwatchLive()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "live:all");
        var units = await scope.UnitsAsync();
        if (units is not null) foreach (var u in units) await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"live:{u}");
    }
}

/// <summary>Live command channel for agents (server → agent push, agent → server acks).</summary>
[Authorize(AuthenticationSchemes = DeviceAuth.Scheme)]
public sealed class AgentHub(PresenceStore presence, AppDb db, IHubContext<ConsoleHub> console, ILogger<AgentHub> log) : Hub
{
    private Guid DeviceId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public override async Task OnConnectedAsync()
    {
        presence.AgentConnected(DeviceId, Context.ConnectionId);
        log.LogInformation("Agent {Device} connected to live channel", DeviceId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.AgentDisconnected(DeviceId, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task Ack(CommandResult result)
    {
        var cmd = await db.Commands.FirstOrDefaultAsync(c => c.Id == result.CommandId && c.DeviceId == DeviceId);
        if (cmd is null) return;
        cmd.Status = result.Success ? "succeeded" : "failed";
        cmd.Result = result.Message?.Length > 500 ? result.Message[..500] : result.Message;
        cmd.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await console.Clients.Group($"device:{DeviceId}").SendAsync("commandStatus", new { cmd.Id, cmd.Type, cmd.Status, cmd.Result });
    }
}
