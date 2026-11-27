using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SentinelDesk.Server.Web.Services;

namespace SentinelDesk.Server.Web.Ui;

/// <summary>
/// Base for console pages and panels: periodic refresh, live-event handlers that update the page, and file downloads.
/// Everything registered here is stopped when the component goes away.
/// </summary>
public abstract class ConsolePage : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime Browser { get; set; } = default!;

    private readonly CancellationTokenSource _life = new();
    private readonly List<Action> _unsubscribe = [];
    private readonly List<IAsyncDisposable> _owned = [];

    /// <summary>Runs <paramref name="load"/> every <paramref name="interval"/> while the component is shown, then re-renders.</summary>
    protected void Every(TimeSpan interval, Func<Task> load)
    {
        var ct = _life.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                    await InvokeAsync(async () =>
                    {
                        try { await load(); } catch (ApiException) { }
                        StateHasChanged();
                    });
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    /// <summary>Applies a change from a background event (hub message, timer) on the component's thread and re-renders.</summary>
    protected void Ui(Action change) => _ = InvokeAsync(() =>
    {
        if (_life.IsCancellationRequested) return;
        change();
        StateHasChanged();
    });

    protected void UiAsync(Func<Task> change) => _ = InvokeAsync(async () =>
    {
        if (_life.IsCancellationRequested) return;
        try { await change(); } catch (ApiException) { }
        StateHasChanged();
    });

    /// <summary>Registers cleanup (e.g. an event unsubscription) for when the component is disposed.</summary>
    protected void OnDispose(Action unsubscribe) => _unsubscribe.Add(unsubscribe);

    protected void Own(IAsyncDisposable d) => _owned.Add(d);

    protected CancellationToken Lifetime => _life.Token;

    /// <summary>Hands the browser a file to save.</summary>
    protected async Task SaveFileAsync(string name, string contentType, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reference = new DotNetStreamReference(stream);
        await Browser.InvokeVoidAsync("sd.download", name, contentType, reference);
    }

    protected async Task<bool> CopyAsync(string text)
    {
        try { return await Browser.InvokeAsync<bool>("sd.copy", text); } catch (JSException) { return false; }
    }

    public virtual async ValueTask DisposeAsync()
    {
        _life.Cancel();
        foreach (var u in _unsubscribe) u();
        foreach (var d in _owned) { try { await d.DisposeAsync(); } catch { /* circuit closing */ } }
        GC.SuppressFinalize(this);
    }
}
