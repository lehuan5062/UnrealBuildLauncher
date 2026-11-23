using System.Text.Json;

namespace UELauncher;

public sealed class UatPlan
{
	public List<string> Args { get; } = new();
	public List<string> Errors { get; } = new();
	public List<string> Warnings { get; } = new();
	public List<string> Notes { get; } = new();
	public string ArchiveTemp { get; set; } = "";
	public string OutputRoot { get; set; } = "";
	public string CommandLine => string.Join(" ", Args);
}

/// <summary>Profile + project + scanned engine to BuildCookRun arguments, following what the engine's LauncherWorker emits for each content scheme. A flag the engine does not have is reported, never passed.</summary>
public static class UatCommand
{
	static string Q(string V) => "\"" + V.Replace("\"", "\\\"") + "\"";

	public static UatPlan Make(Profile P, ProjectInfo Project, EngineInfo E)
	{
		var Plan = new UatPlan();
		var A = Plan.Args;
		var CanSkipZen = E.HasCookSwitch("SkipZenStore");
		var (Settings, B) = LauncherScan.Resolve(E.Launcher, P, ProjectSettings.Read(E, Project, P.Platform), CanSkipZen);
		var Scheme = LauncherScan.Evaluate(E.Launcher, P.ContentScheme, Settings, P.Platform, P.Config, CanSkipZen);
		if (P.ZenStore.Equals("Off", StringComparison.OrdinalIgnoreCase) && P.Cook && !CanSkipZen && !B.ZenPak && !B.CookOnTheFly && !B.PreStaged)
		{
			Plan.Warnings.Add($"UE {E.Version}'s cook commandlet has no -SkipZenStore: the Zen Store setting is left to the project.");
		}

		bool Flag(string Name)
		{
			if (E.Has(Name)) { A.Add("-" + Name); return true; }
			Plan.Warnings.Add($"UE {E.Version} has no -{Name}: skipped.");
			return false;
		}
		void Value(string Name, string V, bool Quote = true)
		{
			if (string.IsNullOrWhiteSpace(V)) { return; }
			if (!E.Has(Name)) { Plan.Warnings.Add($"UE {E.Version} has no -{Name}: skipped."); return; }
			A.Add($"-{Name}={(Quote ? Q(V.Trim()) : V.Trim())}");
		}

		if (!E.Supported) { Plan.Warnings.Add($"UE {E.Version} is older than 5.6, which this app does not support."); }
		if (!Scheme.Available) { Plan.Errors.Add($"{Scheme.Info.Name}: {Scheme.Reason}"); }
		if (Scheme.Note.Length > 0) { Plan.Warnings.Add(Scheme.Note); }
		if (!E.Configs.TryGetValue(P.Platform, out var Cfgs)) { Plan.Errors.Add($"This {E.Kind} engine has no game binaries for {P.Platform}."); }
		else if (!Cfgs.Contains(P.Config)) { Plan.Errors.Add($"This {E.Kind} engine has no {P.Config} binaries for {P.Platform}."); }

		var ByTheBook = !B.CookOnTheFly && !B.ZenPak && !B.PreStaged && P.Cook;
		var DoNotCook = !B.CookOnTheFly && !B.PreStaged && !ByTheBook;
		var Deploy = !B.PreStaged ? B.CookOnTheFly || (P.Deploy && !B.Submission) : P.Deploy;
		var Run = P.Run && !B.Submission;
		var Archive = P.Archive && !B.ZenPak && !B.CookOnTheFly && !B.PreStaged;
		var Stage = !B.PreStaged && (B.Pak || B.Package || Deploy || Archive || Run);
		Validate(P, B, E, Settings, Plan, ByTheBook, Deploy, Run);

		A.Add("BuildCookRun");
		A.Add($"-project={Q(Project.Path_)}");
		A.Add("-noP4");
		A.Add($"-platform={P.Platform}");
		A.Add($"-clientconfig={P.Config}");
		A.Add("-utf8output");
		A.Add("-unattended");
		Value("target", P.Target, false);
		Value("clientarchitecture", P.Architecture, false);

		// build
		var (Build, Why) = DecideBuild(P, Project, E, B);
		Plan.Notes.Add("Build: " + Why);
		if (Build)
		{
			Flag("build");
			if (P.Clean) { Flag("clean"); }
			Value("ubtargs", P.ExtraBuild);
			if (Project.HasCode)
			{
				if (!P.CompileEditor) { Flag("nocompileeditor"); }
				else if (Project.IsEditorOpen())
				{
					Flag("nocompileeditor");
					Plan.Notes.Add("Editor: this project's editor is open, so its code is not recompiled. The cook uses the code it has loaded.");
				}
			}
		}
		if (P.ImportZenSnapshot && !B.ZenPak && !B.PreStaged) { Flag("snapshot"); }

		// maps: the initial map (when running) plus the maps to cook
		var Maps = new List<string>();
		if (Run && !string.IsNullOrWhiteSpace(P.Map)) { Maps.Add(P.Map.Trim()); }
		if (ByTheBook) { Maps.AddRange(P.CookMaps.Where(M => !Maps.Contains(M))); }

		// cook
		void PakFlags()
		{
			if (!B.Pak) { return; }
			Flag("pak");
			if (P.UseIoStore == "On") { Flag("iostore"); } else if (P.UseIoStore == "Off") { Flag("skipiostore"); }
			if (P.Compress == "On") { Flag("compressed"); } else if (P.Compress == "Off") { Flag("ForceUncompressed"); }
		}
		if (B.PreStaged) { Flag("prestaged"); }
		else if (B.CookOnTheFly)
		{
			Flag("cookonthefly");
			if (B.Zen) { Flag("zenstore"); }
			Value("AdditionalCookerOptions", P.ExtraCook);
		}
		else if (ByTheBook)
		{
			Flag("cook");
			Value("CookCultures", P.CookCultures, false);
			if (!P.Unversioned) { Flag("VersionCookedContent"); }
			var CookerOptions = P.ExtraCook.Trim();
			if (P.ZenStore.Equals("Off", StringComparison.OrdinalIgnoreCase) && CanSkipZen && !B.ZenStreaming) { CookerOptions = (CookerOptions + " -SkipZenStore").Trim(); }
			Value("AdditionalCookerOptions", CookerOptions);
			if (B.Zen) { Flag("zenstore"); }
			PakFlags();
			if (B.Pak && P.Chunks) { Flag("manifests"); }
			if (B.Pak && P.HttpChunks)
			{
				Flag("createchunkinstall");
				Value("chunkinstalldirectory", P.HttpChunkDir);
				Value("chunkinstallversion", P.HttpChunkRelease);
			}
			if (B.Submission)
			{
				if (P.EncryptIni) { Flag("encryptinifiles"); }
				if (P.BinaryConfig) { Flag("makebinaryconfig"); }
				if (P.CreateRelease) { Value("createreleaseversion", P.ReleaseName, false); }
				if (P.CreateDlc)
				{
					Value("dlcname", P.DlcName, false);
					if (P.DlcIncludeEngine) { Flag("DLCIncludeEngineContent"); }
				}
				if (P.CreatePatch)
				{
					Flag("generatepatch");
					if (P.AddPatchLevel) { Flag("AddPatchLevel"); }
				}
				if (P.CreatePatch || P.CreateRelease || P.CreateDlc)
				{
					Value("basedonreleaseversion", P.BasedOnRelease, false);
					if (P.CreatePatch) { Value("originalreleaseversion", P.OriginalRelease, false); }
				}
			}
			if (P.IncrementalCook != "Off")
			{
				// 5.6 has two different modes; 5.8 treats every name as the same mode.
				if (E.Launcher.ThreeWayIncremental && P.IncrementalCook == "ModifiedOnly" && E.Has("iterativecooking")) { Flag("iterativecooking"); }
				else if (E.First(new[] { "cookincremental", "iterativecooking", "iterate" }) is { } Inc) { Flag(Inc); }
				else { Plan.Warnings.Add($"UE {E.Version} has no incremental cook flag: skipped."); }
			}
			if (P.SkipEditorContent) { Flag("SkipCookingEditorContent"); }
		}
		else if (DoNotCook)
		{
			Flag("skipcook");
			if (B.ZenPak)
			{
				Flag("skippak");
				Value("ZenWorkspaceSharePath", P.ZenPakStreamingPath);
			}
			else { PakFlags(); }
		}
		if (Maps.Count > 0) { Value("map", string.Join("+", Maps), false); }
		if (B.Submission && P.ForDistribution) { Flag("distribution"); }

		// stage, package, deploy, archive
		if (Stage)
		{
			Flag("stage");
			if (!P.IncludeDebugFiles && E.Has("nodebuginfo")) { Flag("nodebuginfo"); }
			Value("stagingdirectory", P.StageDir);
			if (B.Pak && !string.IsNullOrWhiteSpace(P.RefContainer))
			{
				Value("ReferenceContainerGlobalFileName", P.RefContainer);
				Value("ReferenceContainerCryptoKeys", P.RefCrypto);
			}
		}
		if (B.Submission && P.Prereqs) { Flag("prereqs"); }
		if (B.Package) { Flag("package"); }
		if (Deploy) { Flag("deploy"); }
		if ((Deploy || Run) && !string.IsNullOrWhiteSpace(P.Devices)) { Value("device", P.Devices, false); }
		if ((Deploy || B.Package) && Stage && Run && !string.IsNullOrWhiteSpace(P.Map)) { Value("cmdline", P.Map); }
		if (Archive)
		{
			Plan.OutputRoot = string.IsNullOrWhiteSpace(P.ArchiveRoot) ? Path.Combine(Project.Dir, "Saved", "StagedBuilds") : Path.GetFullPath(P.ArchiveRoot);
			Plan.ArchiveTemp = Path.Combine(Plan.OutputRoot, ".uelauncher-tmp");
			Flag("archive");
			Value("archivedirectory", Plan.ArchiveTemp);
		}

		// run
		if (Run)
		{
			Flag("run");
			Value("addcmdline", P.RunArgs);
			Value("clientcmdline", P.ClientArgs);
			Value("servercmdline", P.ServerArgs);
		}

		foreach (var F in P.Flags)
		{
			var Name = F.TrimStart('-').Split('=')[0];
			if (E.Has(Name)) { A.Add("-" + F.TrimStart('-')); } else { Plan.Warnings.Add($"UE {E.Version} has no -{Name}: skipped."); }
		}
		if (!string.IsNullOrWhiteSpace(P.Advanced)) { A.Add(P.Advanced.Trim()); }
		return Plan;
	}

