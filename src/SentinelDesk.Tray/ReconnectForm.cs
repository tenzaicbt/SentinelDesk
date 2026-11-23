using System.IO.Pipes;
using System.Text;
using SentinelDesk.Shared;

namespace SentinelDesk.Tray;

/// <summary>
/// "Reconnect to server": an administrator pastes a new enrollment token (from Console → Agent management) to reconnect a
/// computer that lost its connection or was removed in the console. Runs elevated (the tray relaunches itself with UAC);
/// the agent only accepts the request from an elevated administrator.
/// </summary>
public sealed class ReconnectForm : Form
{
    private readonly TextBox _server = new() { Dock = DockStyle.Fill };
    private readonly TextBox _token = new() { Dock = DockStyle.Fill, PlaceholderText = "SD-XXXX-XXXX" };
    private readonly CheckBox _trust = new() { AutoSize = true, Text = "Trust this server's certificate (only for a new server address)", Visible = false };
    private readonly Label _result = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 48 };
    private readonly Button _go = new() { Text = "Reconnect", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
    private readonly Button _close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private string? _currentServer;

    public ReconnectForm()
    {
        Text = "SentinelDesk — Reconnect to server";
        Icon = Icons.App;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        AcceptButton = _go;
        CancelButton = _close;

        var intro = new Label
        {
            AutoSize = true, MaximumSize = new Size(440, 0),
            Text = "Use this when the computer cannot connect to the management server, for example after it was removed in the console.\n\n" +
                   "1. In the console, open Agent management and create a new token.\n2. Copy it and paste it below.",
        };
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        grid.Controls.Add(new Label { Text = "Server address", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        grid.Controls.Add(_server, 1, 0);
        grid.Controls.Add(new Label { Text = "Enrollment token", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 1);
        grid.Controls.Add(_token, 1, 1);
        grid.Controls.Add(_trust, 1, 2);
        grid.Controls.Add(_result, 0, 3);
        grid.SetColumnSpan(_result, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_go);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(intro);
        root.Controls.Add(grid);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _server.TextChanged += (_, _) => _trust.Visible = _currentServer is not null && !string.Equals(_server.Text.Trim().TrimEnd('/'), _currentServer, StringComparison.OrdinalIgnoreCase);
        _go.Click += async (_, _) => await ReconnectAsync();
        Shown += async (_, _) => await ConnectAsync();
        FormClosed += (_, _) => { _reader?.Dispose(); _pipe?.Dispose(); };
    }

    private void Show(string text, bool error)
    {
        _result.ForeColor = error ? Color.Firebrick : Color.ForestGreen;
        _result.Text = text;
    }

    /// <summary>Opens the agent pipe and reads the current server address from the agent's status message.</summary>
    private async Task ConnectAsync()
    {
        try
        {
            _pipe = new NamedPipeClientStream(".", PipeNames.Agent, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(3000);
            _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
            var status = await ReadAsync(m => m.Type == "status", TimeSpan.FromSeconds(5));
            _currentServer = status?.Server?.TrimEnd('/');
            _server.Text = _currentServer ?? "";
            _token.Focus();
        }
        catch
        {
            Show("The SentinelDesk agent service is not running on this computer. Restart the computer and try again.", true);
            _go.Enabled = false;
        }
    }

    private async Task ReconnectAsync()
    {
        if (_pipe is not { IsConnected: true }) { await ConnectAsync(); if (_pipe is not { IsConnected: true }) return; }
        if (string.IsNullOrWhiteSpace(_token.Text)) { Show("Paste the enrollment token first.", true); _token.Focus(); return; }
        _go.Enabled = false;
        Show("Reconnecting…", false);
        try
        {
            var msg = new PipeMessage
            {
                Type = "reenroll", Token = _token.Text.Trim(), Server = _server.Text.Trim(), TrustServer = _trust.Visible && _trust.Checked,
                User = $"{Environment.UserDomainName}\\{Environment.UserName}",
            };
            var bytes = Encoding.UTF8.GetBytes(Json.Serialize(msg) + "\n");
            await _pipe!.WriteAsync(bytes);
            await _pipe.FlushAsync();
            var reply = await ReadAsync(m => m.Type == "reenrollResult", TimeSpan.FromSeconds(90));
            if (reply is null) Show("No answer from the agent. Check the server address and try again.", true);
            else
            {
                var ok = reply.State == "ok";
                Show(reply.StatusText ?? (ok ? "Reconnected." : "Reconnect failed."), !ok);
                if (ok) { _token.Clear(); _currentServer = reply.Server; }
            }
        }
        catch (Exception ex) { Show($"Reconnect failed: {ex.Message}", true); }
        finally { _go.Enabled = true; }
    }

    private async Task<PipeMessage?> ReadAsync(Func<PipeMessage, bool> match, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (_reader is not null)
            {
                var line = await _reader.ReadLineAsync(cts.Token);
                if (line is null) return null;
                try { if (Json.Deserialize<PipeMessage>(line) is { } m && match(m)) return m; } catch { }
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }
}
