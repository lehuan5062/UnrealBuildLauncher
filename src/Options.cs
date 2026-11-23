namespace UELauncher;

public enum OptGroup { General, Content, Release, Cook, AdvancedCook, Build, Directory, Deploy, Deprecated, Advanced }
public enum OptKind { Check, Tri, Text, Folder, File, Incremental, Maps, Target, Platform, Architecture, Configuration, Scheme, InitialMap }

public sealed record OptionContext(Profile P, SchemeBehavior B);

/// <summary>One option. Epic is the Add...Property call of the engine's Project Launcher that shows it; EpicKey is its label key in the launcher's extensions.</summary>
public sealed class OptionDef
{
	public required string Id { get; init; }
	public required OptGroup Group { get; init; }
	public required OptKind Kind { get; init; }
	public required string Label { get; init; }
	public string? Epic { get; init; }
	public string? EpicKey { get; init; }
	public string[] Flags { get; init; } = Array.Empty<string>();

	/// <summary>Switches of the cook commandlet this option needs, passed through -AdditionalCookerOptions. The engine scan says which exist.</summary>
	public string[] CookSwitches { get; init; } = Array.Empty<string>();

	/// <summary>For a Project / On / Off option: what the project itself says, shown in the "Project setting" entry.</summary>
	public Func<ProjectSettings, bool>? ProjectValue { get; init; }
	public string Tip { get; init; } = "";
	public Func<Profile, bool>? GetBool { get; init; }
	public Action<Profile, bool>? SetBool { get; init; }
	public Func<Profile, string>? GetText { get; init; }
	public Action<Profile, string>? SetText { get; init; }
	public Func<OptionContext, bool> Visible { get; init; } = _ => true;
	public Func<OptionContext, bool> Enabled { get; init; } = _ => true;
}

public sealed class LayoutSection
{
	public required string Title { get; init; }
	public required OptGroup Group { get; init; }
	public List<OptionDef> Items { get; } = new();
}

public static class OptionCatalog
{
	// The visibility rules of Epic's GenericProfileTreeBuilder.
	static bool ForPak(OptionContext C) => C.B.Pak;
	static bool ForZenPak(OptionContext C) => C.B.ZenPak;
	static bool ForCooked(OptionContext C) => !C.B.ZenPak && !C.B.CookOnTheFly && !C.B.PreStaged;
	static bool ForEnabledCooked(OptionContext C) => C.P.Cook && ForCooked(C);
	static bool ForContent(OptionContext C) => !C.B.ZenPak && !C.B.PreStaged;
	static bool ForCode(OptionContext C) => !C.B.PreStaged;
	static bool ForRelease(OptionContext C) => C.B.Submission;
	static bool ForDeploy(OptionContext C) => !C.B.CookOnTheFly && !C.B.Submission;
	static bool ForRun(OptionContext C) => !C.B.Submission;

	static OptionDef Check(string Id, OptGroup G, string Label, string[] Flags, Func<Profile, bool> Get, Action<Profile, bool> Set, string? Epic = null, string? Key = null, Func<OptionContext, bool>? Visible = null, Func<OptionContext, bool>? Enabled = null, string Tip = "") =>
		new() { Id = Id, Group = G, Kind = OptKind.Check, Label = Label, Flags = Flags, GetBool = Get, SetBool = Set, Epic = Epic, EpicKey = Key, Visible = Visible ?? (_ => true), Enabled = Enabled ?? (_ => true), Tip = Tip };

	static OptionDef Value(string Id, OptGroup G, OptKind Kind, string Label, string[] Flags, Func<Profile, string> Get, Action<Profile, string> Set, string? Epic = null, string? Key = null, Func<OptionContext, bool>? Visible = null, Func<OptionContext, bool>? Enabled = null, string Tip = "", string[]? Switches = null, Func<ProjectSettings, bool>? ProjectValue = null) =>
		new() { Id = Id, Group = G, Kind = Kind, Label = Label, Flags = Flags, GetText = Get, SetText = Set, Epic = Epic, EpicKey = Key, Visible = Visible ?? (_ => true), Enabled = Enabled ?? (_ => true), Tip = Tip, CookSwitches = Switches ?? Array.Empty<string>(), ProjectValue = ProjectValue };

	static OptionDef Special(string Id, OptKind Kind, string Label, string Epic, string[]? Flags = null) =>
		new() { Id = Id, Group = OptGroup.General, Kind = Kind, Label = Label, Epic = Epic, Flags = Flags ?? Array.Empty<string>() };

