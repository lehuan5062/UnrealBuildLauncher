using System.Collections.Concurrent;

namespace UELauncher;

/// <summary>A stack of ini files where the last file to set a key wins, read the way UE layers its config.</summary>
public sealed class IniStack
{
	readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

	public void Load(string FilePath)
	{
		if (!File.Exists(FilePath)) { return; }
		Dictionary<string, string>? Section = null;
		foreach (var Raw in File.ReadLines(FilePath))
		{
			var Line = Raw.Trim();
			if (Line.Length == 0 || Line[0] == ';' || Line[0] == '#') { continue; }
			if (Line[0] == '[' && Line[^1] == ']')
			{
				var Name = Line[1..^1];
				if (!_sections.TryGetValue(Name, out Section)) { _sections[Name] = Section = new(StringComparer.OrdinalIgnoreCase); }
				continue;
			}
			if (Section == null) { continue; }
			var Eq = Line.IndexOf('=');
			if (Eq <= 0) { continue; }
			var Key = Line[..Eq].Trim();
			if (Key[0] == '!') { Section.Remove(Key[1..]); continue; }
			if (Key[0] is '+' or '-' or '.' or '@' or '*') { continue; }	// array operations: none of the keys read here are arrays
			Section[Key] = Line[(Eq + 1)..].Trim();
		}
	}

	public string? Get(string Section, string Key) => _sections.TryGetValue(Section, out var S) && S.TryGetValue(Key, out var V) ? V : null;

	public bool GetBool(string Section, string Key, bool Default = false)
	{
		var V = Get(Section, Key);
		if (V == null) { return Default; }
		return V.Equals("true", StringComparison.OrdinalIgnoreCase) || V.Equals("yes", StringComparison.OrdinalIgnoreCase) || V.Equals("on", StringComparison.OrdinalIgnoreCase) || V == "1";
	}
}

/// <summary>The project's effective packaging settings for one platform: engine defaults overridden by the project.</summary>
public sealed class ProjectSettings
{
	const string Packaging = "/Script/UnrealEd.ProjectPackagingSettings";
	static readonly ConcurrentDictionary<string, ProjectSettings> Cache = new();

	public bool UseZenStore { get; private set; }
	public bool UseIoStore { get; private init; }
	public bool Compressed { get; private init; }
	public bool EnablePakStreaming { get; private init; }
	public bool AllowRemoteNetworkService { get; private init; }
	public bool LegacyIterative { get; private init; }

	/// <summary>The same settings with a profile's Zen Store choice applied: Project leaves them alone, On or Off replaces what the project says.</summary>
	public ProjectSettings WithZenStore(string? Choice)
	{
		if (string.IsNullOrEmpty(Choice) || Choice.Equals("Project", StringComparison.OrdinalIgnoreCase)) { return this; }
		var Copy = (ProjectSettings)MemberwiseClone();
		Copy.UseZenStore = Choice.Equals("On", StringComparison.OrdinalIgnoreCase);
		return Copy;
	}

	public static string IniPlatform(string Platform) => Platform.Equals("Win64", StringComparison.OrdinalIgnoreCase) ? "Windows" : Platform;

	static IEnumerable<string> Hierarchy(string EngineRoot, string? ProjectDir, string Type, string Platform)
	{
		var Ini = IniPlatform(Platform);
		var Config = Path.Combine(EngineRoot, "Engine", "Config");
		yield return Path.Combine(Config, "Base.ini");
		yield return Path.Combine(Config, $"Base{Type}.ini");
		yield return Path.Combine(Config, Ini, $"Base{Ini}{Type}.ini");
		yield return Path.Combine(EngineRoot, "Engine", "Platforms", Ini, "Config", $"Base{Ini}{Type}.ini");
		if (ProjectDir == null) { yield break; }
		yield return Path.Combine(ProjectDir, "Config", $"Default{Type}.ini");
		yield return Path.Combine(ProjectDir, "Config", Ini, $"{Ini}{Type}.ini");
		yield return Path.Combine(ProjectDir, "Platforms", Ini, "Config", $"{Ini}{Type}.ini");
	}

	static IniStack Stack(string EngineRoot, string? ProjectDir, string Type, string Platform)
	{
		var Stack_ = new IniStack();
		foreach (var F in Hierarchy(EngineRoot, ProjectDir, Type, Platform)) { Stack_.Load(F); }
		return Stack_;
	}

	public static ProjectSettings Read(EngineInfo Engine, ProjectInfo? Project, string Platform)
	{
		var Files = new[] { "Game", "Engine", "Editor" }.SelectMany(T => Hierarchy(Engine.Root, Project?.Dir, T, Platform));
		var Key = string.Join("|", Files.Select(F => { var I = new FileInfo(F); return I.Exists ? $"{F}:{I.LastWriteTimeUtc.Ticks}" : ""; }));
		return Cache.GetOrAdd(Key, _ =>
		{
			var Game = Stack(Engine.Root, Project?.Dir, "Game", Platform);
			var EngineIni = Stack(Engine.Root, Project?.Dir, "Engine", Platform);
			var Editor = Stack(Engine.Root, Project?.Dir, "Editor", Platform);
			var IoStore = Game.GetBool(Packaging, "bUseIoStore");

			// 5.6 uses AllowRemoteNetworkService=true/false, 5.8 uses RemoteNetworkService=<kind or None>.
			var Remote = EngineIni.Get("Zen.AutoLaunch", "RemoteNetworkService");
			var AllowRemote = Remote != null ? !Remote.Equals("None", StringComparison.OrdinalIgnoreCase) : EngineIni.GetBool("Zen.AutoLaunch", "AllowRemoteNetworkService");

			return new ProjectSettings
			{
				// ProjectPackagingSettings treats Zen Store as off when I/O Store is off.
				UseZenStore = Game.GetBool(Packaging, "bUseZenStore") && IoStore,
				UseIoStore = IoStore,
				Compressed = Game.GetBool(Packaging, "bCompressed"),
				EnablePakStreaming = Game.GetBool(Packaging, "bEnablePakStreaming"),
				AllowRemoteNetworkService = EngineIni.GetBool("Zen", "AutoLaunch") && AllowRemote,
				LegacyIterative = Editor.GetBool("CookSettings", "LegacyIterative")
			};
		});
	}
}
