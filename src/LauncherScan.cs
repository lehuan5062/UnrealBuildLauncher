using System.Text.RegularExpressions;

namespace UELauncher;

public sealed class SchemeInfo
{
	public string Id { get; set; } = "";
	public string Name { get; set; } = "";
	public string Tip { get; set; } = "";
}

public sealed class SchemeRule
{
	public string Condition { get; set; } = "";
	public string Reason { get; set; } = "";
}

public sealed class SectionInfo
{
	public string Title { get; set; } = "";
	public List<string> Properties { get; set; } = new();
}

/// <summary>What one engine's own Project Launcher offers, read from its source.</summary>
public sealed class LauncherInfo
{
	public string Source { get; set; } = "built-in table";
	public List<SchemeInfo> Schemes { get; set; } = new();
	public List<SchemeRule> Rules { get; set; } = new();
	public Dictionary<string, string> Behavior { get; set; } = new();
	public List<SectionInfo> Sections { get; set; } = new();
	public List<string> AdvancedCookKeys { get; set; } = new();
	public List<string> DeprecatedKeys { get; set; } = new();
	public bool ThreeWayIncremental { get; set; }
}

public sealed record SchemeContext(string Scheme, ProjectSettings Settings, bool Remote, string Config);

/// <summary>What selecting a content scheme means for the command line.</summary>
public sealed record SchemeBehavior(bool Pak, bool Zen, bool ZenStreaming, bool CookOnTheFly, bool Package, bool ZenPak, bool Submission, bool PreStaged);

public sealed record SchemeState(SchemeInfo Info, bool Exists, bool Available, string Reason, string Note);