	const string Release = "AddSubmissionPackageProperty";
	static readonly string[] None = Array.Empty<string>();

	public static readonly OptionDef[] All =
	{
		Special("Target", OptKind.Target, "Target", "AddTargetProperty"),
		Special("Platform", OptKind.Platform, "Platform", "AddPlatformProperty"),
		Special("Architecture", OptKind.Architecture, "Game Architecture", "AddArchitectureProperty", new[] { "clientarchitecture" }),
		Special("Configuration", OptKind.Configuration, "Configuration", "AddConfigurationProperty"),
		Special("Scheme", OptKind.Scheme, "Content Scheme", "AddContentSchemeProperty"),

		Value("Compress", OptGroup.Content, OptKind.Tri, "Compress Pak Files", new[] { "compressed", "ForceUncompressed" }, P => P.Compress, (P, V) => P.Compress = V, "AddCompressPakFilesProperty", null, ForPak, Tip: "Project setting passes nothing. On passes -compressed. Off passes -ForceUncompressed, because UAT otherwise follows the project setting.", ProjectValue: S => S.Compressed),
		Value("ZenStore", OptGroup.Content, OptKind.Tri, "Zen Store", new[] { "zenstore" }, P => P.ZenStore, (P, V) => P.ZenStore = V, "AddCompressPakFilesProperty", null, ForEnabledCooked, Tip: "Project setting passes nothing. Off adds -SkipZenStore to the cooker, which cooks to loose files even if the project has Zen Store on, and makes Loose Files selectable. On passes -zenstore.", Switches: new[] { "SkipZenStore" }, ProjectValue: S => S.UseZenStore),
		Value("UseIoStore", OptGroup.Content, OptKind.Tri, "Use Io Store", new[] { "iostore", "skipiostore" }, P => P.UseIoStore, (P, V) => P.UseIoStore = V, "AddUseIoStoreProperty", "IoStoreLabel", ForPak, Tip: "Project setting passes nothing. On passes -iostore. Off passes -skipiostore, because UAT otherwise follows the project setting.", ProjectValue: S => S.UseIoStore),
		Check("Chunks", OptGroup.Content, "Generate Chunks", new[] { "manifests" }, P => P.Chunks, (P, V) => P.Chunks = V, "AddGenerateChunksProperty", null, ForPak),
		Check("Snapshot", OptGroup.Content, "Import Best Match Zen Snapshot", new[] { "snapshot" }, P => P.ImportZenSnapshot, (P, V) => P.ImportZenSnapshot = V, "AddImportZenSnapshotProperty", "ImportZenSnapshotLabel", ForContent),
		Value("ZenPakPath", OptGroup.Content, OptKind.Folder, "Zen Pak Streaming Path", new[] { "ZenWorkspaceSharePath" }, P => P.ZenPakStreamingPath, (P, V) => P.ZenPakStreamingPath = V, "AddZenPakStreamingPathProperty", null, ForZenPak),

		Check("CreateRelease", OptGroup.Release, "Create Release Version", new[] { "createreleaseversion" }, P => P.CreateRelease, (P, V) => P.CreateRelease = V, Release, null, ForRelease),
		Value("ReleaseName", OptGroup.Release, OptKind.Text, "Release Version Name", new[] { "createreleaseversion" }, P => P.ReleaseName, (P, V) => P.ReleaseName = V, Release, null, ForRelease, C => C.P.CreateRelease || C.P.CreatePatch),
		Check("EncryptIni", OptGroup.Release, "Encrypt ini files", new[] { "encryptinifiles" }, P => P.EncryptIni, (P, V) => P.EncryptIni = V, Release, null, ForRelease),
		Check("ForDistribution", OptGroup.Release, "This build is for distribution to the public", new[] { "distribution" }, P => P.ForDistribution, (P, V) => P.ForDistribution = V, Release, null, ForRelease),
		Check("Prereqs", OptGroup.Release, "Include an installer for prerequisites", new[] { "prereqs" }, P => P.Prereqs, (P, V) => P.Prereqs = V, Release, null, ForRelease),
		Check("BinaryConfig", OptGroup.Release, "Make a binary config file for faster runtime settings", new[] { "makebinaryconfig" }, P => P.BinaryConfig, (P, V) => P.BinaryConfig = V, Release, null, ForRelease),
		Check("CreatePatch", OptGroup.Release, "Create Patch", new[] { "generatepatch" }, P => P.CreatePatch, (P, V) => P.CreatePatch = V, Release, null, ForRelease, C => !C.P.CreateDlc),
		Check("CreateDlc", OptGroup.Release, "Create DLC", new[] { "dlcname" }, P => P.CreateDlc, (P, V) => P.CreateDlc = V, Release, null, ForRelease, C => !C.P.CreatePatch),
		Value("BasedOn", OptGroup.Release, OptKind.Text, "Based On Release Version Name", new[] { "basedonreleaseversion" }, P => P.BasedOnRelease, (P, V) => P.BasedOnRelease = V, Release, null, C => C.B.Submission && (C.P.CreateDlc || C.P.CreatePatch)),
		Value("Original", OptGroup.Release, OptKind.Text, "Original Release Version Name", new[] { "originalreleaseversion" }, P => P.OriginalRelease, (P, V) => P.OriginalRelease = V, Release, null, C => C.B.Submission && C.P.CreatePatch),
		Check("PatchLevel", OptGroup.Release, "Add Patch Level (not generally needed)", new[] { "AddPatchLevel" }, P => P.AddPatchLevel, (P, V) => P.AddPatchLevel = V, Release, null, C => C.B.Submission && C.P.CreatePatch),
		Value("DlcName", OptGroup.Release, OptKind.Text, "DLC Name", new[] { "dlcname" }, P => P.DlcName, (P, V) => P.DlcName = V, Release, null, C => C.B.Submission && C.P.CreateDlc),
		Check("DlcEngine", OptGroup.Release, "DLC Includes Engine Content", new[] { "DLCIncludeEngineContent" }, P => P.DlcIncludeEngine, (P, V) => P.DlcIncludeEngine = V, Release, null, C => C.B.Submission && C.P.CreateDlc),

		Check("Cook", OptGroup.Cook, "Cook Content", new[] { "cook" }, P => P.Cook, (P, V) => P.Cook = V, "AddCookProperty", null, ForCooked),
		Value("Incremental", OptGroup.Cook, OptKind.Incremental, "Incremental Cook", new[] { "cookincremental", "iterativecooking", "iterate" }, P => P.IncrementalCook, (P, V) => P.IncrementalCook = V, "AddIncrementalCookProperty", null, ForEnabledCooked),
		Check("Unversioned", OptGroup.AdvancedCook, "Unversioned Cook", new[] { "VersionCookedContent" }, P => P.Unversioned, (P, V) => P.Unversioned = V, null, "UnversionedCookLabel", ForEnabledCooked, Tip: "UAT cooks unversioned by default. Off passes -VersionCookedContent."),
		new() { Id = "Maps", Group = OptGroup.AdvancedCook, Kind = OptKind.Maps, Label = "Maps To Cook", Flags = new[] { "map" }, Epic = "AddMapsToCookProperty", EpicKey = "MapsToCookLabel", Visible = ForEnabledCooked },
		Value("Cultures", OptGroup.AdvancedCook, OptKind.Text, "Cultures To Cook", new[] { "CookCultures" }, P => P.CookCultures, (P, V) => P.CookCultures = V, null, "CulturesToCookLabel", ForEnabledCooked, Tip: "For example en+fr. Empty uses the project's packaging settings."),
		Value("ExtraCook", OptGroup.AdvancedCook, OptKind.Text, "Additional Cooker Options", new[] { "AdditionalCookerOptions" }, P => P.ExtraCook, (P, V) => P.ExtraCook = V, "AddAdditionalCookerOptionsProperty", "AdditionalCookerOptionsLabel", C => C.P.Cook && !C.B.ZenPak && !C.B.PreStaged),
		Check("SkipEditor", OptGroup.AdvancedCook, "Don't include editor content in the build", new[] { "SkipCookingEditorContent" }, P => P.SkipEditorContent, (P, V) => P.SkipEditorContent = V, null, "SkipEditorContentLabel", ForEnabledCooked),

		Check("Build", OptGroup.Build, "Build the game", new[] { "build" }, P => P.Build, (P, V) => P.Build = V, "AddBuildProperty", null, ForCode),
		Check("ForceBuild", OptGroup.Build, "Build even if a pre-built target exists", new[] { "build" }, P => P.ForceBuild, (P, V) => P.ForceBuild = V, "AddForceBuildProperty", null, ForCode, C => C.P.Build, "Off: build only if there is no matching pre-built target. On: always pass -build."),
		Check("Clean", OptGroup.Build, "Clean first", new[] { "clean" }, P => P.Clean, (P, V) => P.Clean = V, null, null, ForCode, C => C.P.Build, "Passes -clean: rebuilds the game's code and cooks from scratch instead of reusing the last output."),
		Check("CompileEditor", OptGroup.Build, "Compile the project's editor first", new[] { "nocompileeditor" }, P => P.CompileEditor, (P, V) => P.CompileEditor = V, null, null, ForCode, C => C.P.Build, "The cook runs the editor's copy of your code. Skipped automatically while this project's editor is open."),
		Value("ExtraBuild", OptGroup.Build, OptKind.Text, "Extra build args", new[] { "ubtargs" }, P => P.ExtraBuild, (P, V) => P.ExtraBuild = V, null, null, ForCode, C => C.P.Build),

		Check("Archive", OptGroup.Directory, "Archive Build", new[] { "archive" }, P => P.Archive, (P, V) => P.Archive = V, "AddArchiveBuildProperty", null, ForCooked),
		Value("ArchiveRoot", OptGroup.Directory, OptKind.Folder, "Archive Directory", new[] { "archivedirectory" }, P => P.ArchiveRoot, (P, V) => P.ArchiveRoot = V, "AddArchiveBuildDirectoryProperty", null, ForCooked, C => C.P.Archive, "Empty uses the project's Saved\\StagedBuilds."),
		Value("Suffix", OptGroup.Directory, OptKind.Text, "Name suffix", None, P => P.Suffix, (P, V) => P.Suffix = V, null, null, ForCooked, C => C.P.Archive, "The archive folder is named <date>-<suffix>."),
		Check("Zip", OptGroup.Directory, "Zip for release", None, P => P.Zip, (P, V) => P.Zip = V, null, null, ForCooked, C => C.P.Archive),
		Value("StageDir", OptGroup.Directory, OptKind.Folder, "Stage Directory Override", new[] { "stagingdirectory" }, P => P.StageDir, (P, V) => P.StageDir = V, "AddStagingDirectoryProperty", null, ForCooked),
		Check("DebugFiles", OptGroup.Directory, "Include debug files (.pdb)", new[] { "nodebuginfo" }, P => P.IncludeDebugFiles, (P, V) => P.IncludeDebugFiles = V, null, null, ForCooked, Tip: "Off passes -nodebuginfo, so symbols are not copied to the stage."),

		Check("Deploy", OptGroup.Deploy, "Deploy To Device", new[] { "deploy" }, P => P.Deploy, (P, V) => P.Deploy = V, "AddDeployProperty", null, ForDeploy),
		Value("Devices", OptGroup.Deploy, OptKind.Text, "Target Device", new[] { "device" }, P => P.Devices, (P, V) => P.Devices = V, "AddTargetDeviceProperty", null, ForDeploy, C => C.P.Deploy, "Empty uses the default device. Otherwise Platform@Device, joined with +."),
		Check("Run", OptGroup.Deploy, "Run", new[] { "run" }, P => P.Run, (P, V) => P.Run = V, "AddRunProperty", null, ForRun),
		Value("InitialMap", OptGroup.Deploy, OptKind.InitialMap, "Initial Map", new[] { "map" }, P => P.Map, (P, V) => P.Map = V, "AddInitialMapProperty", null, ForRun, C => C.P.Run),
		Value("RunArgs", OptGroup.Deploy, OptKind.Text, "Additional Command Line", new[] { "addcmdline" }, P => P.RunArgs, (P, V) => P.RunArgs = V, "AddCommandLineProperty", null, ForRun, C => C.P.Run),
		Value("ClientArgs", OptGroup.Deploy, OptKind.Text, "Game Command Line", new[] { "clientcmdline" }, P => P.ClientArgs, (P, V) => P.ClientArgs = V, "AddCommandLineProperty", null, ForRun, C => C.P.Run),
		Value("ServerArgs", OptGroup.Deploy, OptKind.Text, "Server Command Line", new[] { "servercmdline" }, P => P.ServerArgs, (P, V) => P.ServerArgs = V, "AddCommandLineProperty", null, ForRun, C => C.P.Run),

		Check("HttpChunks", OptGroup.Deprecated, "Create Http Chunk Install Data", new[] { "createchunkinstall" }, P => P.HttpChunks, (P, V) => P.HttpChunks = V, null, "CreateHttpChunksLabel", ForPak),
		Value("HttpDir", OptGroup.Deprecated, OptKind.Folder, "Http Chunk Install Data Path", new[] { "chunkinstalldirectory" }, P => P.HttpChunkDir, (P, V) => P.HttpChunkDir = V, null, "HttpChunkDirLabel", ForPak, C => C.P.HttpChunks),
		Value("HttpRelease", OptGroup.Deprecated, OptKind.Text, "Http Chunk Install Release Name", new[] { "chunkinstallversion" }, P => P.HttpChunkRelease, (P, V) => P.HttpChunkRelease = V, null, "HttpChunkReleaseName", ForPak, C => C.P.HttpChunks),
		Value("RefContainer", OptGroup.Deprecated, OptKind.File, "Optional I/O Store Reference Chunk Database", new[] { "ReferenceContainerGlobalFileName" }, P => P.RefContainer, (P, V) => P.RefContainer = V, null, "IoStoreRefContainerLabel", ForPak),
		Value("RefCrypto", OptGroup.Deprecated, OptKind.File, "Optional I/O Store Reference Chunk Crypto Key", new[] { "ReferenceContainerCryptoKeys" }, P => P.RefCrypto, (P, V) => P.RefCrypto = V, null, "IoStoreRefCryptoKeys", ForPak),

		Value("Advanced", OptGroup.Advanced, OptKind.Text, "Other UAT flags", None, P => P.Advanced, (P, V) => P.Advanced = V),
	};

