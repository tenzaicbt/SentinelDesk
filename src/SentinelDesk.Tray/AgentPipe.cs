using System.IO.Pipes;
using System.Text;
using SentinelDesk.Shared;

namespace SentinelDesk.Tray;

/// <summary>Reconnecting named-pipe client to the agent service.</summary>
public sealed class AgentPipe : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _write = new(1);

    public event Action<PipeMessage>? Message;
    public event Action<bool>? ConnectionChanged;
    public bool Connected => _pipe?.IsConnected == true;

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", PipeNames.Agent, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(5000, _cts.Token);
                ConnectionChanged?.Invoke(true);
                await SendAsync(new PipeMessage { Type = "hello", User = $"{Environment.UserDomainName}\\{Environment.UserName}", SessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId });
                using var reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
                while (!_cts.IsCancellationRequested && _pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) break;
                    try { if (Json.Deserialize<PipeMessage>(line) is { } m) Message?.Invoke(m); } catch { }
                }
            }
            catch (Exception) when (!_cts.IsCancellationRequested) { }
            finally
            {
                _pipe?.Dispose();
                _pipe = null;
                ConnectionChanged?.Invoke(false);
            }
            try { await Task.Delay(3000, _cts.Token); } catch { }
        }
    }

    public async Task SendAsync(PipeMessage msg)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) return;
        await _write.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Serialize(msg) + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();
        }
        catch { }
        finally { _write.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipe?.Dispose();
    }
}