	/// <summary>The rules of Epic's FLauncherProfile::Validate that apply here.</summary>
	static void Validate(Profile P, SchemeBehavior B, EngineInfo E, ProjectSettings S, UatPlan Plan, bool ByTheBook, bool Deploy, bool Run)
	{
		if (P.Deploy && !B.CookOnTheFly && !ByTheBook && !B.ZenPak && !B.ZenStreaming && !B.PreStaged) { Plan.Errors.Add("Deploy To Device needs Cook Content with this content scheme."); }
		if (B.ZenPak && (!Deploy || !Run)) { Plan.Errors.Add("Zen Pak Streaming needs Deploy To Device and Run."); }
		if (B.ZenStreaming && P.Cook && P.ZenStore.Equals("Off", StringComparison.OrdinalIgnoreCase)) { Plan.Errors.Add("Zen Streaming needs Zen Store, but Zen Store is set to Off."); }
		if (B.CookOnTheFly && P.Config == "Shipping") { Plan.Errors.Add("Cook On The Fly cannot be used with Shipping, which ignores command line options."); }
		if (B.Pak && P.Chunks && !ByTheBook) { Plan.Errors.Add("Generate Chunks needs Cook Content."); }
		if (B.Pak && P.HttpChunks)
		{
			if (!P.Chunks && !(B.Submission && P.CreateDlc)) { Plan.Errors.Add("Http chunk install data needs Generate Chunks."); }
			if (string.IsNullOrWhiteSpace(P.HttpChunkRelease) || !Directory.Exists(P.HttpChunkDir)) { Plan.Errors.Add("Http chunk install data needs an existing folder and a release name."); }
		}
		if (ByTheBook && P.Unversioned && P.IncrementalCook != "Off" && S.LegacyIterative) { Plan.Errors.Add("Unversioned Cook cannot be combined with Incremental Cook while LegacyIterative is on."); }
		if (B.Submission)
		{
			if (P.CreatePatch && !ByTheBook) { Plan.Errors.Add("Create Patch needs Cook Content."); }
			if (P.AddPatchLevel && !P.CreatePatch) { Plan.Errors.Add("Add Patch Level needs Create Patch."); }
			if (P.CreateRelease && string.IsNullOrWhiteSpace(P.ReleaseName)) { Plan.Errors.Add("Create Release Version needs a release version name."); }
			if (P.CreateDlc && string.IsNullOrWhiteSpace(P.DlcName)) { Plan.Errors.Add("Create DLC needs a DLC name."); }
		}
	}