	static readonly Dictionary<OptGroup, string> Titles = new()
	{
		[OptGroup.General] = "General Settings", [OptGroup.Content] = "Content Scheme", [OptGroup.Release] = "Release / DLC / Patching", [OptGroup.Cook] = "Cook",
		[OptGroup.AdvancedCook] = "Advanced Cooking Options", [OptGroup.Build] = "Build", [OptGroup.Directory] = "Directory", [OptGroup.Deploy] = "Deploy And Run",
		[OptGroup.Deprecated] = "Deprecated Properties", [OptGroup.Advanced] = "Advanced"
	};

	public static IEnumerable<string> AllCookSwitches => All.SelectMany(O => O.CookSwitches).Distinct(StringComparer.OrdinalIgnoreCase);

	public static IEnumerable<string> AllFlags => All.SelectMany(O => O.Flags).Concat(new[] { "project", "platform", "clientconfig", "target", "pak", "stage", "package", "zenstore", "cookonthefly", "skipcook", "skippak", "prestaged" }).Distinct(StringComparer.OrdinalIgnoreCase);

	static OptGroup Home(OptionDef O, LauncherInfo? Info)
	{
		if (Info != null && O.EpicKey != null)
		{
			if (Info.DeprecatedKeys.Contains(O.EpicKey)) { return OptGroup.Deprecated; }
			if (Info.AdvancedCookKeys.Contains(O.EpicKey)) { return OptGroup.AdvancedCook; }
		}
		return O.Group;
	}

