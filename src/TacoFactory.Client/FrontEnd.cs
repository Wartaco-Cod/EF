using Silk.NET.Input;
using TacoFactory.Client.Rendering;
using TacoFactory.Core.Sim;
using TacoFactory.Net;
using Color = TacoFactory.Client.Rendering.Color;

namespace TacoFactory.Client;

/// <summary>
/// The screens before the world: title and login (or new account with a purchase key), then character
/// select and creation. Talks to the server through <see cref="ClientConnection"/>'s login calls.
/// </summary>
public sealed partial class GameClient
{
    private enum Front { Login, Register, Characters, Create }

    private Front _front = Front.Login;
    private readonly Field _serverField = new() { Id = "server", Max = 60 };
    private readonly Field _accountField = new() { Id = "account", Max = 24 };
    private readonly Field _passwordField = new() { Id = "password", Max = 64, Secret = true };
    private readonly Field _password2Field = new() { Id = "password2", Max = 64, Secret = true };
    private readonly Field _keyField = new() { Id = "key", Max = 24 };
    private readonly Field _newNameField = new() { Id = "newname", Max = 16 };
    private ClassId _newClass = ClassId.Knight;
    private string? _selectedCharacter;
    private string? _confirmDelete;
    private string _frontMessage = "";
    private Action? _whenHello;     // runs once the server's hello arrives (login waits for the key exchange)
    private bool _frontInit;
    private AccountView? _lastAccount;

    private static readonly Dictionary<ClassId, string> ClassBlurb = new()
    {
        [ClassId.Knight] = "Heavy armour and the longest reach with sword and maul. Holds the line; the hardest to kill.",
        [ClassId.Paladin] = "Sword, shield and the Liturgy. Fights at the front and keeps the party standing.",
        [ClassId.Wizard] = "The fragile scholar of the Order: Alchemy and Hermetica burn whole packs from range.",
        [ClassId.Healer] = "Infirmarian of the Temple: heals, wards and raises the fallen; never far from the fight.",
    };

    private Field[] FrontFields => _front switch
    {
        Front.Login => [_serverField, _accountField, _passwordField],
        Front.Register => [_serverField, _accountField, _passwordField, _password2Field, _keyField],
        Front.Create => [_newNameField],
        _ => [],
    };

    private void FrontInit()
    {
        if (_frontInit) return;
        _frontInit = true;
        if (_serverField.Text.Length == 0) _serverField.Text = _settings.LastServer;
        _accountField.Text = _settings.LastAccount;
        _focus = _accountField.Text.Length == 0 ? _accountField : _passwordField;
        _passwordField.OnEnter = _password2Field.OnEnter = _keyField.OnEnter = () => { if (_front == Front.Login) DoLogin(); else DoRegister(); };
        _newNameField.OnEnter = DoCreate;
    }

    // ------------------------------------------------------------------ actions

