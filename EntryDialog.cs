namespace SessionStartupManager;

internal sealed class EntryDialog : Form
{
    sealed record Choice<T>(T Value, string Text) { public override string ToString() => Text; }

    static List<string> _distros; // wsl -l is slow-ish; ask once per run

    readonly LaunchEntry _e;
    readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly TextBox _name = new() { Dock = DockStyle.Fill };
    readonly ComboBox _distro = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    readonly TextBox _dir = new() { Dock = DockStyle.Fill };
    readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly TextBox _session = new() { Dock = DockStyle.Fill };
    readonly TextBox _args = new() { Dock = DockStyle.Fill };
    readonly CheckBox _lock = new() { Text = "Lock window title (the program can't rename it; Windows Terminal only)", AutoSize = true };
    readonly CheckBox _enabled = new() { Text = "Launch with its group", AutoSize = true };
    readonly Label _argsLabel = Lbl("");
    readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
    readonly Button _pick = new() { Text = "Pick…", AutoSize = true };
    readonly Label _distroLabel = Lbl("Distro");
    readonly Label _modeLabel = Lbl("Session mode");
    readonly Label _sessionLabel = Lbl("Session id");
    readonly IEnumerable<LaunchGroup> _groups;
    bool _loading = true;

    public EntryDialog(LaunchEntry entry, IEnumerable<LaunchGroup> groups)
    {
        _e = entry;
        _groups = groups;
        Text = string.IsNullOrWhiteSpace(entry.Name) ? "New window" : "Edit " + entry.Name;
        Icon = AppIcon.Get();
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        foreach (EntryKind k in Enum.GetValues<EntryKind>()) _kind.Items.Add(new Choice<EntryKind>(k, LaunchEntry.KindText(k)));
        foreach (SessionMode m in Enum.GetValues<SessionMode>()) _mode.Items.Add(new Choice<SessionMode>(m, LaunchEntry.ModeText(m)));

        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => BrowseFolder();
        _pick.Click += (_, _) => PickSession();

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, Lbl("Type"), _kind, null);
        AddRow(grid, Lbl("Name / title"), _name, null);
        AddRow(grid, _distroLabel, _distro, null);
        AddRow(grid, Lbl("Folder"), _dir, browse);
        AddRow(grid, _modeLabel, _mode, null);
        AddRow(grid, _sessionLabel, _session, _pick);
        AddRow(grid, _argsLabel, _args, null);
        AddRow(grid, null, _lock, null);
        AddRow(grid, null, _enabled, null);
        AddRow(grid, null, _hint, null);

        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        int buttonRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(buttons, 0, buttonRow);
        grid.SetColumnSpan(buttons, 3);

        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
        Theme.Apply(this);

        _kind.SelectedIndex = (int)entry.Kind;
        _mode.SelectedIndex = (int)entry.SessionMode;
        _name.Text = entry.Name;
        _distro.Text = entry.Distro;
        _dir.Text = entry.WorkingDirectory;
        _session.Text = entry.SessionId;
        _args.Text = entry.Arguments;
        _lock.Checked = entry.LockTitle;
        _enabled.Checked = entry.Enabled;