	/// <summary>The engine's own sections and order; options its Project Launcher does not list go to their home section.</summary>
	public static List<LayoutSection> Layout(EngineInfo? Engine)
	{
		var Info = Engine?.Launcher;
		var Out = new List<LayoutSection>();
		var Placed = new HashSet<OptionDef>();
		foreach (var S in Info?.Sections ?? new List<SectionInfo>())
		{
			var Items = S.Properties.SelectMany(P => All.Where(O => O.Epic == P)).Where(Placed.Add).ToList();
			OptGroup Group;
			if (Items.Count > 0) { Group = Items[0].Group; }
			else if (S.Title.Contains("Advanced", StringComparison.OrdinalIgnoreCase)) { Group = OptGroup.AdvancedCook; }
			else { continue; }
			var Section = new LayoutSection { Title = S.Title, Group = Group };
			Section.Items.AddRange(Items);
			Out.Add(Section);
		}
		foreach (var O in All.Where(O => !Placed.Contains(O)))
		{
			var Group = Home(O, Info);
			var Section = Out.FirstOrDefault(S => S.Group == Group);
			if (Section == null)
			{
				Section = new LayoutSection { Title = Group == OptGroup.Deprecated && Info?.DeprecatedKeys.Count == 0 ? "Other UAT Options" : Titles[Group], Group = Group };
				var At = Out.FindLastIndex(S => S.Group < Group) + 1;
				Out.Insert(At, Section);
			}
			Section.Items.Add(O);
		}
		return Out;
	}
}