    private (string Host, int Port) ParseServer()
    {
        string s = _serverField.Text.Trim();
        if (s.Length == 0) s = "127.0.0.1";
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out var port)) return (s[..colon], port);
        return (s, Protocol.DefaultPort);
    }

    /// <summary>Opens a fresh connection to the server in the box and runs the request once it says hello.</summary>
    private void ConnectThen(Action request)
    {
        var (host, port) = ParseServer();
        if (_conn.Connected && _conn.Hello is not null && !_conn.Failed && _connectedTo == (host, port)) { request(); return; }
        _conn.Dispose();
        _conn = new ClientConnection { Known = new KnownServers(KnownServers.DefaultPath) };
        _conn.Connect(host, port);
        _connectedTo = (host, port);
        _whenHello = request;
        _frontMessage = $"Contacting {host}...";
    }

    private (string, int) _connectedTo;

    private void DoLogin()
    {
        if (_accountField.Text.Trim().Length == 0 || _passwordField.Text.Length == 0) { _frontMessage = "Enter your account name and password."; return; }
        string acc = _accountField.Text, pw = _passwordField.Text;
        ConnectThen(() => { _conn.Login(acc, pw); _frontMessage = "Signing in..."; });
    }

    private void DoRegister()
    {
        if (_passwordField.Text != _password2Field.Text) { _frontMessage = "The two passwords do not match."; return; }
        string acc = _accountField.Text, pw = _passwordField.Text, key = _keyField.Text;
        ConnectThen(() => { _conn.Register(acc, pw, key); _frontMessage = "Creating your account..."; });
    }

    private void DoCreate()
    {
        _conn.CreateCharacter(_newNameField.Text, _newClass);
        _selectedCharacter = _newNameField.Text.Trim();
    }

    private void LogOutToTitle()
    {
        _conn.Dispose();
        _conn = new ClientConnection();
        _front = Front.Login;
        _passwordField.Text = "";
        _lastAccount = null;
        _frontMessage = "";
        _focus = _passwordField;
    }

    /// <summary>Front-end bookkeeping every frame: hello arrived, account answered, connection failed.</summary>
    private void FrontTick()
    {
        if (_whenHello is not null && _conn.Hello is not null)
        {
            var run = _whenHello;
            _whenHello = null;
            run();
        }
        if (_conn.Failed && _conn.RejectReason is string why && _front is Front.Login or Front.Register)
            _frontMessage = why;
        if (_conn.Failed && _front is Front.Characters or Front.Create)
        {
            string reason = _conn.RejectReason ?? "Disconnected.";
            LogOutToTitle();
            _frontMessage = reason;
        }
        if (!ReferenceEquals(_conn.Account, _lastAccount) && _conn.Account is { } acc)
        {
            _lastAccount = acc;
            if (!acc.Ok) { _frontMessage = acc.Message; return; }
            if (_front is Front.Login or Front.Register)
            {
                _settings.LastServer = _serverField.Text.Trim();
                _settings.LastAccount = acc.Account;
                _settings.Save(_settingsPath);
                _passwordField.Text = _password2Field.Text = _keyField.Text = "";
                _focus = null;
                _front = acc.Characters.Count == 0 ? Front.Create : Front.Characters;
            }
            else if (_front == Front.Create && acc.Characters.Any(c => string.Equals(c.Name, _selectedCharacter, StringComparison.OrdinalIgnoreCase)))
            {
                _front = Front.Characters;
                _newNameField.Text = "";
                _focus = null;
            }
            _frontMessage = acc.Message;
            if (_selectedCharacter is null || acc.Characters.All(c => !string.Equals(c.Name, _selectedCharacter, StringComparison.OrdinalIgnoreCase)))
                _selectedCharacter = acc.Characters.FirstOrDefault()?.Name;
            else _selectedCharacter = acc.Characters.First(c => string.Equals(c.Name, _selectedCharacter, StringComparison.OrdinalIgnoreCase)).Name;
        }
    }

    private void FrontKey(Key key)
    {
        if (key == Key.Escape)
        {
            if (_front == Front.Register) _front = Front.Login;
            else if (_front == Front.Create && _conn.Account?.Characters.Count > 0) _front = Front.Characters;
            return;
        }
        if (key == Key.Tab && FrontFields.Length > 0) { _focus = FrontFields[0]; return; }
        if (key is Key.Enter or Key.KeypadEnter && _front == Front.Characters && _selectedCharacter is string sel) _conn.EnterWorld(sel);
    }

    /// <summary>Tab moves to the next box on the login screens.</summary>
    private bool FrontTab()
    {
        var fields = FrontFields;
        int i = Array.IndexOf(fields, _focus);
        if (i < 0 || fields.Length == 0) return false;
        _focus = fields[(i + 1) % fields.Length];
        return true;
    }

    // ------------------------------------------------------------------ drawing

    private void DrawFrontEnd()
    {
        FrontInit();
        FrontTick();
        _buttons.Clear();
        _blockRects.Clear();
        var bg = _assets.Texture("ui/title.png");
        _batch.Draw(bg, 0, 0, VW, VH, Color.White);
        _batch.FillRect(0, 0, VW, VH, new Color(0, 0, 0, 90));
        _title.DrawCentered(_batch, "TEMPLAR KNIGHTS", VW / 2, 92, Color.FromHex("#E6C14A"));
        _ui.DrawCentered(_batch, "{#C9B88A}Paris, 1307. The Order is outlawed. You are what remains of it.{/}", VW / 2, 146, Color.White);
        switch (_front)
        {
            case Front.Login: DrawLogin(register: false); break;
            case Front.Register: DrawLogin(register: true); break;
            case Front.Characters: DrawCharacters(); break;
            case Front.Create: DrawCreate(); break;
        }
        if (_frontMessage.Length > 0)
            _ui.DrawCentered(_batch, _frontMessage.Replace('{', '('), VW / 2, 990, (_conn.Failed || _conn.Account is { Ok: false })
                ? Color.FromHex("#F08A70") : Color.FromHex("#E8E4DA"));
        _small.Draw(_batch, $"{{#6A6050}}{Core.EngineInfo.Name} {Core.EngineInfo.Version} · protocol {Protocol.Version}{{/}}", 20, VH - 34, Color.White);
        DrawFps();
        DrawPadFocus();
    }

    private void Label(string text, float x, float y) => _small.Draw(_batch, text, x, y + 4, Color.FromHex("#C9B88A"));

    private void DrawLogin(bool register)
    {
        float w = 620, h = register ? 560 : 430, x = (VW - w) / 2, y0 = 250;
        _batch.FillRect(x, y0, w, h, new Color(16, 13, 10, 235));
        _batch.FillRect(x, y0, w, 2, Color.FromHex("#C9A227"));
        _batch.FillRect(x, y0 + h - 2, w, 2, Color.FromHex("#C9A227"));
        _ui.Draw(_batch, register ? "New account" : "Sign in", x + 32, y0 + 22, Color.FromHex("#E6C14A"));
        float y = y0 + 80, lx = x + 32, fx = x + 210, fw = w - 242;
        Label("Server", lx, y); TextField(_serverField, fx, y, fw, "address, or address:port"); y += 52;
        Label("Account", lx, y); TextField(_accountField, fx, y, fw, "account name"); y += 52;
        Label("Password", lx, y); TextField(_passwordField, fx, y, fw, register ? $"at least 8 characters" : "password"); y += 52;
        if (register)
        {
            Label("Again", lx, y); TextField(_password2Field, fx, y, fw, "repeat the password"); y += 52;
            Label("Purchase key", lx, y); TextField(_keyField, fx, y, fw, "TK-XXXX-XXXX-XXXX-XXXX (not needed on open servers)"); y += 52;
        }
        y += 12;
        bool busy = _whenHello is not null;
        if (register)
        {
            Button(lx, y, 260, 44, "Create account", DoRegister, enabled: !busy);
            Button(x + w - 292, y, 260, 44, "Back to sign in", () => { _front = Front.Login; _frontMessage = ""; }, tone: "plain");
        }
        else
        {
            Button(lx, y, 260, 44, "Sign in", DoLogin, enabled: !busy);
            Button(x + w - 292, y, 260, 44, "New account", () => { _front = Front.Register; _frontMessage = ""; _focus = _accountField; }, tone: "plain");
            Button(lx, y + 62, w - 64, 36, "Leave", () => _window.Close(), tone: "red");
        }
    }

    private void DrawCharacters()
    {
        var acc = _conn.Account!;
        float x = 260, y0 = 220, w = 1400, h = 700;
        _batch.FillRect(x, y0, w, h, new Color(16, 13, 10, 235));
        _batch.FillRect(x, y0, w, 2, Color.FromHex("#C9A227"));
        _batch.FillRect(x, y0 + h - 2, w, 2, Color.FromHex("#C9A227"));
        _ui.Draw(_batch, $"Characters of {acc.Account}", x + 32, y0 + 22, Color.FromHex("#E6C14A"));
        _small.Draw(_batch, $"{{#7A7060}}{acc.Characters.Count}/{acc.MaxCharacters}{(acc.Admin ? " · server administrator" : "")}{{/}}", x + 32, y0 + 62, Color.White);
        float y = y0 + 100;
        foreach (var c in acc.Characters)
        {
            bool sel = c.Name == _selectedCharacter;
            _batch.FillRect(x + 24, y, 640, 64, sel ? new Color(74, 60, 38, 255) : new Color(34, 29, 22, 255));
            if (sel) _batch.FillRect(x + 24, y, 4, 64, Color.FromHex("#E6C14A"));
            string name = c.Name;
            _buttons.Add((x + 24, y, 640, 64, () => { if (_selectedCharacter == name) _conn.EnterWorld(name); _selectedCharacter = name; _confirmDelete = null; }));
            _ui.Draw(_batch, c.Name, x + 44, y + 6, Color.FromHex("#F4EFE3"));
            _small.Draw(_batch, $"{{#C9B88A}}Level {c.Level} {c.Class}{{/}} {{#7A7060}}· {c.Where}{{/}}", x + 44, y + 36, Color.White);
            y += 72;
        }
        // The selected knight, large, with its class.
        if (acc.Characters.FirstOrDefault(c => c.Name == _selectedCharacter) is { } chosen)
        {
            float px = x + 720, pw = w - 744;
            var sprite = _assets.Texture($"sprites/class_{chosen.Class.ToString().ToLowerInvariant()}.png");
            _batch.Draw(sprite, px + pw / 2 - 128, y0 + 110, 256, 256, Color.White);
            _title.DrawCentered(_batch, chosen.Name.ToUpperInvariant(), px + pw / 2, y0 + 390, Color.FromHex("#E6C14A"));
            _ui.DrawCentered(_batch, $"Level {chosen.Level} {chosen.Class}", px + pw / 2, y0 + 440, Color.FromHex("#E8E4DA"));
            _small.DrawCentered(_batch, $"{{#9A9080}}{ClassBlurb[chosen.Class]}{{/}}", px + pw / 2, y0 + 480, Color.White);
            string sel = chosen.Name;
            Button(px + pw / 2 - 160, y0 + 530, 320, 52, "Enter the world", () => _conn.EnterWorld(sel));
            if (_confirmDelete == sel)
                Button(px + pw / 2 - 160, y0 + 600, 320, 36, $"Really delete {sel}?", () => { _conn.DeleteCharacter(sel); _confirmDelete = null; }, tone: "red");
            else
                Button(px + pw / 2 - 160, y0 + 600, 320, 36, "Delete", () => _confirmDelete = sel, tone: "plain");
        }
        Button(x + 24, y0 + h - 64, 300, 40, "New character", () => { _front = Front.Create; _focus = _newNameField; _frontMessage = ""; },
            enabled: acc.Characters.Count < acc.MaxCharacters);
        Button(x + 340, y0 + h - 64, 200, 40, "Sign out", LogOutToTitle, tone: "plain");
    }

    private void DrawCreate()
    {
        var acc = _conn.Account;
        float x = 160, y0 = 210, w = 1600, h = 720;
        _batch.FillRect(x, y0, w, h, new Color(16, 13, 10, 235));
        _batch.FillRect(x, y0, w, 2, Color.FromHex("#C9A227"));
        _batch.FillRect(x, y0 + h - 2, w, 2, Color.FromHex("#C9A227"));
        _ui.Draw(_batch, "A new knight of the Temple", x + 32, y0 + 22, Color.FromHex("#E6C14A"));
        float cw = (w - 64 - 3 * 24) / 4;
        for (int i = 0; i < Classes.All.Length; i++)
        {
            var cls = Classes.All[i];
            var def = Classes.Get(cls);
            float cx = x + 32 + i * (cw + 24), cy = y0 + 80;
            bool sel = cls == _newClass;
            _batch.FillRect(cx, cy, cw, 470, sel ? new Color(74, 60, 38, 255) : new Color(34, 29, 22, 255));
            if (sel) { _batch.FillRect(cx, cy, cw, 3, Color.FromHex("#E6C14A")); _batch.FillRect(cx, cy + 467, cw, 3, Color.FromHex("#E6C14A")); }
            var c = cls;
            _buttons.Add((cx, cy, cw, 470, () => _newClass = c));
            _batch.Draw(_assets.Texture($"sprites/class_{cls.ToString().ToLowerInvariant()}.png"), cx + cw / 2 - 96, cy + 16, 192, 192, Color.White);
            _title.DrawCentered(_batch, def.Name.ToUpperInvariant(), cx + cw / 2, cy + 220, Color.FromHex(sel ? "#E6C14A" : "#C9B88A"));
            float ty = cy + 272;
            foreach (var line in Wrap(ClassBlurb[cls], cw - 40))
            {
                _small.DrawCentered(_batch, line, cx + cw / 2, ty, Color.FromHex("#D8D2C4"));
                ty += 26;
            }
            ty += 12;
            _small.DrawCentered(_batch, $"{{#9A9080}}Health x{def.HealthMul:0.##} · Mana x{def.ManaMul:0.##}{{/}}", cx + cw / 2, ty, Color.White);
            _small.DrawCentered(_batch, $"{{#9A9080}}Damage x{def.DamageMul:0.##} · Armour {def.DamageReduction:P0}{{/}}", cx + cw / 2, ty + 26, Color.White);
        }
        float y = y0 + 580;
        Label("Name", x + 32, y);
        TextField(_newNameField, x + 120, y, 400, "3-16 letters; spaces allowed");
        Button(x + 540, y - 6, 300, 44, $"Raise the {_newClass}", DoCreate);
        if (acc?.Characters.Count > 0) Button(x + w - 232, y - 6, 200, 44, "Back", () => { _front = Front.Characters; _focus = null; }, tone: "plain");
        else Button(x + w - 232, y - 6, 200, 44, "Sign out", LogOutToTitle, tone: "plain");
    }

    private IEnumerable<string> Wrap(string text, float width)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            string trial = line.Length == 0 ? word : line + " " + word;
            if (_small.Measure(trial) > width && line.Length > 0) { yield return line; line = word; }
            else line = trial;
        }
        if (line.Length > 0) yield return line;
    }
}
