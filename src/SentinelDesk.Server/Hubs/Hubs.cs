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
public sealed class ConsoleHub(AccessScope scope, LiveStreamRegistry streams, LiveStreamControl streamControl) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "console");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var d in streams.DropConnection(Context.ConnectionId)) await streamControl.StopAsync(d);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task WatchDevice(Guid deviceId)
    {
        // HR viewers are reports-only: no live per-device activity (window titles etc.)
        if (scope.Role != Roles.HrViewer && await scope.CanAccessDeviceAsync(deviceId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"device:{deviceId}");
            // first viewer: ask the agent to stream sub-second samples over its live channel
            if (streams.Watch(Context.ConnectionId, deviceId)) await streamControl.StartAsync(deviceId);
        }
    }

    public async Task UnwatchDevice(Guid deviceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"device:{deviceId}");
        if (streams.Unwatch(Context.ConnectionId, deviceId)) await streamControl.StopAsync(deviceId);
    }

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
public sealed class AgentHub(PresenceStore presence, AppDb db, IHubContext<ConsoleHub> console, LiveStreamRegistry streams,
    ILogger<AgentHub> log) : Hub
{
    private Guid DeviceId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public override async Task OnConnectedAsync()
    {
        presence.AgentConnected(DeviceId, Context.ConnectionId);
        log.LogInformation("Agent {Device} connected to live channel", DeviceId);
        // (re)connected while someone is watching: resume the live stream straight away
        if (streams.Enabled && streams.IsWatched(DeviceId))
            await Clients.Caller.SendAsync("startStream", streams.IntervalMs, LiveStreamRegistry.LeaseSec);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// High-rate sample while the device is being watched. Hot path: no database, just refresh the in-memory
    /// presence and relay to the device's viewers.
    /// </summary>
    public Task Stream(LiveSample s)
    {
        var id = DeviceId;
        var now = DateTime.UtcNow;
        if (presence.Get(id) is { } p)
            presence.Set(p with
            {
                LastSeen = now, Cpu = Math.Round(Math.Clamp(s.Cpu, 0, 100), 1), Ram = Math.Round(Math.Clamp(s.Ram, 0, 100), 1),
                NetInBps = Math.Max(0, s.NetInBps), NetOutBps = Math.Max(0, s.NetOutBps),
                CurrentApp = s.CurrentApp, CurrentTitle = s.CurrentTitle, Idle = s.UserIdle, Locked = s.Locked,
            });
        return console.Clients.Group($"device:{id}").SendAsync("liveSample", new
        {
            deviceId = id, s.Seq, s.AgentTimeMs, serverTimeMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(), s.IntervalMs,
            cpu = Math.Round(s.Cpu, 1), ram = Math.Round(s.Ram, 1), netInBps = Math.Max(0, s.NetInBps), netOutBps = Math.Max(0, s.NetOutBps),
            currentApp = s.CurrentApp?.Length > 260 ? s.CurrentApp[..260] : s.CurrentApp,
            currentTitle = s.CurrentTitle?.Length > 300 ? s.CurrentTitle[..300] : s.CurrentTitle,
            idle = s.UserIdle, s.Locked,
        });
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
