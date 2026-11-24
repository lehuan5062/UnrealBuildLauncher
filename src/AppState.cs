using System.Text.Json;

namespace UELauncher;

/// <summary>What the app remembers for one project.</summary>
public sealed class ProjectMemory
{
	public string Engine { get; set; } = "";

	/// <summary>The project's EngineAssociation when Engine was chosen. If the project moves to another engine, the remembered one no longer applies.</summary>
	public string Association { get; set; } = "";

	public string Profile { get; set; } = "";

	/// <summary>Profiles with changes that were not saved to the project, by profile name.</summary>
	public Dictionary<string, Profile> Working { get; set; } = new();
}

/// <summary>The last project, engine and options, kept per user in %LOCALAPPDATA%\UELauncher\state.json.</summary>
public sealed class AppState
{
	static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	public string LastProject { get; set; } = "";
	public string LastEngine { get; set; } = "";

	/// <summary>Projects opened before, most recent first.</summary>
	public List<string> RecentProjects { get; set; } = new();
	public Dictionary<string, ProjectMemory> Projects { get; set; } = new();

	public void AddRecent(string ProjectPath)
	{
		RecentProjects.RemoveAll(P => P.Equals(ProjectPath, StringComparison.OrdinalIgnoreCase));
		RecentProjects.Insert(0, ProjectPath);
		if (RecentProjects.Count > 20) { RecentProjects.RemoveRange(20, RecentProjects.Count - 20); }
	}

	/// <summary>State saved before the recent list existed still knows which projects were opened.</summary>
	void SeedRecent()
	{
		if (RecentProjects.Count > 0) { return; }
		foreach (var Key in Projects.Keys) { RecentProjects.Add(Key); }
		if (LastProject.Length > 0) { AddRecent(LastProject); }
	}

	public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UELauncher", "state.json");

	public ProjectMemory For(string ProjectPath)
	{
		var Key = Path.GetFullPath(ProjectPath).ToLowerInvariant();
		if (!Projects.TryGetValue(Key, out var Memory)) { Projects[Key] = Memory = new ProjectMemory(); }
		return Memory;
	}

	public static bool Same(Profile A, Profile B) => JsonSerializer.Serialize(A) == JsonSerializer.Serialize(B);

	/// <summary>A missing or damaged file just means nothing is remembered yet.</summary>
	public static AppState Load(string FilePath)
	{
		try
		{
			if (File.Exists(FilePath))
			{
				var State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new AppState();
				State.SeedRecent();
				return State;
			}
		}
		catch (Exception Ex) when (Ex is JsonException or IOException or UnauthorizedAccessException) { }
		return new AppState();
	}

	public void Save(string FilePath)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			var Temp = FilePath + ".tmp";
			File.WriteAllText(Temp, JsonSerializer.Serialize(this, Json));
			File.Move(Temp, FilePath, true);
		}
		catch (Exception Ex) when (Ex is IOException or UnauthorizedAccessException) { }
	}
}