public static class LauncherScan
{
	static readonly Regex EnumRx = new(@"enum\s+class\s+EContentScheme[^{]*\{(.*?)\}", RegexOptions.Singleline | RegexOptions.Compiled);
	static readonly Regex CaseRx = new(@"case\s+(?:ProjectLauncher::)?EContentScheme::(\w+):\s*return\s+LOCTEXT\(\s*""(\w+)""\s*,\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
	static readonly Regex ReasonRx = new(@"OutReason\s*=\s*LOCTEXT\(\s*""\w+""\s*,\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
	static readonly Regex BoolRx = new(@"\bbool\s+(b\w+)\s*=\s*([^;]+);", RegexOptions.Compiled);
	static readonly Regex TreeRx = new(@"AddHeading\([^;]*?LOCTEXT\(\s*""\w+""\s*,\s*""([^""]+)""\)|\b(Add\w+Property)\s*\(", RegexOptions.Compiled);
	static readonly Regex KeyRx = new(@"LOCTEXT\(\s*""(\w+)""", RegexOptions.Compiled);

	public static LauncherInfo Scan(string EngineRoot, Func<string, bool> HasFlag)
	{
		var Plugin = Path.Combine(EngineRoot, "Engine", "Plugins", "Developer", "ProjectLauncher", "Source");
		var Header = Path.Combine(Plugin, "ProjectLauncher", "Public", "Model", "ProjectLauncherModel.h");
		var Model = Path.Combine(Plugin, "ProjectLauncher", "Private", "Model", "ProjectLauncherModel.cpp");
		var Generic = Path.Combine(Plugin, "ProjectLauncher", "Private", "ProfileTree", "GenericProfileTreeBuilder.cpp");
		var Custom = Path.Combine(Plugin, "ProjectLauncher", "Private", "ProfileTree", "CustomProfileTreeBuilder.cpp");
		if (!File.Exists(Header) || !File.Exists(Model) || !File.Exists(Generic)) { return BuiltIn(HasFlag); }

		var Info = new LauncherInfo { Source = "Project Launcher source" };
		var Match_ = EnumRx.Match(File.ReadAllText(Header));
		if (!Match_.Success) { return BuiltIn(HasFlag); }
		var ModelText = File.ReadAllText(Model);
		var Names = new Dictionary<string, string>();
		var Tips = new Dictionary<string, string>();
		foreach (Match M in CaseRx.Matches(ModelText))
		{
			(M.Groups[2].Value.Contains("Tip", StringComparison.Ordinal) ? Tips : Names)[M.Groups[1].Value] = Unescape(M.Groups[3].Value);
		}
		foreach (var Raw in StripComments(Match_.Groups[1].Value).Split(','))
		{
			var Id = Raw.Trim().Split('=')[0].Trim();
			if (Id.Length == 0 || Id == "MAX") { continue; }
			Info.Schemes.Add(new SchemeInfo { Id = Id, Name = Names.GetValueOrDefault(Id, Id), Tip = Tips.GetValueOrDefault(Id, "") });
		}
		if (Info.Schemes.Count == 0) { return BuiltIn(HasFlag); }

		var SetBody = Body(ModelText, "FModel::SetProfileContentScheme(");
		if (SetBody != null)
		{
			foreach (Match M in BoolRx.Matches(StripComments(SetBody))) { Info.Behavior[M.Groups[1].Value] = Regex.Replace(M.Groups[2].Value, @"\s+", " ").Trim(); }
		}

		var GenericText = File.ReadAllText(Generic);
		var RuleBody = Body(GenericText, "::IsContentSchemeAvailable(EContentScheme");
		Info.Rules = RuleBody != null ? ParseRules(StripComments(RuleBody)) : BuiltInRules();
		Info.ThreeWayIncremental = GenericText.Contains("ELauncherProfileIncrementalCookMode::ModifiedAndDependencies", StringComparison.Ordinal);

		if (File.Exists(Custom))
		{
			SectionInfo? Section = null;
			foreach (Match M in TreeRx.Matches(StripComments(File.ReadAllText(Custom))))
			{
				if (M.Groups[1].Success) { Info.Sections.Add(Section = new SectionInfo { Title = M.Groups[1].Value }); }
				else { Section?.Properties.Add(M.Groups[2].Value); }
			}
		}
		Info.AdvancedCookKeys = Keys(Path.Combine(Plugin, "CommonLaunchExtensions", "Private", "AdvancedCook", "AdvancedCookLaunchExtension.cpp"));
		Info.DeprecatedKeys = Keys(Path.Combine(Plugin, "CommonLaunchExtensions", "Private", "DeprecatedProperties", "DeprecatedPropertiesLaunchExtension.cpp"));
		return Info;
	}

	/// <summary>Files whose content the scan depends on, for the scan cache key.</summary>
	public static IEnumerable<string> SourceFiles()
	{
		var P = Path.Combine("Engine", "Plugins", "Developer", "ProjectLauncher", "Source");
		yield return Path.Combine(P, "ProjectLauncher", "Public", "Model", "ProjectLauncherModel.h");
		yield return Path.Combine(P, "ProjectLauncher", "Private", "Model", "ProjectLauncherModel.cpp");
		yield return Path.Combine(P, "ProjectLauncher", "Private", "ProfileTree", "GenericProfileTreeBuilder.cpp");
		yield return Path.Combine(P, "ProjectLauncher", "Private", "ProfileTree", "CustomProfileTreeBuilder.cpp");
		yield return Path.Combine(P, "CommonLaunchExtensions", "Private", "AdvancedCook", "AdvancedCookLaunchExtension.cpp");
		yield return Path.Combine(P, "CommonLaunchExtensions", "Private", "DeprecatedProperties", "DeprecatedPropertiesLaunchExtension.cpp");
	}

	static List<string> Keys(string FilePath) => File.Exists(FilePath) ? KeyRx.Matches(File.ReadAllText(FilePath)).Select(M => M.Groups[1].Value).Distinct().ToList() : new();

	static string Unescape(string S) => S.Replace("\\\"", "\"").Replace("\\\\", "\\");
	static string StripComments(string S) => Regex.Replace(Regex.Replace(S, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\r\n]*", "");

	/// <summary>The { } body that follows the first occurrence of Signature, skipping string literals.</summary>
	static string? Body(string Text, string Signature)
	{
		var At = Text.IndexOf(Signature, StringComparison.Ordinal);
		if (At < 0) { return null; }
		var Open = Text.IndexOf('{', At);
		if (Open < 0) { return null; }
		var Close = Matching(Text, Open, '{', '}');
		return Close < 0 ? null : Text[(Open + 1)..Close];
	}

	static int Matching(string Text, int Open, char A, char B)
	{
		var Depth = 0;
		for (var I = Open; I < Text.Length; ++I)
		{
			var C = Text[I];
			if (C == '"')
			{
				for (++I; I < Text.Length && Text[I] != '"'; ++I) { if (Text[I] == '\\') { ++I; } }
			}
			else if (C == A) { ++Depth; }
			else if (C == B && --Depth == 0) { return I; }
		}
		return -1;
	}

	static List<SchemeRule> ParseRules(string Body_)
	{
		var Rules = new List<SchemeRule>();
		foreach (Match M in Regex.Matches(Body_, @"\bif\s*\("))
		{
			var Open = M.Index + M.Length - 1;
			var Close = Matching(Body_, Open, '(', ')');
			if (Close < 0) { continue; }
			var BlockStart = Close + 1;
			while (BlockStart < Body_.Length && char.IsWhiteSpace(Body_[BlockStart])) { ++BlockStart; }
			string Block;
			if (BlockStart < Body_.Length && Body_[BlockStart] == '{')
			{
				var BlockEnd = Matching(Body_, BlockStart, '{', '}');
				if (BlockEnd < 0) { continue; }
				Block = Body_[BlockStart..BlockEnd];
			}
			else
			{
				var End = Body_.IndexOf(';', BlockStart);
				if (End < 0) { continue; }
				Block = Body_[BlockStart..End];
			}
			if (!Block.Contains("return false", StringComparison.Ordinal)) { continue; }
			var Reason = ReasonRx.Match(Block);
			Rules.Add(new SchemeRule { Condition = Regex.Replace(Body_[(Open + 1)..Close], @"\s+", " ").Trim(), Reason = Reason.Success ? Unescape(Reason.Groups[1].Value) : "" });
		}
		return Rules;
	}

	static List<SchemeRule> BuiltInRules() => new()
	{
		new() { Condition = "InContentScheme == EContentScheme::LooseFiles && ProjectSettings.bUseZenStore", Reason = "Loose Files cannot be used when Zen Store is enabled in Project Settings" },
		new() { Condition = "InContentScheme == EContentScheme::ZenPakStreaming && !ProjectSettings.bHasAutomaticZenPakStreamingWorkspaceCreation", Reason = "Automatic Zen Pak streaming workspace creation has not been enabled in Project Settings" },
		new() { Condition = "(InContentScheme == EContentScheme::ZenStreaming || InContentScheme == EContentScheme::ZenPakStreaming) && !ProjectSettings.bAllowRemoteNetworkService && Model->IsUsingRemotePlatform(BuildCookRun)", Reason = "Zen Streaming to a remote device requires RemoteNetworkService" },
		new() { Condition = "InContentScheme == EContentScheme::SubmissionPackage && BuildCookRun->GetBuildConfiguration() != EBuildConfiguration::Shipping", Reason = "Submission packages require a Shipping build" },
	};

	static LauncherInfo BuiltIn(Func<string, bool> HasFlag)
	{
		var Info = new LauncherInfo { Rules = BuiltInRules(), ThreeWayIncremental = false };
		void Add(string Id, string Name, string Tip, params string[] Flags)
		{
			if (Flags.All(HasFlag)) { Info.Schemes.Add(new SchemeInfo { Id = Id, Name = Name, Tip = Tip }); }
		}
		Add("PakFiles", "Pak Files", "Store cooked game content in one or more large Pak Files", "pak");
		Add("ZenStreaming", "Zen Streaming", "Stream cooked game content from Zen Server", "zenstore");
		Add("ZenPakStreaming", "Zen Pak Streaming", "Stream an existing Pak Files build via a Zen", "ZenWorkspaceSharePath");
		Add("DevelopmentPackage", "Development Package", "Package cooked game content into a single installable package file", "pak", "package");
		Add("LooseFiles", "Loose Files (legacy)", "Store cooked game assets in individual files");
		Add("CookOnTheFly", "Cook On The Fly", "Only cook game assets when the game requires them, and send them over the network", "cookonthefly");
		Add("PreStagedBuild", "Use Current Staged Build", "Use the existing staged build as-is", "prestaged");
		return Info;
	}

	public static SchemeBehavior Behavior(LauncherInfo Info, string Scheme, ProjectSettings Settings, string Platform, string Config)
	{
		var C = new SchemeContext(Scheme, Settings, IsRemote(Platform), Config);
		bool Value(string Name, bool Fallback) => Info.Behavior.TryGetValue(Name, out var E) ? CppCondition.Eval(E, C) ?? Fallback : Fallback;
		var IsPackage = Scheme is "DevelopmentPackage" or "SubmissionPackage";
		return new SchemeBehavior(
			Value("bPakFiles", Scheme == "PakFiles" || IsPackage),
			Value("bUseZen", Scheme is "ZenStreaming" or "ZenPakStreaming" || (Scheme != "LooseFiles" && Settings.UseZenStore)),
			Value("bUseZenStreaming", Scheme == "ZenStreaming"),
			Value("bCOTF", Scheme == "CookOnTheFly"),
			Value("bPackage", IsPackage),
			Value("bZenPakStreaming", Scheme == "ZenPakStreaming"),
			Value("bSubmissionPackage", Scheme == "SubmissionPackage"),
			Value("bPreStagedBuild", Scheme == "PreStagedBuild"));
	}

	public static bool IsRemote(string Platform) => !ProjectSettings.IniPlatform(Platform).Equals("Windows", StringComparison.OrdinalIgnoreCase);

	/// <summary>The settings a profile really cooks with: the project's, with the profile's Zen Store choice applied. The choice only counts where it is shown, so a leftover value
	/// does not change a Zen Pak Streaming, Cook On The Fly or Use Current Staged Build profile, and it needs the cook commandlet's -SkipZenStore to turn Zen off.</summary>
	public static ProjectSettings Effective(LauncherInfo Info, Profile P, ProjectSettings Raw, bool CanSkipZen)
	{
		if (P.ZenStore.Equals("Project", StringComparison.OrdinalIgnoreCase) || !P.Cook) { return Raw; }
		if (P.ZenStore.Equals("Off", StringComparison.OrdinalIgnoreCase) && !CanSkipZen) { return Raw; }
		var B = Behavior(Info, P.ContentScheme, Raw, P.Platform, P.Config);
		return B.ZenPak || B.CookOnTheFly || B.PreStaged ? Raw : Raw.WithZenStore(P.ZenStore);
	}

	/// <summary>The settings and the scheme's behaviour for a profile, with its Zen Store choice winning over the engine's own expression (5.6 turns Zen on for every scheme but Loose Files and Cook On The Fly, whatever the project says).</summary>
	public static (ProjectSettings Settings, SchemeBehavior B) Resolve(LauncherInfo Info, Profile P, ProjectSettings Raw, bool CanSkipZen)
	{
		var Settings = Effective(Info, P, Raw, CanSkipZen);
		var B = Behavior(Info, P.ContentScheme, Settings, P.Platform, P.Config);
		if (!ReferenceEquals(Settings, Raw)) { B = P.ZenStore.Equals("On", StringComparison.OrdinalIgnoreCase) ? B with { Zen = true } : B with { Zen = B.ZenStreaming || B.ZenPak }; }
		return (Settings, B);
	}

	/// <summary>Runs the engine's own availability rules for one scheme. With canOverrideZen, a scheme that is blocked only because Zen Store is on says how to unblock it.</summary>
	public static SchemeState Evaluate(LauncherInfo Info, string Scheme, ProjectSettings Settings, string Platform, string Config, bool CanOverrideZen = false)
	{
		var Found = Info.Schemes.FirstOrDefault(S => S.Id == Scheme);
		if (Found == null) { return new SchemeState(new SchemeInfo { Id = Scheme, Name = Scheme }, false, false, "This engine's Project Launcher has no such content scheme.", ""); }
		var C = new SchemeContext(Scheme, Settings, IsRemote(Platform), Config);
		var Notes = new List<string>();
		foreach (var Rule in Info.Rules)
		{
			var Blocked = CppCondition.Eval(Rule.Condition, C);
			if (Blocked == true)
			{
				var Reason = Rule.Reason.Length > 0 ? Rule.Reason : "Not available for this project.";
				if (CanOverrideZen && Settings.UseZenStore && CppCondition.Eval(Rule.Condition, C with { Settings = Settings.WithZenStore("Off") }) == false)
				{
					Reason = Reason.TrimEnd('.') + ". Set Zen Store (Content Scheme section) to Off to override this.";
				}
				return new SchemeState(Found, true, false, Reason, "");
			}
			if (Blocked == null) { Notes.Add($"Could not check this engine rule: {(Rule.Reason.Length > 0 ? Rule.Reason : Rule.Condition)}"); }
		}
		return new SchemeState(Found, true, true, "", string.Join(" ", Notes));
	}
}

/// <summary>Evaluates the small C++ conditions in the Project Launcher. A term it does not know is null (unknown), and stays unknown unless the rest decides the result.</summary>
public static class CppCondition
{
	public static bool? Eval(string Expression, SchemeContext Context)
	{
		try
		{
			var P = new Parser(Expression, Context);
			var V = P.Or();
			return P.AtEnd ? V : null;
		}
		catch (FormatException) { return null; }
	}

	sealed class Parser
	{
		readonly string _s;
		readonly SchemeContext _c;
		int _i;

		public Parser(string S, SchemeContext C) { _s = S; _c = C; }

		void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) { ++_i; } }
		public bool AtEnd { get { Skip(); return _i >= _s.Length; } }
		bool Take(string Token)
		{
			Skip();
			if (string.CompareOrdinal(_s, _i, Token, 0, Token.Length) != 0) { return false; }
			_i += Token.Length;
			return true;
		}

		public bool? Or()
		{
			var V = And();
			while (Take("||"))
			{
				var R = And();
				V = V == true || R == true ? true : V == false && R == false ? false : null;
			}
			return V;
		}

		bool? And()
		{
			var V = Unary();
			while (Take("&&"))
			{
				var R = Unary();
				V = V == false || R == false ? false : V == true && R == true ? true : null;
			}
			return V;
		}

		bool? Unary()
		{
			Skip();
			if (_i + 1 < _s.Length && _s[_i] == '!' && _s[_i + 1] != '=')
			{
				++_i;
				var V = Unary();
				return V == null ? null : !V;
			}
			if (Take("("))
			{
				var V = Or();
				if (!Take(")")) { throw new FormatException(); }
				return V;
			}
			var Left = Atom();
			if (Take("==")) { return Compare(Left, Atom()); }
			if (Take("!=")) { var V = Compare(Left, Atom()); return V == null ? null : !V; }
			return Truth(Left);
		}

		string Atom()
		{
			Skip();
			var Start = _i;
			while (_i < _s.Length)
			{
				var C = _s[_i];
				if (char.IsLetterOrDigit(C) || C is '_' or ':' or '.') { ++_i; }
				else if (C == '-' && _i + 1 < _s.Length && _s[_i + 1] == '>') { _i += 2; }
				else if (C == '(' && _i > Start)
				{
					var Depth = 0;
					do
					{
						if (_s[_i] == '(') { ++Depth; } else if (_s[_i] == ')') { --Depth; }
						++_i;
					} while (Depth > 0 && _i < _s.Length);
				}
				else { break; }
			}
			if (_i == Start) { throw new FormatException(); }
			return _s[Start.._i];
		}

		bool? Truth(string A)
		{
			if (A == "true") { return true; }
			if (A == "false") { return false; }
			if (A.Contains("IsUsingRemotePlatform", StringComparison.Ordinal)) { return _c.Remote; }
			if (A.Contains("IsHostPlatform", StringComparison.Ordinal)) { return !_c.Remote; }
			return A[(A.LastIndexOf('.') + 1)..] switch
			{
				"bUseZenStore" => _c.Settings.UseZenStore,
				"bUseIoStore" => _c.Settings.UseIoStore,
				"bHasAutomaticZenPakStreamingWorkspaceCreation" => _c.Settings.EnablePakStreaming,
				"bAllowRemoteNetworkService" => _c.Settings.AllowRemoteNetworkService,
				_ => null
			};
		}

		bool? Compare(string L, string R)
		{
			static string Tail(string S) => S[(S.LastIndexOf(':') + 1)..];
			if (R.Contains("EContentScheme::", StringComparison.Ordinal) && L is "InContentScheme" or "ContentScheme") { return _c.Scheme == Tail(R); }
			if (R.Contains("EBuildConfiguration::", StringComparison.Ordinal) && L.Contains("GetBuildConfiguration", StringComparison.Ordinal)) { return _c.Config.Equals(Tail(R), StringComparison.OrdinalIgnoreCase); }
			if (R.Contains("EProfileType::", StringComparison.Ordinal) && L.EndsWith("ProfileType", StringComparison.Ordinal)) { return Tail(R) == "Custom"; }
			return null;
		}
	}
}
