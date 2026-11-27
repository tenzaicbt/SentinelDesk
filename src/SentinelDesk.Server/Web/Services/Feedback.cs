namespace SentinelDesk.Server.Web.Services;

public enum ToastKind { Success, Error, Warning, Info }

public sealed class Toast
{
    public long Id { get; init; }
    public ToastKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
}

/// <summary>Small notifications in the bottom-right corner (replaces window alerts).</summary>
public sealed class Toasts
{
    private long _next;
    private readonly List<Toast> _items = [];
    public IReadOnlyList<Toast> Items { get { lock (_items) return [.. _items]; } }
    public event Action? Changed;

    public void Show(ToastKind kind, string title, string? description = null, string? actionLabel = null, Action? action = null)
    {
        var t = new Toast { Id = Interlocked.Increment(ref _next), Kind = kind, Title = title, Description = description, ActionLabel = actionLabel, Action = action };
        lock (_items) { _items.Add(t); if (_items.Count > 5) _items.RemoveAt(0); }
        Changed?.Invoke();
        _ = Task.Delay(kind == ToastKind.Error ? 8000 : 5000).ContinueWith(_ => Dismiss(t.Id));
    }

    public void Success(string title, string? description = null) => Show(ToastKind.Success, title, description);
    public void Error(string title, string? description = null) => Show(ToastKind.Error, title, description);
    public void Warning(string title, string? description = null) => Show(ToastKind.Warning, title, description);
    public void Info(string title, string? description = null) => Show(ToastKind.Info, title, description);

    /// <summary>The API's message for an <see cref="ApiException"/>, otherwise the fallback.</summary>
    public void Fail(Exception e, string fallback = "Failed") => Error(e is ApiException a ? a.Message : fallback);

    public void Dismiss(long id)
    {
        bool removed;
        lock (_items) removed = _items.RemoveAll(t => t.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }
}

public sealed class ConfirmOptions
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    /// <summary>Highlighted note under the message (e.g. "device is offline, runs on next check-in").</summary>
    public string? Note { get; init; }
    public string NoteTone { get; init; } = "warning";
    public string? ConfirmLabel { get; init; }
    public string CancelLabel { get; init; } = "Cancel";
    /// <summary>Destructive action: red button and warning icon.</summary>
    public bool Danger { get; init; }
}

public sealed class PromptOptions
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public required string Label { get; init; }
    public string? DefaultValue { get; init; }
    public string? Placeholder { get; init; }
    public string ConfirmLabel { get; init; } = "OK";
    public string CancelLabel { get; init; } = "Cancel";
    public string? InputMode { get; init; }
    public string? AutoComplete { get; init; }
    public int? MaxLength { get; init; }
    /// <summary>Return an error message to block submit, or null when valid.</summary>
    public Func<string, string?>? Validate { get; init; }
}

/// <summary>In-app replacements for confirm() / prompt(), styled like the console.</summary>
public sealed class Dialogs
{
    public sealed class Request
    {
        public ConfirmOptions? Confirm { get; init; }
        public PromptOptions? Prompt { get; init; }
        public TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Queue<Request> _queue = new();
    public Request? Current { get { lock (_queue) return _queue.TryPeek(out var r) ? r : null; } }
    public event Action? Changed;

    public async Task<bool> ConfirmAsync(ConfirmOptions opts) => await Enqueue(new Request { Confirm = opts }) is not null;
    public Task<string?> PromptAsync(PromptOptions opts) => Enqueue(new Request { Prompt = opts });

    private Task<string?> Enqueue(Request r)
    {
        lock (_queue) _queue.Enqueue(r);
        Changed?.Invoke();
        return r.Result.Task;
    }

    /// <summary>value: null = cancelled; any string = confirmed / the entered text.</summary>
    public void Close(string? value)
    {
        Request? r;
        lock (_queue) _queue.TryDequeue(out r);
        r?.Result.TrySetResult(value);
        Changed?.Invoke();
    }
}