	/// <summary>Epic's ShouldBuild, with one safety rule: when it cannot be sure a content-only project can use the pre-built target, it builds.</summary>
	static (bool Build, string Why) DecideBuild(Profile P, ProjectInfo Project, EngineInfo E, SchemeBehavior B)
	{
		if (B.PreStaged || !P.Build) { return (false, B.PreStaged ? "skipped, the staged build is used as-is." : "off."); }
		if (P.ForceBuild) { return (true, "always, \"Build even if a pre-built target exists\" is on."); }
		if (!E.IsPromoted) { return (true, "needed, this engine is not a promoted build."); }
		if (Project.HasCode)
		{
			var Target = !string.IsNullOrEmpty(P.Target) ? P.Target : Project.Targets.Where(T => T.Type == "Game").Select(T => T.Name).FirstOrDefault() ?? Project.Name;
			return HasPromotedTarget(Project.Dir, Target, P.Platform, P.Config, E)
				? (false, $"skipped, a matching pre-built {Target} target exists.")
				: (true, $"needed, the project has code and no promoted {Target} target.");
		}
		if (Project.HasPluginOverrides) { return (true, "needed, the project's plugin settings may require its own target."); }
		return HasPromotedTarget(Path.Combine(E.Root, "Engine"), "UnrealGame", P.Platform, P.Config, E)
			? (false, "skipped, the engine's pre-built UnrealGame target matches.")
			: (true, "needed, no matching pre-built UnrealGame target.");
	}

	static bool HasPromotedTarget(string BaseDir, string Target, string Platform, string Config, EngineInfo E)
	{
		var Name = Config == "Development" ? $"{Target}.target" : $"{Target}-{Platform}-{Config}.target";
		var Receipt = Path.Combine(BaseDir, "Binaries", Platform, Name);
		if (!File.Exists(Receipt)) { return false; }
		try
		{
			using var Doc = JsonDocument.Parse(File.ReadAllText(Receipt));
			if (!Doc.RootElement.TryGetProperty("Version", out var V)) { return false; }
			return V.GetProperty("IsPromotedBuild").GetInt32() != 0 && V.GetProperty("MajorVersion").GetInt32() == E.Major && V.GetProperty("MinorVersion").GetInt32() == E.Minor
				&& V.GetProperty("PatchVersion").GetInt32() == E.Patch && V.GetProperty("Changelist").GetInt64() == E.Changelist && V.GetProperty("BranchName").GetString() == E.Branch;
		}
		catch (Exception Ex) when (Ex is JsonException or KeyNotFoundException or IOException or InvalidOperationException or FormatException) { return false; }
	}
}
