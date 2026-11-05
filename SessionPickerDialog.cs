namespace SessionStartupManager;

internal sealed class SessionPickerDialog : Form
{
    sealed record ProviderChoice(EntryKind? Kind, string Text) { public override string ToString() => Text; }
    static List<SessionInfo> _cache; // scanning ~600 logs takes a moment, so keep it for the app's lifetime

    readonly bool _multi;
    readonly IEnumerable<LaunchGroup> _groups;
    readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly TextBox _filter = new() { Width = 260, PlaceholderText = "Filter by name, folder or id" };
    readonly CheckBox _namedOnly = new() { Text = "Named sessions only", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    readonly CheckBox _latestOnly = new() { Text = "Latest per name", AutoSize = true, Checked = true, Margin = new Padding(12, 6, 3, 3) };
    readonly Button _refresh = new() { Text = "Rescan", AutoSize = true };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true };
    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 9, 3, 3) };
    readonly Button _ok = new() { Text = "Add", AutoSize = true, DialogResult = DialogResult.OK };
    readonly Label _hint = new() { AutoSize = true, Padding = new Padding(8, 4, 8, 4), Dock = DockStyle.Bottom,
        Text = "AGY lists the latest cached conversation per workspace. Use Add window to enter an older conversation ID." };

    public List<SessionInfo> Selected { get; } = new();

    public SessionPickerDialog(bool multi, IEnumerable<LaunchGroup> groups, EntryKind? kind = null)
    {
        _multi = multi;
        _groups = groups ?? Enumerable.Empty<LaunchGroup>();

        Text = multi ? "Add sessions" : "Pick a " + (kind.HasValue ? LaunchEntry.KindText(kind.Value) + " " : "") + "session";
        Icon = AppIcon.Get();
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1200, 520);
        MinimumSize = new Size(600, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        _ok.Text = multi ? "Add checked" : "Select";

        _list.CheckBoxes = multi;
        _list.MultiSelect = multi;
        _list.Columns.Add("Session name", 220);
        _list.Columns.Add("Provider", 130);
        _list.Columns.Add("Used in groups", 230);
        _list.Columns.Add("Folder", 260);
        _list.Columns.Add("Last used", 130);
        _list.Columns.Add("Session id", 140);
        _list.DoubleClick += (_, _) => { if (!_multi && _list.SelectedItems.Count == 1) { DialogResult = DialogResult.OK; Close(); } };

        _provider.Items.Add(new ProviderChoice(null, "All providers"));
        foreach (var provider in new[] { EntryKind.Claude, EntryKind.Codex, EntryKind.Agy })
            _provider.Items.Add(new ProviderChoice(provider, LaunchEntry.KindText(provider)));
        _provider.SelectedItem = _provider.Items.Cast<ProviderChoice>().First(p => p.Kind == kind);
        _provider.Enabled = !kind.HasValue;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        top.Controls.AddRange(new Control[] { _provider, _filter, _namedOnly, _latestOnly, _refresh });

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
        bottom.Controls.AddRange(new Control[] { cancel, _ok, _status });

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(_hint);
        Controls.Add(bottom);
        AcceptButton = _ok;
        CancelButton = cancel;
        Theme.Apply(this);

        _filter.TextChanged += (_, _) => Fill();
        _provider.SelectedIndexChanged += (_, _) => Fill();
        _namedOnly.CheckedChanged += (_, _) => Fill();
        _latestOnly.CheckedChanged += (_, _) => Fill();
        _refresh.Click += async (_, _) => await Rescan();
        FormClosing += OnClosingCollect;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_cache == null) await Rescan();
        else Fill();
        _filter.Focus();
    }

    async Task Rescan()
    {
        _refresh.Enabled = _ok.Enabled = false;
        _list.Items.Clear();
        _status.Text = "Scanning sessions…";
        var progress = new Progress<string>(s => { if (!IsDisposed) _status.Text = s; });
        try { _cache = await Task.Run(() => SessionScanner.ScanAll(progress)); }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _status.Text = "Scan failed: " + ex.Message;
            _refresh.Enabled = true;
            return;
        }
        if (IsDisposed) return;
        _refresh.Enabled = _ok.Enabled = true;
        Fill();
    }

    void Fill()
    {
        if (_cache == null) return;
        IEnumerable<SessionPickerRow> rows = SessionPickerModel.Rows(_cache, _groups, _latestOnly.Checked, _namedOnly.Checked);
        var provider = ((ProviderChoice)_provider.SelectedItem).Kind;
        if (provider.HasValue) rows = rows.Where(r => r.Session.Kind == provider.Value);
        _hint.Visible = !provider.HasValue || provider == EntryKind.Agy;

        string f = _filter.Text.Trim();
        if (f.Length > 0)
            rows = rows.Where(r => (r.Session.Title ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                                || LaunchEntry.KindText(r.Session.Kind).Contains(f, StringComparison.OrdinalIgnoreCase)
                                || r.UsageText.Contains(f, StringComparison.OrdinalIgnoreCase)
                                || r.Session.Cwd.Contains(f, StringComparison.OrdinalIgnoreCase)
                                || r.Session.Id.StartsWith(f, StringComparison.OrdinalIgnoreCase));

        _list.BeginUpdate();
        _list.Items.Clear();
        int count = 0;
        foreach (var row in rows)
        {
            var s = row.Session;
            var item = new ListViewItem(s.Title ?? "(untitled)") { Tag = s,
                ToolTipText = row.SameNameGroups.Length > 0
                    ? row.UsageText + "\nSame name means a different session ID with this name and folder is already assigned."
                    : row.UsageText };
            item.SubItems.Add(LaunchEntry.KindText(s.Kind));
            item.SubItems.Add(row.UsageText);
            item.SubItems.Add(s.Cwd);
            item.SubItems.Add(s.LastUsed == DateTime.MinValue ? "—" : s.LastUsed.ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add(s.Id);
            if (row.HasUsage) item.ForeColor = Theme.Muted;
            _list.Items.Add(item);
            count++;
        }
        _list.EndUpdate();
        _status.Text = $"{count} of {_cache.Count} sessions";
    }

    void OnClosingCollect(object sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK) return;
        IEnumerable<ListViewItem> picked = _multi && _list.CheckedItems.Count > 0
            ? _list.CheckedItems.Cast<ListViewItem>()
            : _list.SelectedItems.Cast<ListViewItem>();
        Selected.AddRange(picked.Select(i => (SessionInfo)i.Tag));
        if (Selected.Count == 0)
        {
            e.Cancel = true;
            _status.Text = _multi ? "Tick at least one session." : "Select a session.";
        }
    }
}
