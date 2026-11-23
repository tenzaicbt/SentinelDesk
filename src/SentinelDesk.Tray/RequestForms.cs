using System.IO.Pipes;
using System.Text;
using SentinelDesk.Shared;

namespace SentinelDesk.Tray;

/// <summary>One request to the agent over the pipe, waiting for its reply (used by the small tray windows).</summary>
public static class AgentRequest
{
    public static async Task<PipeMessage?> SendAsync(PipeMessage msg, string replyType, TimeSpan timeout)
    {
        using var pipe = new NamedPipeClientStream(".", PipeNames.Agent, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(msg) + "\n");
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) return null;
                try { if (Json.Deserialize<PipeMessage>(line) is { } m && m.Type == replyType) return m; } catch { }
            }
        }
        catch (OperationCanceledException) { return null; }
    }

    public static string User => $"{Environment.UserDomainName}\\{Environment.UserName}";
}

/// <summary>Base for the small tray windows: a title text, fields, a result line and two buttons.</summary>
public abstract class SmallForm : Form
{
    protected readonly Label Result = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 44 };
    protected readonly Button Go = new() { AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
    private readonly Button _close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
    protected readonly TableLayoutPanel Grid = new() { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };

    protected SmallForm(string title, string intro, string goText)
    {
        Text = $"SentinelDesk — {title}";
        Icon = Icons.App;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        TopMost = true;
        Go.Text = goText;
        AcceptButton = Go;
        CancelButton = _close;
        Grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(Go);
        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(420, 0), Text = intro });
        root.Controls.Add(Grid);
        root.Controls.Add(Result);
        root.Controls.Add(buttons);
        Controls.Add(root);
        Go.Click += async (_, _) => await SubmitAsync();
    }

    protected void Row(string label, Control field)
    {
        var r = Grid.RowCount++;
        Grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, r);
        field.Dock = DockStyle.Fill;
        Grid.Controls.Add(field, 1, r);
    }

    protected void Show(string text, bool error)
    {
        Result.ForeColor = error ? Color.Firebrick : Color.ForestGreen;
        Result.Text = text;
    }

    protected abstract Task SubmitAsync();

    protected async Task<PipeMessage?> AskAgentAsync(PipeMessage msg, string replyType)
    {
        Go.Enabled = false;
        try { return await AgentRequest.SendAsync(msg, replyType, TimeSpan.FromSeconds(30)); }
        catch { Show("The SentinelDesk agent is not reachable. Restart the computer and try again.", true); return null; }
        finally { Go.Enabled = true; }
    }
}

/// <summary>"Request access": the user asks an administrator to allow a blocked website, program or USB drive.</summary>
public sealed class RequestAccessForm : SmallForm
{
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _value = new();
    private readonly TextBox _reason = new() { Multiline = true, Height = 60 };
    private static readonly (string Kind, string Label)[] Kinds = [("websites", "Website"), ("apps", "Application"), ("usb", "USB drive")];

    public RequestAccessForm(string? kind = null, string? value = null)
        : base("Request access", "Ask your administrator to allow a website, application or USB drive that is blocked. " +
                                 "They will see your request in the console and you will get a message with the answer.", "Send request")
    {
        foreach (var k in Kinds) _kind.Items.Add(k.Label);
        _kind.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.Kind == kind));
        _value.Text = value ?? "";
        _value.PlaceholderText = "e.g. youtube.com or steam.exe";
        _reason.PlaceholderText = "Why do you need it for your work?";
        Row("What", _kind);
        Row("Website or program", _value);
        Row("Reason", _reason);
        Shown += (_, _) => (string.IsNullOrEmpty(value) ? _value : _reason).Focus();
    }

    protected override async Task SubmitAsync()
    {
        if (string.IsNullOrWhiteSpace(_value.Text)) { Show("Enter the website, program or drive you need.", true); _value.Focus(); return; }
        var reply = await AskAgentAsync(new PipeMessage
        {
            Type = "accessRequest", Title = Kinds[_kind.SelectedIndex].Kind, CurrentApp = _value.Text.Trim(), Body = _reason.Text.Trim(), User = AgentRequest.User,
        }, "accessRequestResult");
        if (reply is null) return;
        Show(reply.StatusText ?? "Request sent.", reply.State != "ok");
        if (reply.State == "ok") { _value.Enabled = _reason.Enabled = _kind.Enabled = false; Go.Visible = false; }
    }
}

/// <summary>"Enter unlock code": a code from the administrator pauses blocking on this computer, also without a connection.</summary>
public sealed class UnlockCodeForm : SmallForm
{
    private readonly TextBox _code = new() { CharacterCasing = CharacterCasing.Upper, PlaceholderText = "XXXXX-XXXXX", MaxLength = 20 };

    public UnlockCodeForm()
        : base("Enter unlock code", "Your administrator can give you a one-time code (for example by phone) that pauses blocking on " +
                                    "this computer for a few hours. It works even when the computer is offline.", "Unlock")
    {
        Row("Unlock code", _code);
        Shown += (_, _) => _code.Focus();
    }

    protected override async Task SubmitAsync()
    {
        if (UnlockCodes.Normalize(_code.Text).Length != 10) { Show("The code has 10 letters and numbers, e.g. 7K2QD-9MX4T.", true); return; }
        var reply = await AskAgentAsync(new PipeMessage { Type = "unlock", Token = _code.Text, User = AgentRequest.User }, "unlockResult");
        if (reply is null) return;
        Show(reply.StatusText ?? "", reply.State != "ok");
        if (reply.State == "ok") { _code.Enabled = false; Go.Visible = false; }
    }
}
