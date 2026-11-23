using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UELauncher;

public sealed class MainForm : Form, IMessageFilter
{
	[DllImport("user32.dll")] static extern bool PostMessage(IntPtr Window, uint Message, IntPtr W, IntPtr L);
	const int WmMouseWheel = 0x020A;

	/// <summary>The wheel never changes a closed dropdown. Over the options it scrolls the options instead; an open list still scrolls normally.</summary>
	public bool PreFilterMessage(ref Message M)
	{
		if (M.Msg != WmMouseWheel || Control.FromChildHandle(M.HWnd) is not ComboBox B || B.DroppedDown) { return false; }
		if (_options.Contains(B)) { SendMessage(_options.Handle, WmMouseWheel, M.WParam, M.LParam); }
		return true;
	}

	[StructLayout(LayoutKind.Sequential)] struct Rect32 { public int Left, Top, Right, Bottom; }
	[StructLayout(LayoutKind.Sequential)] struct ComboInfo { public int Size; public Rect32 Item, Button; public int ButtonState; public IntPtr Combo, Edit, List; }
	[DllImport("user32.dll")] static extern bool GetComboBoxInfo(IntPtr Window, ref ComboInfo Info);
	[DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr Window, uint Message, IntPtr W, IntPtr L);
	[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr Window, out Rect32 Rect);
	[DllImport("user32.dll")] static extern bool PrintWindow(IntPtr Window, IntPtr Dc, uint Flags);
	const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, LbSetCurSel = 0x0186, WmKeyDown = 0x0100;
	const int VkEscape = 0x1B;

