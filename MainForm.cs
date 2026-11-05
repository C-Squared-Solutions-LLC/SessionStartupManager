using System.Diagnostics;

namespace SessionStartupManager;

internal sealed class MainForm : Form
{
    sealed record Choice<T>(T Value, string Text) { public override string ToString() => Text; }

    readonly AppConfig _cfg;

    // Groups (left)
    readonly CheckedListBox _groups = new() { Dock = DockStyle.Fill, IntegralHeight = false, FormattingEnabled = true };

    // Selected group's settings
    readonly TextBox _groupName = new() { Width = 170, Margin = new Padding(3, 4, 12, 3) };
    readonly ComboBox _desktop = Combo(230);
    readonly ComboBox _monitor = Combo(290);
    readonly ComboBox _arrange = Combo(95);
    readonly CheckBox _groupTabs = Check("Tabs in one window");
    readonly CheckBox _groupStartup = Check("Run at Windows startup");

    // Entries of the selected group
    readonly DataGridView _grid = new();

    // Global settings
    readonly CheckBox _useWt = Check("Use Windows Terminal");
    readonly CheckBox _returnHome = Check("Return to starting desktop");
    readonly ComboBox _host = Combo(95);
    readonly NumericUpDown _delay = new() { Minimum = 0, Maximum = 10000, Increment = 100, Width = 65 };
    readonly NumericUpDown _startupDelay = new() { Minimum = 0, Maximum = 600, Width = 55 };

    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 4, 3, 6) };
    readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    bool _loading, _launching;

    LaunchGroup Group => _groups.SelectedItem as LaunchGroup;

    public MainForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "Session Startup Manager";
        Icon = AppIcon.Get();
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1280, 620);
        MinimumSize = new Size(960, 420);
        StartPosition = FormStartPosition.CenterScreen;

        BuildGrid();

        // ---- left: groups
        var groupsHeader = new Label { Text = "Groups  (ticked = in Launch all)", Dock = DockStyle.Top, AutoSize = false, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) };
        var groupButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        groupButtons.Controls.AddRange(new Control[]
        {
            Btn("New", NewGroup), Btn("Delete", DeleteGroup), Btn("Up", () => MoveGroup(-1)), Btn("Down", () => MoveGroup(1)),
        });
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 4, 6) };
        left.Controls.Add(_groups);
        left.Controls.Add(groupsHeader);
        left.Controls.Add(groupButtons);

        // ---- right: group settings, entry toolbar, grid
        _arrange.Items.AddRange(Enum.GetValues<ArrangeMode>().Select(m => (object)new Choice<ArrangeMode>(m, LaunchGroup.ArrangeText(m))).ToArray());
        var launchGroup = Btn("Launch group", LaunchCurrentGroup);
        launchGroup.Font = new Font(Font, FontStyle.Bold);
        launchGroup.Tag = Theme.AccentTag;
        var groupBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 6, 4, 2) };
        var arrangePair = Pair("Arrange", _arrange);
        groupBar.Controls.AddRange(new Control[]
        {
            Pair("Group", _groupName), Pair("Desktop", _desktop), Pair("Monitor", _monitor), arrangePair,
            _groupTabs, _groupStartup, launchGroup, Btn("Pin group to taskbar…", PinGroup),
        });
        groupBar.SetFlowBreak(arrangePair, true);

        var moveTo = Btn("Move to group ▾", null);
        moveTo.Click += (_, _) => ShowMoveMenu(moveTo);
        var entryBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
        entryBar.Controls.AddRange(new Control[]
        {
            Btn("Add sessions…", AddSessions),
            Btn("Add window…", AddWindow),
            Btn("Edit", EditSelected),
            Btn("Duplicate", DuplicateSelected),
            Btn("Remove", RemoveSelected),
            Btn("Move up", () => MoveEntry(-1)),
            Btn("Move down", () => MoveEntry(1)),
            moveTo,
            Btn("Launch selected", LaunchSelected),
        });

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 6, 0) };
        right.Controls.Add(_grid);
        right.Controls.Add(entryBar);
        right.Controls.Add(groupBar);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        // ---- bottom: global settings and Launch all
        _host.Items.AddRange(new object[] { "pwsh", "powershell" });
        var settings = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        settings.Controls.AddRange(new Control[]
        {
            _useWt, _returnHome,
            Lbl("AI sessions run in"), _host,
            Lbl("Extra delay (ms)"), _delay,
            Lbl("Startup delay (s)"), _startupDelay,
        });

        var launchAll = Btn("Launch all", LaunchAll);
        launchAll.Font = new Font(Font, FontStyle.Bold);
        launchAll.Padding = new Padding(12, 3, 12, 3);
        launchAll.Tag = Theme.AccentTag;
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Anchor = AnchorStyles.Right };
        actions.Controls.AddRange(new Control[]
        {
            launchAll,
            Btn("Pin Launch all…", PinLaunchAll),
            Btn("Open config", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{AppConfig.FilePath}\""))),
        });

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(6, 4, 6, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(settings, 0, 0);
        bottom.Controls.Add(actions, 1, 0);
        bottom.Controls.Add(_status, 0, 1);
        bottom.SetColumnSpan(_status, 2);

        // Fill first so the Bottom dock claims its space before the split.
        Controls.Add(split);
        Controls.Add(bottom);
        split.SplitterDistance = 320;
        Theme.Apply(this);
        Theme.StyleToolTip(_tips);

        _tips.SetToolTip(_desktop, "Switches to this desktop (Ctrl+Win+Arrow) before opening the group's windows.\nDesktops past the last one are created.");
        _tips.SetToolTip(_monitor, "Moves the group's windows onto this monitor after they open.");
        _tips.SetToolTip(_groupStartup, "Launch this group when you sign in to Windows (HKCU Run entry).");
        _tips.SetToolTip(_returnHome, "After launching groups on other desktops, switch back to the desktop you started from.");
        _tips.SetToolTip(_startupDelay, "Seconds to wait after sign-in before launching startup groups.");
        _tips.SetToolTip(_delay, "Extra pause between windows, on top of waiting for each one to appear.");

        // ---- events
        _groups.Format += (_, e) => e.Value = GroupLabel((LaunchGroup)e.ListItem);
        _groups.SelectedIndexChanged += (_, _) => { if (!_loading) LoadGroup(); };
        _groups.ItemCheck += (_, e) =>
        {
            if (_loading) return;
            ((LaunchGroup)_groups.Items[e.Index]).IncludeInLaunchAll = e.NewValue == CheckState.Checked;
            Save();
        };
        _groups.DoubleClick += (_, _) => { _groupName.Focus(); _groupName.SelectAll(); };

        _groupName.TextChanged += (_, _) => GroupEdited(g => g.Name = _groupName.Text.Trim().Length == 0 ? "Untitled" : _groupName.Text.Trim());
        _desktop.SelectedIndexChanged += (_, _) => GroupEdited(g => g.Desktop = ((Choice<int>)_desktop.SelectedItem).Value);
        _monitor.SelectedIndexChanged += (_, _) => GroupEdited(g =>
        {
            g.Monitor = ((Choice<string>)_monitor.SelectedItem).Value;
            var m = Monitors.Find(g.Monitor, null);
            if (m != null) g.MonitorBounds = Monitors.FormatBounds(m.Bounds);
            _arrange.Enabled = g.Monitor.Length > 0;
        });
        _arrange.SelectedIndexChanged += (_, _) => GroupEdited(g => g.Arrange = ((Choice<ArrangeMode>)_arrange.SelectedItem).Value);
        _groupTabs.CheckedChanged += (_, _) => GroupEdited(g => g.TabsInOneWindow = _groupTabs.Checked);
        _groupStartup.CheckedChanged += (_, _) =>
        {
            GroupEdited(g => g.RunAtStartup = _groupStartup.Checked);
            if (!_loading)
                _status.Text = _groupStartup.Checked
                    ? $"\"{Group.Name}\" will launch {_cfg.StartupDelaySec}s after you sign in."
                    : $"\"{Group.Name}\" no longer launches at sign-in.";
        };

        _useWt.CheckedChanged += (_, _) => SettingsChanged();
        _returnHome.CheckedChanged += (_, _) => SettingsChanged();
        _host.SelectedIndexChanged += (_, _) => SettingsChanged();
        _delay.ValueChanged += (_, _) => SettingsChanged();
        _startupDelay.ValueChanged += (_, _) => SettingsChanged();

        _loading = true;
        _useWt.Checked = cfg.UseWindowsTerminal;
        _returnHome.Checked = cfg.ReturnToStartingDesktop;
        _host.SelectedItem = string.Equals(cfg.SessionHost, "powershell", StringComparison.OrdinalIgnoreCase) ? "powershell" : "pwsh";
        _delay.Value = Math.Clamp(cfg.DelayMs, 0, 10000);
        _startupDelay.Value = Math.Clamp(cfg.StartupDelaySec, 0, 600);
        _loading = false;

        RefreshGroups(0);
        Save(); // writes migrated configs and keeps the Run entry pointing at this exe

        if (!Launcher.OnPath("wt.exe")) _status.Text = "Windows Terminal not found. Windows will open in the classic console, and titles can't be locked.";
        else if (cfg.Groups.All(g => g.Entries.Count == 0)) _status.Text = "Use \"Add sessions…\" for Claude, Codex or AGY, or \"Add window…\" to set up a new session or shell.";
        else _status.Text = $"Config: {AppConfig.FilePath}";
    }

    // Pull saved titles for providers that expose them locally.
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var targets = _cfg.Groups.SelectMany(g => g.Entries)
            .Where(x => (x.Kind is EntryKind.Claude or EntryKind.Codex) && x.SessionMode == SessionMode.Resume && !string.IsNullOrWhiteSpace(x.SessionId))
            .ToList();
        if (targets.Count == 0) return;

        var titles = await Task.Run(() => targets.Select(t => SessionScanner.TitleForSession(t.Kind, t.SessionId.Trim())).ToList());
        if (IsDisposed) return;
        int changed = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            if (titles[i] != null && titles[i] != targets[i].Name) { targets[i].Name = titles[i]; changed++; }
        }
        if (changed > 0)
        {
            Save();
            RefreshGrid(CurrentIndex);
            _status.Text = $"Synced {changed} name(s) from saved sessions.";
        }
    }

    // ================= groups

    static string GroupLabel(LaunchGroup g)
    {
        var bits = new List<string> { g.Entries.Count == 1 ? "1 window" : $"{g.Entries.Count} windows" };
        if (g.Desktop > 0) bits.Add("desktop " + g.Desktop);
        if (g.RunAtStartup) bits.Add("startup");
        return $"{g.Name}   ({string.Join(" · ", bits)})";
    }

    void RefreshGroups(int select)
    {
        _loading = true;
        _groups.Items.Clear();
        foreach (var g in _cfg.Groups) _groups.Items.Add(g, g.IncludeInLaunchAll);
        _groups.SelectedIndex = Math.Clamp(select, 0, _groups.Items.Count - 1);
        _loading = false;
        LoadGroup();
    }

    // Re-renders the selected group's label without touching selection or the editors.
    void RefreshGroupLabel()
    {
        int i = _groups.SelectedIndex;
        if (i < 0) return;
        bool was = _loading;
        _loading = true;
        _groups.Items[i] = _groups.Items[i];
        _groups.SetItemChecked(i, ((LaunchGroup)_groups.Items[i]).IncludeInLaunchAll);
        _loading = was;
    }

    void LoadGroup()
    {
        var g = Group;
        if (g == null) return;
        _loading = true;
        _groupName.Text = g.Name;
        FillDesktops(g.Desktop);
        FillMonitors(g.Monitor);
        _arrange.SelectedIndex = (int)g.Arrange;
        _arrange.Enabled = g.Monitor.Length > 0;
        _groupTabs.Checked = g.TabsInOneWindow;
        _groupStartup.Checked = g.RunAtStartup;
        _loading = false;
        RefreshGrid(0);
    }

    void FillDesktops(int selected)
    {
        _desktop.Items.Clear();
        _desktop.Items.Add(new Choice<int>(0, "Current desktop"));
        var desktops = VirtualDesktops.List();
        foreach (var d in desktops) _desktop.Items.Add(new Choice<int>(d.Index, $"{d.Index}: {d.Name}"));
        int max = Math.Max(desktops.Count + 2, selected);
        for (int i = desktops.Count + 1; i <= max; i++) _desktop.Items.Add(new Choice<int>(i, $"{i}: (new, created on launch)"));
        _desktop.SelectedItem = _desktop.Items.Cast<Choice<int>>().First(c => c.Value == selected || (c.Value == 0 && selected < 0));
    }

    void FillMonitors(string selected)
    {
        _monitor.Items.Clear();
        _monitor.Items.Add(new Choice<string>("", "Don't move (wherever Windows opens them)"));
        foreach (var m in Monitors.List()) _monitor.Items.Add(new Choice<string>(m.DeviceName, m.Label));
        var match = _monitor.Items.Cast<Choice<string>>().FirstOrDefault(c => string.Equals(c.Value, selected, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            match = new Choice<string>(selected, $"{selected.TrimStart('\\', '.')} (not connected)");
            _monitor.Items.Add(match);
        }
        _monitor.SelectedItem = match;
    }

    void GroupEdited(Action<LaunchGroup> apply)
    {
        if (_loading || Group == null) return;
        apply(Group);
        Save();
        RefreshGroupLabel();
    }

    void NewGroup()
    {
        string name = "New group";
        for (int n = 2; _cfg.Groups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"New group {n}";
        _cfg.Groups.Add(new LaunchGroup { Name = name });
        Save();
        RefreshGroups(_cfg.Groups.Count - 1);
        _groupName.Focus();
        _groupName.SelectAll();
    }

    void DeleteGroup()
    {
        var g = Group;
        if (g == null) return;
        if (_cfg.Groups.Count == 1) { _status.Text = "Keep at least one group. Rename this one instead."; return; }
        string what = g.Entries.Count == 0 ? "" : $" and its {g.Entries.Count} window(s)";
        if (MessageBox.Show(this, $"Delete group \"{g.Name}\"{what}?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        int i = _groups.SelectedIndex;
        _cfg.Groups.Remove(g);
        try { Shortcuts.RemoveGroup(g); } catch { }
        Save();
        RefreshGroups(Math.Min(i, _cfg.Groups.Count - 1));
    }

    void MoveGroup(int delta)
    {
        int i = _groups.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _cfg.Groups.Count) return;
        (_cfg.Groups[i], _cfg.Groups[j]) = (_cfg.Groups[j], _cfg.Groups[i]);
        Save();
        RefreshGroups(j);
    }

    void ShowMoveMenu(Control anchor)
    {
        var idx = SelectedIndexes();
        if (idx.Count == 0) { _status.Text = "Select the windows to move first."; return; }
        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        foreach (var target in _cfg.Groups.Where(g => g != Group))
        {
            var t = target;
            menu.Items.Add(t.Name, null, (_, _) => MoveEntriesTo(idx, t));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("New group…", null, (_, _) =>
        {
            var g = new LaunchGroup { Name = "New group" };
            _cfg.Groups.Add(g);
            MoveEntriesTo(idx, g);
        });
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    void MoveEntriesTo(List<int> idx, LaunchGroup target)
    {
        var source = Group;
        var moving = idx.Select(i => source.Entries[i]).ToList();
        foreach (var e in moving) source.Entries.Remove(e);
        target.Entries.AddRange(moving);
        Save();
        RefreshGroups(_cfg.Groups.IndexOf(source));
        _status.Text = $"Moved {moving.Count} window(s) to \"{target.Name}\".";
    }

    // ================= entries

    List<LaunchEntry> Entries => Group.Entries;

    void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.RowTemplate.Height = 26;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", FillWeight = 5, MinimumWidth = 36 });
        AddText("Name / title", 26);
        AddText("Type", 12);
        AddText("Mode / distro", 15);
        AddText("Folder", 28);
        AddText("Args / command", 20);
        AddText("Title", 7);

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_loading || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            Entries[e.RowIndex].Enabled = (bool)_grid.Rows[e.RowIndex].Cells[0].Value;
            Save();
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != 0) EditSelected(); };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { EditSelected(); e.Handled = e.SuppressKeyPress = true; }
        };

        void AddText(string header, float weight) =>
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = weight, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
    }

    void RefreshGrid(int select = -1)
    {
        if (Group == null) return;
        bool was = _loading;
        _loading = true;
        _grid.Rows.Clear();
        foreach (var e in Entries)
        {
            bool session = LaunchEntry.IsSession(e.Kind);
            string detail = session && e.SessionMode == SessionMode.Resume
                ? (string.IsNullOrWhiteSpace(e.SessionId) ? "(picker)" : e.SessionId[..Math.Min(8, e.SessionId.Length)]) + " " + e.Arguments
                : e.Arguments;
            string mode = e.Kind switch
            {
                EntryKind.Claude or EntryKind.Codex or EntryKind.Agy => LaunchEntry.ModeText(e.SessionMode),
                EntryKind.Wsl => string.IsNullOrWhiteSpace(e.Distro) ? "(default distro)" : e.Distro,
                _ => "",
            };
            _grid.Rows.Add(
                e.Enabled,
                string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name,
                LaunchEntry.KindText(e.Kind),
                mode,
                string.IsNullOrWhiteSpace(e.WorkingDirectory) ? "~" : e.WorkingDirectory,
                detail?.Trim() ?? "",
                e.LockTitle ? "Locked" : session ? "App" : "Fixed");
        }
        _loading = was;

        if (select >= 0 && select < _grid.Rows.Count)
        {
            _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[select].Cells[1];
            _grid.Rows[select].Selected = true;
        }
    }

    int CurrentIndex => _grid.CurrentRow?.Index ?? -1;

    List<int> SelectedIndexes() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).OrderBy(i => i).ToList();

    void AddSessions()
    {
        using var dlg = new SessionPickerDialog(true, _cfg.Groups);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var s in dlg.Selected)
        {
            Entries.Add(new LaunchEntry
            {
                Kind = s.Kind,
                SessionMode = SessionMode.Resume,
                SessionId = s.Id,
                Name = s.Title ?? s.Id[..Math.Min(8, s.Id.Length)],
                WorkingDirectory = s.Cwd,
            });
        }
        EntriesChanged(Entries.Count - 1);
        _status.Text = $"Added {dlg.Selected.Count} session(s) to \"{Group.Name}\".";
    }

    void AddWindow()
    {
        var entry = new LaunchEntry { Kind = EntryKind.Pwsh, SessionMode = SessionMode.Continue };
        using var dlg = new EntryDialog(entry, _cfg.Groups);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Entries.Add(entry);
        EntriesChanged(Entries.Count - 1);
    }

    void EditSelected()
    {
        int i = CurrentIndex;
        if (i < 0) return;
        var copy = Entries[i].Clone();
        using var dlg = new EntryDialog(copy, _cfg.Groups);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Entries[i].CopyFrom(copy);
        EntriesChanged(i);
    }

    void DuplicateSelected()
    {
        int i = CurrentIndex;
        if (i < 0) return;
        Entries.Insert(i + 1, Entries[i].Clone());
        EntriesChanged(i + 1);
    }

    void RemoveSelected()
    {
        var idx = SelectedIndexes();
        if (idx.Count == 0) return;
        string what = idx.Count == 1 ? $"\"{Entries[idx[0]].Name}\"" : $"{idx.Count} windows";
        if (MessageBox.Show(this, $"Remove {what} from \"{Group.Name}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (int i in idx.OrderByDescending(i => i)) Entries.RemoveAt(i);
        EntriesChanged(Math.Min(idx[0], Entries.Count - 1));
    }

    void MoveEntry(int delta)
    {
        int i = CurrentIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= Entries.Count) return;
        (Entries[i], Entries[j]) = (Entries[j], Entries[i]);
        EntriesChanged(j);
    }

    void EntriesChanged(int select)
    {
        Save();
        RefreshGrid(select);
        RefreshGroupLabel();
    }

    // ================= launching

    static LaunchJob Job(LaunchGroup g, IEnumerable<LaunchEntry> entries) => new() { Group = g, Entries = entries.ToList() };

    void LaunchAll() => Launch(
        _cfg.Groups.Where(g => g.IncludeInLaunchAll).Select(g => Job(g, g.Entries.Where(e => e.Enabled))),
        "Nothing to launch. Tick a group on the left and the windows inside it.");

    void LaunchCurrentGroup() => Launch(new[] { Job(Group, Entries.Where(e => e.Enabled)) }, "No windows in this group are switched on.");

    void LaunchSelected() => Launch(new[] { Job(Group, SelectedIndexes().Select(i => Entries[i])) }, "Select one or more rows first.");

    async void Launch(IEnumerable<LaunchJob> jobs, string emptyMessage)
    {
        if (_launching) return;
        var list = jobs.Where(j => j.Entries.Count > 0).ToList();
        if (list.Count == 0) { _status.Text = emptyMessage; return; }

        _launching = true;
        UseWaitCursor = true;
        var progress = new Progress<string>(s => { if (_launching) _status.Text = s; });
        var errors = await Task.Run(() => Launcher.Launch(_cfg, list, progress));
        _launching = false;
        UseWaitCursor = false;

        int count = list.Sum(j => j.Entries.Count);
        _status.Text = errors.Count == 0 ? $"Launched {count} window(s)." : $"Launched with {errors.Count} problem(s).";
        if (errors.Count > 0)
            MessageBox.Show(this, "Some windows didn't start or couldn't be placed:\n\n" + string.Join("\n", errors), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ================= shortcuts

    void PinLaunchAll() => Pin(Shortcuts.CreateLaunchAll, "Launch all",
        "\"Launch Sessions\" is on your desktop and in the Start menu.");

    void PinGroup() => Pin(() => Shortcuts.CreateGroup(Group), Group.Name,
        $"\"Launch {Group.Name}\" is in Start menu > Session Startup Manager.");

    void Pin(Func<string> create, string what, string where)
    {
        string lnk;
        try { lnk = create(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't create the shortcut: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        MessageBox.Show(this,
            where + "\n\n" +
            "Windows doesn't let apps pin themselves, so one manual step: in the Explorer window that opens, " +
            "right-click the highlighted shortcut > Show more options > Pin to taskbar.\n\n" +
            "Each group gets its own taskbar button. Shift+click any of them to open this manager instead.",
            "Pin " + what, MessageBoxButtons.OK, MessageBoxIcon.Information);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lnk}\""));
    }

    // ================= settings / persistence

    void SettingsChanged()
    {
        if (_loading) return;
        _cfg.UseWindowsTerminal = _useWt.Checked;
        _cfg.ReturnToStartingDesktop = _returnHome.Checked;
        _cfg.SessionHost = (string)_host.SelectedItem ?? "pwsh";
        _cfg.DelayMs = (int)_delay.Value;
        _cfg.StartupDelaySec = (int)_startupDelay.Value;
        Save();
    }

    void Save()
    {
        try
        {
            _cfg.Save();
            StartupRegistration.Sync(_cfg);
        }
        catch (Exception ex) { _status.Text = "Save failed: " + ex.Message; }
    }

    static Button Btn(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    static ComboBox Combo(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(3, 4, 12, 3) };

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 12, 3) };

    static Label Lbl(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };

    // Keeps a label on the same line as its control when the bar wraps.
    static FlowLayoutPanel Pair(string label, Control control)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        p.Controls.Add(Lbl(label));
        p.Controls.Add(control);
        return p;
    }
}
