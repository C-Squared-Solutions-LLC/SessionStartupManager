namespace ClaudeStartupManager;

internal sealed class SessionPickerDialog : Form
{
    static List<SessionInfo> _cache; // scanning ~600 logs takes a moment, so keep it for the app's lifetime

    readonly bool _multi;
    readonly HashSet<string> _alreadyAdded;
    readonly TextBox _filter = new() { Width = 260, PlaceholderText = "Filter by name, folder or id" };
    readonly CheckBox _namedOnly = new() { Text = "Named sessions only", AutoSize = true, Checked = true, Margin = new Padding(12, 6, 3, 3) };
    readonly CheckBox _latestOnly = new() { Text = "Latest per name", AutoSize = true, Checked = true, Margin = new Padding(12, 6, 3, 3) };
    readonly Button _refresh = new() { Text = "Rescan", AutoSize = true };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 9, 3, 3) };
    readonly Button _ok = new() { Text = "Add", AutoSize = true, DialogResult = DialogResult.OK };

    public List<SessionInfo> Selected { get; } = new();

    public SessionPickerDialog(bool multi, IEnumerable<string> alreadyAdded)
    {
        _multi = multi;
        _alreadyAdded = new HashSet<string>(alreadyAdded ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        Text = multi ? "Add Claude sessions" : "Pick a Claude session";
        Icon = AppIcon.Get();
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(900, 520);
        MinimumSize = new Size(600, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        _ok.Text = multi ? "Add checked" : "Select";

        _list.CheckBoxes = multi;
        _list.MultiSelect = multi;
        _list.Columns.Add("Session name", 280);
        _list.Columns.Add("Folder", 320);
        _list.Columns.Add("Last used", 130);
        _list.Columns.Add("Session id", 140);
        _list.DoubleClick += (_, _) => { if (!_multi && _list.SelectedItems.Count == 1) { DialogResult = DialogResult.OK; Close(); } };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        top.Controls.AddRange(new Control[] { _filter, _namedOnly, _latestOnly, _refresh });

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
        bottom.Controls.AddRange(new Control[] { cancel, _ok, _status });

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bottom);
        AcceptButton = _ok;
        CancelButton = cancel;
        Theme.Apply(this);

        _filter.TextChanged += (_, _) => Fill();
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
        _status.Text = "Scanning Claude sessions…";
        var progress = new Progress<string>(s => _status.Text = s);
        try { _cache = await Task.Run(() => SessionScanner.ScanAll(progress)); }
        catch (Exception ex) { _cache = new(); _status.Text = "Scan failed: " + ex.Message; }
        _refresh.Enabled = _ok.Enabled = true;
        Fill();
    }

    void Fill()
    {
        if (_cache == null) return;
        IEnumerable<SessionInfo> rows = _cache.Where(s => s.Cwd != null);
        if (_namedOnly.Checked) rows = rows.Where(s => s.Named);
        if (_latestOnly.Checked)
            rows = rows.GroupBy(s => ((s.Title ?? s.Id) + "|" + s.Cwd).ToLowerInvariant()).Select(g => g.First()); // _cache is newest-first

        string f = _filter.Text.Trim();
        if (f.Length > 0)
            rows = rows.Where(s => (s.Title ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                                || s.Cwd.Contains(f, StringComparison.OrdinalIgnoreCase)
                                || s.Id.StartsWith(f, StringComparison.OrdinalIgnoreCase));

        _list.BeginUpdate();
        _list.Items.Clear();
        int count = 0;
        foreach (var s in rows)
        {
            bool added = _alreadyAdded.Contains(s.Id);
            var item = new ListViewItem(s.Title ?? "(untitled)") { Tag = s };
            item.SubItems.Add(s.Cwd);
            item.SubItems.Add(s.LastUsed.ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add(added ? s.Id[..Math.Min(8, s.Id.Length)] + "  (added)" : s.Id);
            if (added) item.ForeColor = Theme.Muted;
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