	readonly TextBox _project = new() { ReadOnly = true, Dock = DockStyle.Fill };
	readonly ComboBox _engine = new() { Dock = DockStyle.Fill };
	readonly Label _engineInfo = new() { AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
	readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
	readonly Panel _options = new() { Dock = DockStyle.Fill, AutoScroll = true };
	readonly TextBox _command = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
	readonly RichTextBox _log = new() { ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Both, WordWrap = false, DetectUrls = false, HideSelection = false, BackColor = SystemColors.Window, Font = new Font("Consolas", 9), MaxLength = int.MaxValue };
	readonly Font _logBold = new("Consolas", 9, FontStyle.Bold);
	readonly CheckBox _errorsOnly = new() { Text = "Errors and warnings only", AutoSize = true };
	readonly TextBox _flagSearch = new() { Dock = DockStyle.Top, PlaceholderText = "Search this engine's flags" };
	readonly ListView _flags = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true };
	readonly AccentButton _build = new() { Text = "Build", AutoSize = true, MinimumSize = new Size(100, 0), BackColor = Color.FromArgb(0, 120, 212), ForeColor = Color.White };
	readonly Label _why = new() { AutoSize = true, ForeColor = Color.Firebrick, Padding = new Padding(8, 7, 0, 0), MaximumSize = new Size(520, 0) };
	readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 0), Enabled = false };
	readonly Label _phase = new() { AutoSize = true, Padding = new Padding(8, 7, 0, 0), Text = "Idle" };
	readonly ToolTip _tips = new();
	readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
	readonly System.Windows.Forms.Timer _drain = new() { Interval = 200 };
	readonly List<string> _logLines = new();
	readonly System.Collections.Concurrent.ConcurrentQueue<string> _incoming = new();
	readonly List<(Label Header, List<Row> Rows)> _layout = new();
	ComboBox? _schemeBox;
	Label? _schemeHint;
	ProjectInfo? _proj;
	EngineInfo? _eng;
	Profile _current = Profile.Shipping();
	BuildRunner? _runner;
	DateTime _started;
	string _phaseName = "Idle";
	bool _loading;
	TabControl _tabs = null!;
	readonly string[]? _selfTest;
	readonly string? _memoryPhase;
	readonly Button _revert = new() { Text = "Revert", AutoSize = true, Enabled = false };
	readonly System.Windows.Forms.Timer _saveState = new() { Interval = 500 };
	readonly string _statePath;
	readonly AppState _state;
	int _schemeNameWidth;

	sealed class Row
	{
		public required OptionDef O { get; init; }
		public required Control Editor { get; init; }
		public Control? Caption { get; init; }
		public Control? Extra { get; init; }
		public Func<bool>? ExtraVisible { get; init; }
	}

	protected override bool ShowWithoutActivation => _selfTest != null;

	/// <param name="SelfTest">Out folder, second engine folder, profile file: drives the real form off-screen and without focus, then closes it.</param>
	public MainForm(string? UProject, string[]? SelfTest = null, string? MemoryPhase = null)
	{
		Application.AddMessageFilter(this);
		FormClosed += (_, _) => Application.RemoveMessageFilter(this);
		_selfTest = SelfTest;
		_memoryPhase = MemoryPhase;
		_statePath = SelfTest != null ? Path.Combine(SelfTest[0], "state.json") : AppState.DefaultPath;
		_state = AppState.Load(_statePath);
		Text = "UE Launcher";
		ClientSize = new Size(1180, 780);
		MinimumSize = new Size(900, 560);
		if (_selfTest != null)
		{
			StartPosition = FormStartPosition.Manual;
			Location = new Point(-32000, -32000);
			ShowInTaskbar = false;
			Shown += async (_, _) => await (MemoryPhase != null ? RunMemoryTest() : RunSelfTest());
		}
		var Header_ = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(6) };
		Header_.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		Header_.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		Header_.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		var BrowseProject = new Button { Text = "Open...", AutoSize = true };
		var BrowseEngine = new Button { Text = "Folder...", AutoSize = true };
		var Rescan = new Button { Text = "Rescan", AutoSize = true };
		var EngineButtons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
		EngineButtons.Controls.AddRange(new Control[] { BrowseEngine, Rescan });
		Header_.Controls.Add(Caption("Project"), 0, 0); Header_.Controls.Add(_project, 1, 0); Header_.Controls.Add(BrowseProject, 2, 0);
		Header_.Controls.Add(Caption("Engine"), 0, 1); Header_.Controls.Add(_engine, 1, 1); Header_.Controls.Add(EngineButtons, 2, 1);
		Header_.Controls.Add(_engineInfo, 1, 2);
		var ProfileBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 0, 6, 4) };
		var SaveBtn = new Button { Text = "Save", AutoSize = true };
		var SaveAs = new Button { Text = "Save as...", AutoSize = true };
		ProfileBar.Controls.AddRange(new Control[] { Caption("Profile"), _profiles, SaveBtn, SaveAs, _revert, _build, _cancel, _phase, _why });
		_tips.SetToolTip(_revert, "Discard the unsaved changes and go back to this profile as last saved");
		var Tabs = new TabControl { Dock = DockStyle.Fill };
		var CmdTab = new TabPage("Command");
		var CopyBtn = new Button { Text = "Copy command", Dock = DockStyle.Top, Height = 26, Width = 120 };
		CmdTab.Controls.Add(_command); CmdTab.Controls.Add(CopyBtn);
		var LogTab = new TabPage("Log");
		var LogBar = new Panel { Dock = DockStyle.Top, Height = 26 };
		LogBar.Controls.Add(_errorsOnly);
		LogTab.Controls.Add(_log); LogTab.Controls.Add(LogBar);
		var FlagTab = new TabPage("All engine flags");
		_flags.Columns.Add("Flag", 200); _flags.Columns.Add("Description", 420);
		FlagTab.Controls.Add(_flags); FlagTab.Controls.Add(_flagSearch);
		Tabs.TabPages.AddRange(new[] { CmdTab, LogTab, FlagTab });
		_tabs = Tabs;
		var Split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
		Split.Panel1.Controls.Add(_options); Split.Panel2.Controls.Add(Tabs);
		Controls.Add(Split); Controls.Add(ProfileBar); Controls.Add(Header_);
		Load += (_, _) => { Split.SplitterDistance = Math.Min(560, ClientSize.Width - 320); };

		BrowseProject.Click += (_, _) =>
		{
			using var D = new OpenFileDialog { Filter = "Unreal project (*.uproject)|*.uproject" };
			if (D.ShowDialog(this) == DialogResult.OK) { OpenProject(D.FileName); }
		};
		BrowseEngine.Click += (_, _) =>
		{
			using var D = new FolderBrowserDialog { Description = "Select an Unreal Engine folder (launcher or built from source)" };
			if (D.ShowDialog(this) == DialogResult.OK) { SelectEngine(D.SelectedPath, false); }
		};
		Rescan.Click += (_, _) =>
		{
			if (_proj != null)
			{
				_proj = ProjectInfo.Load(_proj.Path_);
				LoadProfiles(_current.Name);
			}
			if (_eng != null) { SelectEngine(_eng.Root, true); }
		};
		_engine.SelectionChangeCommitted += (_, _) => { if (_engine.SelectedItem is EngineItem I) { SelectEngine(I.Root, false); } };
		_engine.KeyDown += (_, K) => { if (K.KeyCode == Keys.Enter && _engine.Text.Length > 0) { SelectEngine(_engine.Text, false); } };
		_profiles.SelectedIndexChanged += (_, _) => { if (!_loading && _profiles.SelectedItem is Profile P) { _current = Working(P); RebuildOptions(); } };
		SaveBtn.Click += (_, _) => SaveProfile(_current.Name);
		SaveAs.Click += (_, _) =>
		{
			var N = Prompt("Profile name", _current.Name);
			if (string.IsNullOrWhiteSpace(N)) { return; }
			// The edits move to the new profile, so the original goes back to how it was saved.
			if (_proj != null) { _state.For(_proj.Path_).Working.Remove(_current.Name); }
			_current.Name = N.Trim();
			SaveProfile(_current.Name);
		};
		_revert.Click += (_, _) => RevertProfile();
		_saveState.Tick += (_, _) => { _saveState.Stop(); _state.Save(_statePath); };
		FormClosing += (_, _) => { Remember(); _saveState.Stop(); _state.Save(_statePath); };
		CopyBtn.Click += (_, _) => { if (_command.Text.Length > 0) { Clipboard.SetText(_command.Text); } };
		_build.Click += async (_, _) => { Tabs.SelectedTab = LogTab; await StartBuild(); };
		_cancel.Click += (_, _) => { _cancel.Enabled = false; _runner?.Cancel(); };
		_errorsOnly.CheckedChanged += (_, _) => RefillLog();
		_flagSearch.TextChanged += (_, _) => FillFlags();
		_flags.ItemChecked += (_, Ev) =>
		{
			if (_loading) { return; }
			var Name = Ev.Item.Text;
			_current.Flags.RemoveAll(F => F.TrimStart('-').Split('=')[0].Equals(Name, StringComparison.OrdinalIgnoreCase));
			if (Ev.Item.Checked) { _current.Flags.Add(Name); }
			Preview();
		};
		_clock.Tick += (_, _) => ShowPhase();
		_drain.Tick += (_, _) => DrainLog();
		_drain.Start();

		foreach (var (Name, Root) in EngineScanner.Discover()) { _engine.Items.Add(new EngineItem(Name, Root)); }
		var Start = UProject ?? (_state.LastProject.Length > 0 ? _state.LastProject : null);
		if (Start != null && File.Exists(Start)) { OpenProject(Start); }
		else if (_state.LastEngine.Length > 0 && EngineScanner.IsEngineRoot(_state.LastEngine)) { SelectEngine(_state.LastEngine, false); }
		else if (_engine.Items.Count > 0) { SelectEngine(((EngineItem)_engine.Items[0]!).Root, false); }
		else { RebuildOptions(); }
	}

	sealed record EngineItem(string Name, string Root) { public override string ToString() => $"{Name}  ({Root})"; }
	static Label Caption(string T) => new() { Text = T, AutoSize = true, Padding = new Padding(0, 7, 6, 0) };

	void OpenProject(string FilePath)
	{
		try
		{
			_proj = ProjectInfo.Load(FilePath);
			_project.Text = _proj.Path_;
			Text = $"UE Launcher - {_proj.Name}";
			var Memory = _state.For(_proj.Path_);
			LoadProfiles(Memory.Profile.Length > 0 ? Memory.Profile : null);
			_eng = null;
			_engine.SelectedItem = null;
			var Remembered = Memory.Engine.Length > 0 && Memory.Association == _proj.EngineAssociation && EngineScanner.IsEngineRoot(Memory.Engine);
			var Root = Remembered ? Memory.Engine : EngineScanner.Resolve(_proj.EngineAssociation);
			if (Root != null) { SelectEngine(Root, false); }
			else { _engineInfo.Text = $"The project asks for engine '{_proj.EngineAssociation}', which was not found: select its folder."; RebuildOptions(); }
		}
		catch (Exception Ex) when (Ex is IOException or JsonException or UnauthorizedAccessException) { MessageBox.Show(this, Ex.Message, "Cannot open project"); }
	}

	void LoadProfiles(string? Select)
	{
		if (_proj == null) { return; }
		_loading = true;
		_profiles.Items.Clear();
		_profiles.DisplayMember = nameof(Profile.Name);
		foreach (var P in Profile.LoadAll(_proj)) { _profiles.Items.Add(P); }
		var I = Math.Max(0, _profiles.Items.Cast<Profile>().ToList().FindIndex(P => P.Name == (Select ?? "Shipping")));
		_profiles.SelectedIndex = I;
		_current = Working((Profile)_profiles.Items[I]!);
		_loading = false;
	}

	/// <summary>The profile with its remembered unsaved changes, if it has any.</summary>
	Profile Working(Profile Saved) => _proj != null && _state.For(_proj.Path_).Working.TryGetValue(Saved.Name, out var Edited) ? Edited.Clone() : Saved.Clone();

	void RevertProfile()
	{
		if (_proj == null || _profiles.SelectedItem is not Profile Saved) { return; }
		_state.For(_proj.Path_).Working.Remove(Saved.Name);
		_current = Saved.Clone();
		RebuildOptions();
	}

	/// <summary>Records the current project, engine, profile and options; the file is written shortly after, and on exit.</summary>
	void Remember()
	{
		if (_proj != null)
		{
			_state.LastProject = _proj.Path_;
			var Memory = _state.For(_proj.Path_);
			Memory.Profile = _current.Name;
			if (_eng != null)
			{
				Memory.Engine = _eng.Root;
				Memory.Association = _proj.EngineAssociation;
			}
			var Saved = _profiles.Items.OfType<Profile>().FirstOrDefault(P => P.Name == _current.Name);
			var Changed = Saved == null || !AppState.Same(Saved, _current);
			if (Changed) { Memory.Working[_current.Name] = _current.Clone(); } else { Memory.Working.Remove(_current.Name); }
			_revert.Enabled = Changed && Saved != null;
		}
		if (_eng != null) { _state.LastEngine = _eng.Root; }
		_saveState.Stop();
		_saveState.Start();
	}

	void SaveProfile(string Name)
	{
		if (_proj == null) { return; }
		var Safe = string.Concat(Name.Select(C => Path.GetInvalidFileNameChars().Contains(C) ? '_' : C));
		_current.Save(Path.Combine(_proj.ProfileDir, Safe + ".json"));
		_state.For(_proj.Path_).Working.Remove(Name);
		LoadProfiles(Name);
		RebuildOptions();
	}

	async void SelectEngine(string Folder, bool Force)
	{
		try
		{
			UseWaitCursor = true;
			_engineInfo.Text = "Scanning...";
			var Scanned = await Task.Run(() => EngineScanner.Scan(Folder, Force));
			_eng = Scanned;
			var Item = _engine.Items.OfType<EngineItem>().FirstOrDefault(I => I.Root.Equals(Scanned.Root, StringComparison.OrdinalIgnoreCase));
			if (Item == null) { Item = new EngineItem(Path.GetFileName(Scanned.Root), Scanned.Root); _engine.Items.Add(Item); }
			_engine.SelectedItem = Item;
			_engineInfo.Text = $"UE {Scanned.Version} (CL {Scanned.Changelist}), {Scanned.Kind} build, {Scanned.Flags.Count} flags from {Scanned.FlagSource}, options from {Scanned.Launcher.Source}" + (Scanned.Supported ? "" : "  - older than 5.6, not supported") + (Scanned.IsInstalled ? "" : "  - UAT and the editor may compile on first use");
			RebuildOptions();
		}
		catch (Exception Ex) when (Ex is InvalidOperationException or IOException or JsonException or UnauthorizedAccessException or ArgumentException) { _engineInfo.Text = Ex.Message; }
		finally { UseWaitCursor = false; }
	}

	// ---------------------------------------------------------------- options

	void RebuildOptions()
	{
		_loading = true;
		_options.SuspendLayout();
		foreach (var Old in _options.Controls.Cast<Control>().ToList()) { Old.Dispose(); }
		_layout.Clear();
		_schemeBox = null;
		_schemeHint = null;
		var T = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(6) };
		T.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
		T.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		foreach (var S in OptionCatalog.Layout(_eng))
		{
			var H = new Label { Text = S.Title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2) };
			T.Controls.Add(H);
			T.SetColumnSpan(H, 2);
			var Rows = new List<Row>();
			foreach (var O in S.Items) { Rows.Add(MakeRow(T, O)); }
			_layout.Add((H, Rows));
		}
		_options.Controls.Add(T);
		_options.ResumeLayout();
		_loading = false;
		RefreshStates();
		FillFlags();
	}

	static Label RowLabel(string S) => new() { Text = S, AutoSize = true, Padding = new Padding(0, 5, 0, 0) };

	Row Pair(TableLayoutPanel T, OptionDef O, Control Editor)
	{
		var L = RowLabel(O.Label);
		Editor.Dock = DockStyle.Fill;
		T.Controls.Add(L);
		T.Controls.Add(Editor);
		return new Row { O = O, Editor = Editor, Caption = L };
	}

	ComboBox Combo(IEnumerable<string> Items, string Value, Action<string> Set)
	{
		var B = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
		B.Items.AddRange(Items.Cast<object>().ToArray());
		if (!B.Items.Contains(Value)) { B.Items.Add(Value); }
		B.SelectedItem = Value;
		var Applied = Value;
		WhenPicked(B, () =>
		{
			if (B.SelectedItem is string V && V != Applied)
			{
				Applied = V;
				Set(V);
				RefreshStates();
			}
		});
		return B;
	}

	/// <summary>Applies a dropdown's value only once its list has closed, because changing the layout or a neighbouring control while it is open makes Windows close it.
	/// Hovering changes nothing; a click or Enter applies; Escape or clicking away keeps the old value.</summary>
	void WhenPicked(ComboBox B, Action Apply)
	{
		var Open = false;
		var Committed = false;
		var Opened = -1;
		void Finish()
		{
			if (!Open) { return; }
			Open = false;
			if (B.IsDisposed) { return; }
			if (Committed) { if (!_loading) { Apply(); } }
			else if (B.SelectedIndex != Opened && Opened < B.Items.Count)
			{
				var Was = _loading;
				_loading = true;
				B.SelectedIndex = Opened;
				_loading = Was;
			}
		}
		B.DropDown += (_, _) => { Open = true; Committed = false; Opened = B.SelectedIndex; };
		B.SelectionChangeCommitted += (_, _) => Committed = true;
		B.DropDownClosed += (_, _) => { if (IsHandleCreated && !IsDisposed) { BeginInvoke(Finish); } };
		B.SelectedIndexChanged += (_, _) => { if (!Open && !_loading && !B.IsDisposed) { Apply(); } };
	}

	Row MakeRow(TableLayoutPanel T, OptionDef O)
	{
		switch (O.Kind)
		{
			case OptKind.Check:
			{
				var B = new CheckBox { Text = O.Label, Checked = O.GetBool!(_current), AutoSize = true };
				B.CheckedChanged += (_, _) => { if (!_loading) { O.SetBool!(_current, B.Checked); RefreshStates(); } };
				T.Controls.Add(B);
				T.SetColumnSpan(B, 2);
				return new Row { O = O, Editor = B };
			}
			case OptKind.Tri:
			{
				string[] Values = { "Project", "On", "Off" };
				var B = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
				B.Items.AddRange(new object[] { "Project setting", "On", "Off" });
				B.SelectedIndex = Math.Max(0, Array.IndexOf(Values, O.GetText!(_current)));
				var Applied = B.SelectedIndex;
				WhenPicked(B, () =>
				{
					if (B.SelectedIndex >= 0 && B.SelectedIndex != Applied)
					{
						Applied = B.SelectedIndex;
						O.SetText!(_current, Values[Applied]);
						RefreshStates();
					}
				});
				return Pair(T, O, B);
			}
			case OptKind.Text:
			{
				var B = new TextBox { Text = O.GetText!(_current) };
				B.TextChanged += (_, _) => { if (!_loading) { O.SetText!(_current, B.Text); Preview(); } };
				return Pair(T, O, B);
			}
			case OptKind.Folder:
			case OptKind.File:
				return Pair(T, O, PathEditor(O));
			case OptKind.Incremental:
			{
				if (_eng?.Launcher.ThreeWayIncremental == true)
				{
					var Names = new Dictionary<string, string> { ["Off"] = "Off", ["ModifiedOnly"] = "Modified only (legacy, faster, unreliable)", ["ModifiedAndDependencies"] = "Modified and dependencies" };
					var B = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
					B.Items.AddRange(Names.Values.Cast<object>().ToArray());
					B.SelectedItem = Names.GetValueOrDefault(O.GetText!(_current), "Off");
					var Applied = B.SelectedItem as string;
					WhenPicked(B, () =>
					{
						if (B.SelectedItem is string V && V != Applied)
						{
							Applied = V;
							O.SetText!(_current, Names.First(K => K.Value == V).Key);
							RefreshStates();
						}
					});
					return Pair(T, O, B);
				}
				var C = new CheckBox { Text = O.Label, Checked = O.GetText!(_current) != "Off", AutoSize = true };
				C.CheckedChanged += (_, _) => { if (!_loading) { O.SetText!(_current, C.Checked ? "ModifiedAndDependencies" : "Off"); RefreshStates(); } };
				T.Controls.Add(C);
				T.SetColumnSpan(C, 2);
				return new Row { O = O, Editor = C };
			}
			case OptKind.Maps:
				return MapsRow(T, O);
			case OptKind.Target:
			{
				const string Default = "(Project default)";
				var Items = new List<string> { Default };
				if (_proj != null) { Items.AddRange(_proj.Targets.Where(X => X.Type is "Game" or "Client").Select(X => X.Name)); }
				return Pair(T, O, Combo(Items, _current.Target.Length == 0 ? Default : _current.Target, V => _current.Target = V == Default ? "" : V));
			}
			case OptKind.Platform:
			{
				var Items = _eng?.Configs.Keys.OrderBy(K => K).ToList() ?? new List<string> { "Win64" };
				var B = Combo(Items, _current.Platform, V =>
				{
					_current.Platform = V;
					if (_eng != null && _eng.Configs.TryGetValue(V, out var Have) && !Have.Contains(_current.Config)) { _current.Config = Have.Last(); }
					if (_eng?.Architectures.GetValueOrDefault(V)?.Contains(_current.Architecture) != true) { _current.Architecture = ""; }
					BeginInvoke(RebuildOptions);
				});
				return Pair(T, O, B);
			}
			case OptKind.Architecture:
			{
				const string Default = "Project Default";
				var Items = new List<string> { Default };
				if (_eng?.Architectures.TryGetValue(_current.Platform, out var Have) == true) { Items.AddRange(Have); }
				return Pair(T, O, Combo(Items, _current.Architecture.Length == 0 ? Default : _current.Architecture, V => _current.Architecture = V == Default ? "" : V));
			}
			case OptKind.Configuration:
			{
				var Items = _eng != null && _eng.Configs.TryGetValue(_current.Platform, out var Have) ? Have : new List<string> { "Development", "Shipping" };
				return Pair(T, O, Combo(Items, _current.Config, V => _current.Config = V));
			}
			case OptKind.InitialMap:
			{
				const string Default = "(Default)";
				var Items = new List<string> { Default };
				if (_proj != null) { Items.AddRange(_proj.Maps); }
				return Pair(T, O, Combo(Items, _current.Map.Length == 0 ? Default : _current.Map, V => _current.Map = V == Default ? "" : V));
			}
			default:
				return SchemeRow(T, O);
		}
	}

	Control PathEditor(OptionDef O)
	{
		var Panel_ = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 27, Margin = new Padding(3, 0, 3, 0) };
		Panel_.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		Panel_.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
		var Box = new TextBox { Text = O.GetText!(_current), Dock = DockStyle.Fill, Margin = new Padding(0, 2, 2, 0) };
		var Browse = new Button { Text = "...", Dock = DockStyle.Fill, Margin = Padding.Empty };
		Box.TextChanged += (_, _) => { if (!_loading) { O.SetText!(_current, Box.Text); Preview(); } };
		Browse.Click += (_, _) =>
		{
			if (O.Kind == OptKind.Folder)
			{
				using var D = new FolderBrowserDialog { Description = O.Label };
				if (D.ShowDialog(this) == DialogResult.OK) { Box.Text = D.SelectedPath; }
			}
			else
			{
				using var D = new OpenFileDialog { Title = O.Label };
				if (D.ShowDialog(this) == DialogResult.OK) { Box.Text = D.FileName; }
			}
		};
		Panel_.Controls.Add(Box, 0, 0);
		Panel_.Controls.Add(Browse, 1, 0);
		return Panel_;
	}

	Row MapsRow(TableLayoutPanel T, OptionDef O)
	{
		var Only = new CheckBox { Text = "Only the selected maps (off: the project's startup maps)", Checked = _current.CookMaps.Count > 0, AutoSize = true };
		var List_ = new CheckedListBox { Height = 90, CheckOnClick = true, IntegralHeight = false, Dock = DockStyle.Fill };
		foreach (var M in _proj?.Maps ?? new List<string>()) { List_.Items.Add(M, _current.CookMaps.Contains(M)); }
		var Saved = new List<string>(_current.CookMaps);
		Only.CheckedChanged += (_, _) =>
		{
			if (_loading) { return; }
			if (Only.Checked) { _current.CookMaps = new List<string>(Saved); }
			else { Saved = new List<string>(_current.CookMaps); _current.CookMaps = new List<string>(); }
			RefreshStates();
		};
		List_.ItemCheck += (_, Ev) =>
		{
			if (_loading) { return; }
			var Name = (string)List_.Items[Ev.Index];
			_current.CookMaps.Remove(Name);
			if (Ev.NewValue == CheckState.Checked) { _current.CookMaps.Add(Name); }
			Saved = new List<string>(_current.CookMaps);
			Preview();
		};
		var L = RowLabel(O.Label);
		T.Controls.Add(L);
		T.Controls.Add(Only);
		T.Controls.Add(List_);
		T.SetColumnSpan(List_, 2);
		return new Row { O = O, Editor = Only, Caption = L, Extra = List_, ExtraVisible = () => Only.Checked };
	}

	Row SchemeRow(TableLayoutPanel T, OptionDef O)
	{
		var B = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, MaxDropDownItems = 12 };
		var Hint = new Label { AutoSize = false, Height = 46, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 2, 0, 0) };
		B.DrawItem += (_, E) => DrawScheme(B, E);
		B.DropDown += (_, _) => SizeSchemeList(B);
		WhenPicked(B, () => ApplyScheme(B));
		_schemeBox = B;
		_schemeHint = Hint;
		var L = RowLabel(O.Label);
		B.Dock = DockStyle.Fill;
		T.Controls.Add(L);
		T.Controls.Add(B);
		T.Controls.Add(new Label { AutoSize = false, Width = 1, Height = 1 });
		T.Controls.Add(Hint);
		return new Row { O = O, Editor = B, Caption = L, Extra = Hint, ExtraVisible = () => true };
	}

	/// <summary>Only draws. Each item shows its description or the reason it is blocked, so nothing has to change on hover.</summary>
	void DrawScheme(ComboBox B, DrawItemEventArgs E)
	{
		if (E.Index < 0 || E.Index >= B.Items.Count) { return; }
		var S = (SchemeState)B.Items[E.Index]!;
		var InField = (E.State & DrawItemState.ComboBoxEdit) != 0;
		var Hot = !InField && S.Available && (E.State & DrawItemState.Selected) != 0;
		using (var Back = new SolidBrush(Hot ? SystemColors.Highlight : SystemColors.Window)) { E.Graphics.FillRectangle(Back, E.Bounds); }
		const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
		var NameColor = !S.Available ? SystemColors.GrayText : Hot ? SystemColors.HighlightText : SystemColors.ControlText;
		var NameWidth = InField ? E.Bounds.Width - 4 : _schemeNameWidth;
		TextRenderer.DrawText(E.Graphics, S.Info.Name, B.Font, new Rectangle(E.Bounds.X + 2, E.Bounds.Y, NameWidth, E.Bounds.Height), NameColor, Flags);
		if (!InField)
		{
			var Detail = S.Available ? (S.Note.Length > 0 ? S.Note : S.Info.Tip) : S.Reason;
			var Left = E.Bounds.X + 2 + _schemeNameWidth;
			TextRenderer.DrawText(E.Graphics, Detail, B.Font, new Rectangle(Left, E.Bounds.Y, Math.Max(0, E.Bounds.Right - Left - 4), E.Bounds.Height), Hot ? SystemColors.HighlightText : SystemColors.GrayText, Flags);
		}
		else if ((E.State & DrawItemState.Focus) != 0 && (E.State & DrawItemState.NoFocusRect) == 0) { E.DrawFocusRectangle(); }
	}

	/// <summary>Makes the open list wide enough for the name column and every blocked reason.</summary>
	void SizeSchemeList(ComboBox B)
	{
		var States = B.Items.Cast<SchemeState>().ToList();
		if (States.Count == 0) { return; }
		int Width(string S) => TextRenderer.MeasureText(S, B.Font).Width;
		_schemeNameWidth = States.Max(S => Width(S.Info.Name)) + 12;
		var Reasons = States.Max(S => S.Available ? 0 : Width(S.Reason));
		var Tips = States.Max(S => S.Available ? Width(S.Info.Tip) : 0);
		var Wanted = _schemeNameWidth + Math.Max(Reasons, Math.Min(Tips, 420)) + 28;
		B.DropDownWidth = Math.Max(B.Width, Math.Min(Wanted, Screen.FromControl(B).WorkingArea.Width - 40));
	}

	void ApplyScheme(ComboBox B)
	{
		if (B != _schemeBox || B.SelectedItem is not SchemeState S) { return; }
		if (!S.Available)
		{
			// A blocked scheme cannot be picked: go back to the current one and say why.
			_loading = true;
			B.SelectedItem = B.Items.Cast<SchemeState>().FirstOrDefault(X => X.Info.Id == _current.ContentScheme);
			_loading = false;
			if (_schemeHint != null) { _schemeHint.Text = $"{S.Info.Name} is not available: {S.Reason}"; }
			return;
		}
		if (S.Info.Id == _current.ContentScheme) { ShowSchemeHint(S); return; }
		_current.ContentScheme = S.Info.Id;
		RefreshStates();
	}

	void ShowSchemeHint(SchemeState S)
	{
		if (_schemeHint == null) { return; }
		var Text_ = S.Available ? (S.Note.Length > 0 ? S.Note : S.Info.Tip) : $"Not available: {S.Reason}";
		if (_schemeHint.Text != Text_) { _schemeHint.Text = Text_; }
	}

	/// <param name="Raw">The project's own settings. The list is judged with the profile's Zen Store choice applied, so setting it to Off unblocks Loose Files.</param>
	void RefreshSchemes(ProjectSettings Raw)
	{
		if (_schemeBox == null || _eng == null || _schemeBox.DroppedDown) { return; }
		var Info = _eng.Launcher;
		var CanSkipZen = _eng.HasCookSwitch("SkipZenStore");
		var Choice = _current.Cook && (CanSkipZen || !_current.ZenStore.Equals("Off", StringComparison.OrdinalIgnoreCase)) ? _current.ZenStore : "Project";
		var Settings = Raw.WithZenStore(Choice);
		var States = Info.Schemes.Select(S => LauncherScan.Evaluate(Info, S.Id, Settings, _current.Platform, _current.Config, CanSkipZen)).ToList();
		if (States.All(S => S.Info.Id != _current.ContentScheme)) { States.Add(LauncherScan.Evaluate(Info, _current.ContentScheme, Settings, _current.Platform, _current.Config, CanSkipZen)); }
		_schemeBox.BeginUpdate();
		_schemeBox.Items.Clear();
		_schemeBox.Items.AddRange(States.Cast<object>().ToArray());
		var Selected = States.First(S => S.Info.Id == _current.ContentScheme);
		_schemeBox.SelectedItem = Selected;
		_schemeBox.EndUpdate();
		ShowSchemeHint(Selected);
	}

	/// <summary>Applies the engine's and the scheme's rules to every row, without rebuilding them.</summary>
	void RefreshStates()
	{
		if (_loading) { return; }
		var Settings = _eng != null ? ProjectSettings.Read(_eng, _proj, _current.Platform) : null;
		var B = _eng != null && Settings != null
			? LauncherScan.Resolve(_eng.Launcher, _current, Settings, _eng.HasCookSwitch("SkipZenStore")).B
			: new SchemeBehavior(true, false, false, false, false, false, false, false);
		var Ctx = new OptionContext(_current, B);
		_loading = true;
		_options.SuspendLayout();
		if (Settings != null) { RefreshSchemes(Settings); }
		foreach (var (Header, Rows) in _layout)
		{
			var Any = false;
			foreach (var R in Rows)
			{
				var Visible = R.O.Visible(Ctx);
				Any |= Visible;
				R.Editor.Visible = Visible;
				if (R.Caption != null) { R.Caption.Visible = Visible; }
				if (R.Extra != null) { R.Extra.Visible = Visible && (R.ExtraVisible?.Invoke() ?? true); }
				var FlagsOk = _eng == null || R.O.Flags.Length == 0 || R.O.Flags.Any(_eng.Has);
				var SwitchesOk = _eng == null || R.O.CookSwitches.Length == 0 || R.O.CookSwitches.Any(_eng.HasCookSwitch);
				var Supported = FlagsOk && SwitchesOk;
				var Enabled = Supported && R.O.Enabled(Ctx);
				R.Editor.Enabled = Enabled;
				if (R.Extra is CheckedListBox) { R.Extra.Enabled = Enabled; }
				var Missing = Supported ? "" : !FlagsOk ? $"UE {_eng!.Version} has no -{string.Join(" or -", R.O.Flags)}" : $"UE {_eng!.Version}'s cook commandlet has no -{string.Join(" or -", R.O.CookSwitches)}";
				if (R.Editor is CheckBox C && R.O.Kind != OptKind.Maps)
				{
					var Wanted = Supported ? R.O.Label : $"{R.O.Label}  (not in UE {_eng!.Version})";
					if (C.Text != Wanted) { C.Text = Wanted; }
				}
				else if (R.Caption is Label L)
				{
					L.ForeColor = Supported ? SystemColors.ControlText : SystemColors.GrayText;
				}
				if (R.O.Kind == OptKind.Tri && R.Editor is ComboBox Tri && Settings != null && R.O.ProjectValue != null)
				{
					var Wanted = $"Project setting ({(R.O.ProjectValue(Settings) ? "on" : "off")})";
					if ((string)Tri.Items[0]! != Wanted)
					{
						var I = Tri.SelectedIndex;
						Tri.Items[0] = Wanted;
						Tri.SelectedIndex = I;
					}
				}
				_tips.SetToolTip(R.Editor, Missing.Length > 0 ? Missing : R.O.Tip);
			}
			Header.Visible = Any;
		}
		_options.ResumeLayout();
		_loading = false;
		Preview();
	}

	void FillFlags()
	{
		var Was = _loading;
		_loading = true;
		_flags.BeginUpdate();
		_flags.Items.Clear();
		if (_eng != null)
		{
			var Q = _flagSearch.Text.Trim();
			foreach (var F in _eng.Flags.Where(F => Q.Length == 0 || F.Name.Contains(Q, StringComparison.OrdinalIgnoreCase) || F.Description.Contains(Q, StringComparison.OrdinalIgnoreCase)))
			{
				var I = new ListViewItem(F.Name) { Checked = _current.Flags.Any(X => X.TrimStart('-').Split('=')[0].Equals(F.Name, StringComparison.OrdinalIgnoreCase)) };
				I.SubItems.Add(F.Description);
				_flags.Items.Add(I);
			}
		}
		_flags.EndUpdate();
		_loading = Was;
	}

	void Preview()
	{
		Remember();
		if (_proj == null || _eng == null) { _command.Text = "Open a project and select an engine."; _build.Enabled = false; _why.Text = "Open a project and select an engine."; return; }
		var Plan = UatCommand.Make(_current, _proj, _eng);
		var S = new StringBuilder($"\"{_eng.RunUat}\" {Plan.CommandLine}");
		void Block(string Title, List<string> Lines)
		{
			if (Lines.Count == 0) { return; }
			S.Append("\r\n\r\n").Append(Title).Append(":\r\n").Append(string.Join("\r\n", Lines));
		}
		Block("Cannot build", Plan.Errors);
		Block("Warnings", Plan.Warnings);
		Block("Notes", Plan.Notes);
		_command.Text = S.ToString();
		_command.Select(0, 0);
		_build.Enabled = _runner == null && Plan.Errors.Count == 0;
		_build.BackColor = _build.Enabled ? Color.FromArgb(0, 120, 212) : SystemColors.ControlDark;
		_why.Text = _runner != null ? "" : Plan.Errors.Count > 0 ? "Cannot build: " + Plan.Errors[0] : "";
		_tips.SetToolTip(_why, string.Join("\n", Plan.Errors));
	}

	// ---------------------------------------------------------------- build

	async Task StartBuild()
	{
		if (_proj == null || _eng == null || _runner != null) { return; }
		_logLines.Clear(); _log.Clear();
		_runner = new BuildRunner();
		_runner.Line += S => _incoming.Enqueue(S);
		_runner.Phase += S => BeginInvoke(() => { _phaseName = S; ShowPhase(); });
		_build.Enabled = false; _cancel.Enabled = true; _started = DateTime.Now; _phaseName = "Starting"; _clock.Start();
		try
		{
			var R = await _runner.RunAsync(_current.Clone(), _proj, _eng);
			DrainLog();
			Notify(R.Summary());
		}
		catch (Exception Ex) when (Ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { _phaseName = "Failed"; Notify(Ex.Message); }
		finally { _clock.Stop(); _runner = null; _cancel.Enabled = false; Preview(); ShowPhase(); }
	}

	void Notify(string Message)
	{
		if (_selfTest == null) { MessageBox.Show(this, Message, "UE Launcher"); }
		else { File.AppendAllText(Path.Combine(_selfTest[0], "selftest.txt"), Message + "\r\n"); }
	}

	void Snap(string Name)
	{
		Application.DoEvents();
		using var Image = new Bitmap(Width, Height);
		DrawToBitmap(Image, new Rectangle(0, 0, Width, Height));
		Image.Save(Path.Combine(_selfTest![0], Name + ".png"));
	}

	void SnapOptions(string Name)
	{
		Application.DoEvents();
		var Table = _options.Controls[0];
		using var Image = new Bitmap(Table.Width, Table.Height);
		Table.DrawToBitmap(Image, new Rectangle(0, 0, Table.Width, Table.Height));
		Image.Save(Path.Combine(_selfTest![0], Name + ".png"));
	}

	static async Task<bool> Until(Func<bool> Condition, int Seconds)
	{
		var End = DateTime.Now.AddSeconds(Seconds);
		while (!Condition() && DateTime.Now < End) { await Task.Delay(100); }
		return Condition();
	}

	string SchemeList() => _schemeBox == null ? "(none)" : string.Join(", ", _schemeBox.Items.Cast<SchemeState>().Select(S => S.Info.Name + (S.Available ? "" : " [greyed]")));

	/// <summary>Opens the real dropdown and moves over every item with the same window messages the mouse produces.</summary>
	async Task<string> HoverTest()
	{
		var B = _schemeBox;
		if (B == null) { return "no scheme box"; }
		B.Focus();
		B.DroppedDown = true;
		await Task.Delay(200);
		if (!B.DroppedDown) { return "the list did not open"; }
		var Info = new ComboInfo { Size = Marshal.SizeOf<ComboInfo>() };
		if (!GetComboBoxInfo(B.Handle, ref Info) || Info.List == IntPtr.Zero) { return "no list window"; }
		for (var Pass = 0; Pass < 2; ++Pass)
		{
			for (var I = 0; I < B.Items.Count; ++I)
			{
				SendMessage(Info.List, LbSetCurSel, I, IntPtr.Zero);
				SendMessage(Info.List, WmMouseMove, IntPtr.Zero, ((B.ItemHeight * I + B.ItemHeight / 2) << 16) | 20);
				await Task.Delay(80);
				if (!B.DroppedDown) { return $"COLLAPSED while hovering \"{((SchemeState)B.Items[I]!).Info.Name}\" (pass {Pass + 1})"; }
			}
		}
		// a picture of the open list, hovering an item that can be picked
		var Hover = B.Items.Cast<SchemeState>().ToList().FindIndex(S => S.Info.Id == "DevelopmentPackage");
		if (Hover >= 0) { SendMessage(Info.List, LbSetCurSel, Hover, IntPtr.Zero); }
		await Task.Delay(100);
		GetWindowRect(Info.List, out var R);
		using (var Image = new Bitmap(Math.Max(1, R.Right - R.Left), Math.Max(1, R.Bottom - R.Top)))
		{
			using (var G = Graphics.FromImage(Image))
			{
				var Dc = G.GetHdc();
				PrintWindow(Info.List, Dc, 0);
				G.ReleaseHdc(Dc);
			}
			Image.Save(Path.Combine(_selfTest![0], "2a_dropdown.png"));
		}
		var Before = _current.ContentScheme;
		B.DroppedDown = false;	// the same as clicking away
		await Task.Delay(200);
		return $"stayed open over all {B.Items.Count} items, twice; after closing without a click: profile={_current.ContentScheme} (was {Before}), box={(B.SelectedItem as SchemeState)?.Info.Id}";
	}

	/// <summary>Opens the list, hovers one scheme, then clicks it or presses Escape.</summary>
	async Task<string> PickTest(string SchemeId, bool Cancel)
	{
		var B = _schemeBox!;
		var Index = B.Items.Cast<SchemeState>().ToList().FindIndex(S => S.Info.Id == SchemeId);
		if (Index < 0) { return $"{SchemeId} is not listed"; }
		B.Focus();
		B.DroppedDown = true;
		await Task.Delay(200);
		var Info = new ComboInfo { Size = Marshal.SizeOf<ComboInfo>() };
		GetComboBoxInfo(B.Handle, ref Info);
		var At = (IntPtr)(((B.ItemHeight * Index + B.ItemHeight / 2) << 16) | 20);
		SendMessage(Info.List, LbSetCurSel, Index, IntPtr.Zero);
		SendMessage(Info.List, WmMouseMove, IntPtr.Zero, At);
		await Task.Delay(100);
		var OpenBefore = B.DroppedDown;
		if (Cancel) { SendMessage(B.Handle, WmKeyDown, VkEscape, IntPtr.Zero); }
		else
		{
			SendMessage(Info.List, WmLButtonDown, 1, At);
			SendMessage(Info.List, WmLButtonUp, IntPtr.Zero, At);
		}
		await Task.Delay(300);
		var Closed = !B.DroppedDown;
		if (!Closed) { B.DroppedDown = false; await Task.Delay(150); }
		return $"{(Cancel ? "Escape on" : "click")} {SchemeId}: open while hovering={OpenBefore}, closed after={Closed}, profile={_current.ContentScheme}, box={(B.SelectedItem as SchemeState)?.Info.Id}, hint=\"{_schemeHint?.Text}\"";
	}

	Control? Editor(string Id) => _layout.SelectMany(S => S.Rows).FirstOrDefault(R => R.O.Id == Id)?.Editor;

	string Describe(string Label) => $"{Label}: project={_proj?.Name} engine={_eng?.Version} profile={_current.Name} scheme={_current.ContentScheme} chunks={_current.Chunks} cookArgs='{_current.ExtraCook}' compress={_current.Compress} zen={_current.ZenStore} revert={_revert.Enabled}";

	/// <summary>Three app starts share one state file: "set" changes things, "check" must find them after a restart, "after" must find the reverted profile.</summary>
	async Task RunMemoryTest()
	{
		Directory.CreateDirectory(_selfTest![0]);
		try
		{
			await Until(() => _eng != null, 60);
			await Task.Delay(400);
			Profile Named(string N) => _profiles.Items.Cast<Profile>().First(P => P.Name == N);
			if (_memoryPhase == "set")
			{
				var Second = EngineScanner.NormalizeRoot(_selfTest[1]);
				SelectEngine(Second, false);
				await Until(() => _eng?.Root == Second, 60);
				await Task.Delay(300);
				_profiles.SelectedItem = Named("Development");
				await Task.Delay(200);
				((CheckBox)Editor("Chunks")!).Checked = true;
				((TextBox)Editor("ExtraCook")!).Text = "-fromMemoryTest";
				((ComboBox)Editor("Compress")!).SelectedIndex = 1;
				((ComboBox)Editor("ZenStore")!).SelectedIndex = 2;
				_schemeBox!.SelectedItem = _schemeBox.Items.Cast<SchemeState>().First(S => S.Info.Id == "DevelopmentPackage");
				await Task.Delay(200);
				Notify(Describe("set"));
			}
			else if (_memoryPhase == "check")
			{
				Notify(Describe("after restart"));
				Notify($"widgets: engine box={(_engine.SelectedItem as EngineItem)?.Root} profile box={(_profiles.SelectedItem as Profile)?.Name} scheme box={(_schemeBox?.SelectedItem as SchemeState)?.Info.Id} chunks={((CheckBox)Editor("Chunks")!).Checked} cookArgs={((TextBox)Editor("ExtraCook")!).Text} compress={((ComboBox)Editor("Compress")!).SelectedItem}");
				_profiles.SelectedItem = Named("Shipping");
				await Task.Delay(200);
				Notify(Describe("switched to Shipping"));
				_profiles.SelectedItem = Named("Development");
				await Task.Delay(200);
				Notify(Describe("back to Development"));
				_revert.PerformClick();
				await Task.Delay(200);
				Notify(Describe("reverted"));
			}
			else { Notify(Describe("restart after revert")); }
		}
		catch (Exception Ex) { Notify("memory test error: " + Ex); }
		Close();
	}

	async Task RunSelfTest()
	{
		var Out = _selfTest![0];
		Directory.CreateDirectory(Out);
		File.WriteAllText(Path.Combine(Out, "selftest.txt"), "");
		try
		{
			Notify($"engine scanned: {await Until(() => _eng != null, 60)} ({_eng?.Version})");
			Notify("schemes: " + SchemeList());
			_tabs.SelectedIndex = 0; Snap("1_command"); SnapOptions("1_options");
			_tabs.SelectedIndex = 2; Snap("2_flags");
			var First = _eng!.Root;
			var Second = EngineScanner.NormalizeRoot(_selfTest[1]);
			SelectEngine(Second, false);
			Notify($"second engine: {await Until(() => _eng?.Root == Second, 60)} ({_eng?.Version} {_eng?.Kind})");
			await Task.Delay(300);
			Notify("schemes: " + SchemeList());
			_tabs.SelectedIndex = 0; Snap("3_second_engine"); SnapOptions("3_options");
			SelectEngine(First, false);
			await Until(() => _eng?.Root == First, 60);
			await Task.Delay(300);
			Notify("hover: " + await HoverTest());
			Notify("pick: " + await PickTest("DevelopmentPackage", false));
			Notify("pick: " + await PickTest("LooseFiles", false));
			Notify("pick: " + await PickTest("ZenStreaming", true));
			Notify("pick: " + await PickTest("PakFiles", false));
			Snap("2b_after_picks");

			// the wheel over closed dropdowns: values stay, the options scroll
			var Config = (ComboBox)Editor("Configuration")!;
			var (ConfigBefore, SchemeBefore, ProfileBefore, ScrollBefore) = (Config.SelectedIndex, _schemeBox!.SelectedIndex, _profiles.SelectedIndex, _options.VerticalScroll.Value);
			foreach (var Box in new[] { Config, _schemeBox, _profiles })
			{
				for (var I = 0; I < 3; ++I) { PostMessage(Box.Handle, WmMouseWheel, unchecked((IntPtr)(-120 << 16)), IntPtr.Zero); }
				await Task.Delay(150);
			}
			Notify($"wheel: config same={Config.SelectedIndex == ConfigBefore}, scheme same={_schemeBox.SelectedIndex == SchemeBefore}, profile same={_profiles.SelectedIndex == ProfileBefore}, options scrolled {ScrollBefore} -> {_options.VerticalScroll.Value}");
			_options.AutoScrollPosition = Point.Empty;

			// Zen Store override: Loose Files is blocked while the project has Zen Store on, and selectable once the override is Off
			var Zen = Editor("ZenStore") as ComboBox;
			var Loose = _schemeBox?.Items.Cast<SchemeState>().FirstOrDefault(S => S.Info.Id == "LooseFiles");
			Notify($"zen: row visible={Zen?.Visible} enabled={Zen?.Enabled} first entry='{Zen?.Items[0]}'; Loose Files available={Loose?.Available} reason='{Loose?.Reason}'");
			if (Zen != null)
			{
				Zen.SelectedIndex = 2;
				await Task.Delay(300);
				Loose = _schemeBox?.Items.Cast<SchemeState>().FirstOrDefault(S => S.Info.Id == "LooseFiles");
				Notify($"zen: after Off, Loose Files available={Loose?.Available}");
				Notify("pick: " + await PickTest("LooseFiles", false));
				Notify($"zen: command has -SkipZenStore={_command.Text.Contains("-SkipZenStore", StringComparison.Ordinal)}, -pak={_command.Text.Contains(" -pak", StringComparison.Ordinal)}, -zenstore={_command.Text.Contains(" -zenstore", StringComparison.Ordinal)}");
				Snap("2c_loose_zen_off");
				Zen.SelectedIndex = 0;
				await Task.Delay(200);
			}

			// picking a blocked scheme must not change the profile
			var Before = _current.ContentScheme;
			if (_schemeBox != null)
			{
				var Blocked = _schemeBox.Items.Cast<SchemeState>().FirstOrDefault(S => !S.Available);
				if (Blocked != null)
				{
					_schemeBox.SelectedItem = Blocked;
					Notify($"picked blocked {Blocked.Info.Name}: profile still {_current.ContentScheme} (was {Before}); hint: {_schemeHint?.Text}");
				}
				var Pack = _schemeBox.Items.Cast<SchemeState>().FirstOrDefault(S => S.Info.Id == "DevelopmentPackage");
				if (Pack != null)
				{
					_schemeBox.SelectedItem = Pack;
					Notify($"picked {Pack.Info.Name}: profile {_current.ContentScheme}; command has -package: {_command.Text.Contains(" -package", StringComparison.Ordinal)}");
					_schemeBox.SelectedItem = _schemeBox.Items.Cast<SchemeState>().First(S => S.Info.Id == Before);
				}
			}

			_current = Profile.Load(_selfTest[2]);
			RebuildOptions();
			_build.PerformClick();
			Notify($"build started: {await Until(() => _runner != null && _logLines.Count > 3, 60)}");
			await Task.Delay(12000);
			Snap("4_building");
			AppendLog(new[] { "LogCook: Display: a normal line", "LogCook: Warning: something odd", "LogCook: Error: something broke", "LogCook: Display: 0 errors, 0 warnings", "LogX: Verbose: quiet detail", "********** COOK COMMAND STARTED **********", "[UELauncher] note: a launcher note", "BUILD SUCCESSFUL", "BUILD FAILED" });
			Snap("4b_colors");
			_cancel.PerformClick();
			Notify($"cancel finished: {await Until(() => _runner == null, 60)}, phase {_phaseName}");
			Snap("5_cancelled");
		}
		catch (Exception Ex) { Notify("self-test error: " + Ex); }
		Close();
	}

	void ShowPhase() => _phase.Text = _runner == null ? _phaseName : $"{_phaseName}  {DateTime.Now - _started:hh\\:mm\\:ss}";
	static bool Important(string S) => S.Contains("error", StringComparison.OrdinalIgnoreCase) || S.Contains("warning", StringComparison.OrdinalIgnoreCase) || S.StartsWith("[UELauncher]", StringComparison.Ordinal);

	enum LogKind { Normal, Error, Warning, Note, Phase, Good, Bad, Quiet }
	static readonly System.Text.RegularExpressions.Regex ErrorRx = new(@"\b(error|fatal|exception|failed)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
	static readonly System.Text.RegularExpressions.Regex WarningRx = new(@"\bwarning\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
	static readonly System.Text.RegularExpressions.Regex ZeroRx = new(@"\b0 (errors?|warnings?)\b|\b(errors?|warnings?)\s*[:=]\s*0\b|\bno (errors?|warnings?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

	static LogKind Classify(string S)
	{
		if (S.StartsWith("[UELauncher]", StringComparison.Ordinal))
		{
			return S.Contains("] error", StringComparison.Ordinal) || S.Contains("FAILED", StringComparison.Ordinal) ? LogKind.Bad
				: S.Contains("] warning", StringComparison.Ordinal) ? LogKind.Warning
				: S.Contains("SUCCESSFUL", StringComparison.Ordinal) ? LogKind.Good : LogKind.Note;
		}
		if (S.Contains("BUILD SUCCESSFUL", StringComparison.Ordinal)) { return LogKind.Good; }
		if (S.Contains("BUILD FAILED", StringComparison.Ordinal)) { return LogKind.Bad; }
		if (S.Contains("**********", StringComparison.Ordinal)) { return LogKind.Phase; }
		if (S.Contains(": Verbose:", StringComparison.Ordinal) || S.Contains(": VeryVerbose:", StringComparison.Ordinal) || S.Contains(": Log:", StringComparison.Ordinal)) { return LogKind.Quiet; }
		var Zero = ZeroRx.IsMatch(S);
		if (!Zero && ErrorRx.IsMatch(S)) { return LogKind.Error; }
		if (!Zero && WarningRx.IsMatch(S)) { return LogKind.Warning; }
		return LogKind.Normal;
	}

	static (Color Color, bool Bold) Style(LogKind K) => K switch
	{
		LogKind.Error => (Color.FromArgb(200, 30, 30), false),
		LogKind.Bad => (Color.FromArgb(200, 30, 30), true),
		LogKind.Warning => (Color.FromArgb(176, 112, 0), false),
		LogKind.Note => (Color.FromArgb(0, 102, 204), false),
		LogKind.Phase => (Color.FromArgb(0, 128, 96), true),
		LogKind.Good => (Color.FromArgb(0, 140, 40), true),
		LogKind.Quiet => (Color.FromArgb(130, 130, 130), false),
		_ => (SystemColors.WindowText, false)
	};

	/// <summary>Appends lines in their colors; runs of the same kind go in as one piece.</summary>
	void AppendLog(IEnumerable<string> Lines)
	{
		var Follow = _log.TextLength == 0 || _log.GetCharIndexFromPosition(new Point(2, Math.Max(0, _log.ClientSize.Height - 2))) >= _log.TextLength - 400;
		var Run = new StringBuilder();
		LogKind? Current = null;
		void Flush()
		{
			if (Current == null || Run.Length == 0) { return; }
			var (Color_, Bold) = Style(Current.Value);
			_log.SelectionStart = _log.TextLength;
			_log.SelectionLength = 0;
			_log.SelectionColor = Color_;
			_log.SelectionFont = Bold ? _logBold : _log.Font;
			_log.AppendText(Run.ToString());
			Run.Clear();
		}
		foreach (var Line in Lines)
		{
			var Kind = Classify(Line);
			if (Current != Kind) { Flush(); Current = Kind; }
			Run.Append(Line).Append('\n');
		}
		Flush();
		if (Follow) { SendMessage(_log.Handle, 0x0115, (IntPtr)7, IntPtr.Zero); }	// WM_VSCROLL, SB_BOTTOM
	}

	void DrainLog()
	{
		if (_incoming.IsEmpty) { return; }
		var Fresh = new List<string>();
		while (_incoming.TryDequeue(out var S)) { _logLines.Add(S); Fresh.Add(S); }
		if (_logLines.Count > 60000) { _logLines.RemoveRange(0, 20000); RefillLog(); return; }
		AppendLog(_errorsOnly.Checked ? Fresh.Where(Important) : Fresh);
	}

	void RefillLog()
	{
		_log.Clear();
		AppendLog(_errorsOnly.Checked ? _logLines.Where(Important) : _logLines);
	}

	string? Prompt(string Title, string Value)
	{
		using var F = new Form { Text = Title, ClientSize = new Size(340, 76), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
		var B = new TextBox { Text = Value, Left = 8, Top = 8, Width = 324 };
		var Ok = new Button { Text = "OK", Left = 256, Top = 40, DialogResult = DialogResult.OK };
		F.Controls.AddRange(new Control[] { B, Ok });
		F.AcceptButton = Ok;
		return F.ShowDialog(this) == DialogResult.OK ? B.Text : null;
	}
}