        _kind.SelectedIndexChanged += (_, _) => KindChanged();
        _mode.SelectedIndexChanged += (_, _) => UpdateState();
        FormClosing += OnClosingSave;
        if (Kind == EntryKind.Wsl) FillDistros();
        UpdateState();
        _loading = false;
    }

    EntryKind Kind => ((Choice<EntryKind>)_kind.SelectedItem).Value;
    SessionMode Mode => ((Choice<SessionMode>)_mode.SelectedItem).Value;

    void KindChanged()
    {
        if (!_loading)
        {
            // A session ID belongs to its provider; never reuse it after switching type.
            _session.Clear();
        }
        if (!_loading && Kind == EntryKind.Wsl)
        {
            FillDistros();
            // Ubuntu's prompt rewrites the title on every command, so the configured name won't stick otherwise.
            _lock.Checked = true;
            if (string.IsNullOrWhiteSpace(_dir.Text)) _dir.Text = "~";
        }
        UpdateState();
    }

    void FillDistros()
    {
        _distros ??= Launcher.WslDistros();
        string keep = _distro.Text;
        _distro.Items.Clear();
        foreach (var d in _distros) _distro.Items.Add(d);
        _distro.Text = keep.Length > 0 ? keep
            : _distros.FirstOrDefault(d => d.StartsWith("Ubuntu", StringComparison.OrdinalIgnoreCase)) ?? _distros.FirstOrDefault() ?? "";
    }

    void UpdateState()
    {
        bool session = LaunchEntry.IsSession(Kind), wsl = Kind == EntryKind.Wsl;
        _distroLabel.Visible = _distro.Visible = wsl;
        _modeLabel.Visible = _mode.Visible = session;
        bool resume = session && Mode == SessionMode.Resume;
        _sessionLabel.Visible = _session.Visible = _pick.Visible = resume;
        _sessionLabel.Text = Kind == EntryKind.Agy ? "Conversation id" : "Session id / name";
        _session.PlaceholderText = Kind == EntryKind.Agy ? "required for resume; pick a recent workspace or paste an ID"
            : $"blank = show {LaunchEntry.KindText(Kind)}'s resume picker";
        _argsLabel.Text = session ? "Extra CLI args" : "Startup command";
        _args.PlaceholderText = Kind switch
        {
            EntryKind.Claude => "e.g. --model opus --permission-mode plan",
            EntryKind.Codex => "e.g. --profile work",
            EntryKind.Agy => "e.g. --mode plan",
            EntryKind.Cmd => "e.g. npm run dev   (cmd syntax)",
            EntryKind.Wsl => "e.g. docker compose up   (bash, then you get a shell)",
            _ => "e.g. npm run dev   (PowerShell syntax)",
        };
        _hint.Text = Kind switch
        {
            EntryKind.Wsl => "Folder can be a Linux path (~, /home/you/proj) or a Windows path (C:\\Users\\…, mounted under /mnt/c). The command runs in bash -i, then drops to an interactive shell.",
            EntryKind.Claude => Mode switch
            {
                SessionMode.Resume => "The window title comes from the session's /rename name (or Claude's auto title), read fresh on every launch. Claude keeps it updated while running unless you lock it.",
                SessionMode.Continue => "Runs claude --continue: picks up the most recent conversation in the folder. The title comes from that conversation's name.",
                _ => "Starts a fresh session with --name set to the name above.",
            },
            EntryKind.Codex => Mode switch
            {
                SessionMode.Resume => "Runs codex resume with the session ID or name. Leave it blank to choose in Codex. Pick lists locally saved Codex sessions.",
                SessionMode.Continue => "Runs codex resume --last: continues the most recent session in this folder.",
                _ => "Runs codex to start a fresh session. The name above sets the initial window title.",
            },
            EntryKind.Agy => Mode switch
            {
                SessionMode.Resume => "Runs agy --conversation with the ID above. Pick lists the latest cached conversation per workspace; paste an ID to resume an older one.",
                SessionMode.Continue => "Runs agy --continue: continues the latest conversation in this folder, or starts a new one if none is available.",
                _ => "Runs agy to start a fresh Antigravity session. The name above sets the initial window title.",
            },
            _ => "The window title is the name above. Leave the command blank for a plain shell in that folder.",
        };
    }

    void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Working folder" };
        if (Directory.Exists(_dir.Text)) dlg.InitialDirectory = _dir.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _dir.Text = dlg.SelectedPath;
    }

    void PickSession()
    {
        using var dlg = new SessionPickerDialog(false, _groups, Kind);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected.Count == 0) return;
        var s = dlg.Selected[0];
        _session.Text = s.Id;
        _dir.Text = s.Cwd;
        if (s.Title != null) _name.Text = s.Title;
    }

    void OnClosingSave(object sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK) return;

        if (Kind == EntryKind.Agy && Mode == SessionMode.Resume && string.IsNullOrWhiteSpace(_session.Text))
        {
            e.Cancel = true;
            _hint.Text = "Enter a conversation ID, pick a cached session, or choose Continue latest in folder.";
            _session.Focus();
            return;
        }

        string dir = _dir.Text.Trim();
        bool linuxPath = Kind == EntryKind.Wsl && (dir.StartsWith('/') || dir.StartsWith('~'));
        if (dir.Length > 0 && !linuxPath && !dir.StartsWith("~") && !Directory.Exists(Environment.ExpandEnvironmentVariables(dir)))
        {
            var r = MessageBox.Show(this, $"Folder doesn't exist:\n{dir}\n\nSave anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
        }

        _e.Kind = Kind;
        _e.SessionMode = Mode;
        _e.Name = _name.Text.Trim();
        _e.Distro = Kind == EntryKind.Wsl ? _distro.Text.Trim() : "";
        _e.WorkingDirectory = dir;
        _e.SessionId = _session.Text.Trim();
        _e.Arguments = _args.Text.Trim();
        _e.LockTitle = _lock.Checked;
        _e.Enabled = _enabled.Checked;
    }

    static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) };

    static void AddRow(TableLayoutPanel t, Control label, Control field, Control extra)
    {
        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label != null) t.Controls.Add(label, 0, row);
        t.Controls.Add(field, 1, row);
        if (extra != null) t.Controls.Add(extra, 2, row);
    }
}
