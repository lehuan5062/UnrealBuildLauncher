using System.Text.Json;
using System.Text.RegularExpressions;

namespace UELauncher;

public sealed class ProjectInfo
{
	public string Path_ { get; init; } = "";
	public string Dir => Path.GetDirectoryName(Path_)!;
	public string Name => Path.GetFileNameWithoutExtension(Path_);
	public string EngineAssociation { get; init; } = "";
	public List<(string Name, string Type)> Targets { get; init; } = new();
	public List<string> Maps { get; init; } = new();
	public List<string> Modules { get; init; } = new();
	public bool HasPluginOverrides { get; init; }
	public bool HasCode => Targets.Count > 0 || Modules.Count > 0;
	public string ProfileDir => Path.Combine(Dir, "Build", "UELauncher");

	/// <summary>True while an editor has this project's module loaded (Windows keeps a loaded DLL from being opened for writing).</summary>
	public bool IsEditorOpen()
	{
		foreach (var Module in Modules)
		{
			var Dll = Path.Combine(Dir, "Binaries", "Win64", $"UnrealEditor-{Module}.dll");
			if (!File.Exists(Dll)) { continue; }
			try { using var Probe = new FileStream(Dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
			catch (IOException) { return true; }
			catch (UnauthorizedAccessException) { }
		}
		return false;
	}

	public static ProjectInfo Load(string UProject)
	{
		var Full = Path.GetFullPath(UProject);
		using var Doc = JsonDocument.Parse(File.ReadAllText(Full), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		var Root_ = Doc.RootElement;
		var Assoc = Root_.TryGetProperty("EngineAssociation", out var A) ? A.GetString() ?? "" : "";
		var Modules = new List<string>();
		if (Root_.TryGetProperty("Modules", out var M_) && M_.ValueKind == JsonValueKind.Array)
		{
			foreach (var E in M_.EnumerateArray()) { if (E.TryGetProperty("Name", out var N) && N.GetString() is { } Name) { Modules.Add(Name); } }
		}
		var Root = Path.GetDirectoryName(Full)!;
		var PluginList = Root_.TryGetProperty("Plugins", out var P_) && P_.ValueKind == JsonValueKind.Array && P_.GetArrayLength() > 0;
		var PluginDir = Path.Combine(Root, "Plugins");
		var LocalPlugins = Directory.Exists(PluginDir) && Directory.EnumerateFiles(PluginDir, "*.uplugin", SearchOption.AllDirectories).Any();

		var Targets = new List<(string Name, string Type)>();
		var Source = Path.Combine(Root, "Source");
		if (Directory.Exists(Source))
		{
			foreach (var F in Directory.GetFiles(Source, "*.Target.cs"))
			{
				var T = File.ReadAllText(F);
				var C = Regex.Match(T, @"class\s+(\w+?)Target\b");
				var K = Regex.Match(T, @"Type\s*=\s*TargetType\.(\w+)");
				if (C.Success) { Targets.Add((C.Groups[1].Value, K.Success ? K.Groups[1].Value : "Game")); }
			}
		}
		var Maps = new List<string>();
		var Content = Path.Combine(Root, "Content");
		if (Directory.Exists(Content))
		{
			foreach (var M in Directory.EnumerateFiles(Content, "*.umap", SearchOption.AllDirectories))
			{
				Maps.Add("/Game/" + Path.ChangeExtension(Path.GetRelativePath(Content, M), null).Replace('\\', '/'));
			}
			Maps.Sort(StringComparer.OrdinalIgnoreCase);
		}
		return new ProjectInfo { Path_ = Full, EngineAssociation = Assoc, Targets = Targets, Maps = Maps, Modules = Modules, HasPluginOverrides = PluginList || LocalPlugins };
	}
}
