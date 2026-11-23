using System.Text.Json;
using System.Text.Json.Serialization;

namespace UELauncher;

public sealed class Profile
{
	public string Name { get; set; } = "Shipping";

	// General
	public string Target { get; set; } = "";
	public string Platform { get; set; } = "Win64";
	public string Architecture { get; set; } = "";
	public string Config { get; set; } = "Shipping";
	public string ContentScheme { get; set; } = "PakFiles";

	// Content scheme. Compress and UseIoStore are Project, On or Off.
	public string Compress { get; set; } = "Project";
	public string UseIoStore { get; set; } = "Project";
	public string ZenStore { get; set; } = "Project";
	public bool Chunks { get; set; }
	public bool ImportZenSnapshot { get; set; }
	public string ZenPakStreamingPath { get; set; } = "";

	// Release / DLC / Patching
	public bool CreateRelease { get; set; }
	public string ReleaseName { get; set; } = "";
	public bool EncryptIni { get; set; }
	public bool ForDistribution { get; set; }
	public bool Prereqs { get; set; }
	public bool BinaryConfig { get; set; }
	public bool CreatePatch { get; set; }
	public bool AddPatchLevel { get; set; }
	public bool CreateDlc { get; set; }
	public string DlcName { get; set; } = "";
	public bool DlcIncludeEngine { get; set; }
	public string BasedOnRelease { get; set; } = "";
	public string OriginalRelease { get; set; } = "";

	// Cook. IncrementalCook is Off, ModifiedOnly or ModifiedAndDependencies.
	public bool Cook { get; set; } = true;
	public string IncrementalCook { get; set; } = "Off";
	public bool Unversioned { get; set; } = true;
	public List<string> CookMaps { get; set; } = new();
	public string CookCultures { get; set; } = "";
	public string ExtraCook { get; set; } = "";
	public bool SkipEditorContent { get; set; }

	// Build
	public bool Build { get; set; } = true;
	public bool ForceBuild { get; set; }
	public bool Clean { get; set; }
	public bool CompileEditor { get; set; } = true;
	public string ExtraBuild { get; set; } = "";

	// Directory
	public bool Archive { get; set; } = true;
	public string ArchiveRoot { get; set; } = "";
	public string Suffix { get; set; } = "ship";
	public bool Zip { get; set; } = true;
	public string StageDir { get; set; } = "";
	public bool IncludeDebugFiles { get; set; }

	// Deploy and run
	public bool Deploy { get; set; }
	public string Devices { get; set; } = "";
	public bool Run { get; set; }
	public string Map { get; set; } = "";
	public string RunArgs { get; set; } = "";
	public string ClientArgs { get; set; } = "";
	public string ServerArgs { get; set; } = "";

	// Deprecated by Epic in newer engines
	public bool HttpChunks { get; set; }
	public string HttpChunkDir { get; set; } = "";
	public string HttpChunkRelease { get; set; } = "";
	public string RefContainer { get; set; } = "";
	public string RefCrypto { get; set; } = "";

	public List<string> Flags { get; set; } = new();
	public string Advanced { get; set; } = "";

	// Fields of the first profile format, read once and converted by Upgrade().
	[JsonPropertyName("Pak"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? OldPak { get; set; }
	[JsonPropertyName("Compressed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? OldCompressed { get; set; }
	[JsonPropertyName("IoStore"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? OldIoStore { get; set; }
	[JsonPropertyName("Incremental"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? OldIncremental { get; set; }

	Profile Upgrade()
	{
		if (OldPak == false && ContentScheme == "PakFiles") { ContentScheme = "LooseFiles"; }
		if (OldCompressed == true && Compress == "Project") { Compress = "On"; }
		if (OldIoStore == true && UseIoStore == "Project") { UseIoStore = "On"; }
		if (OldIncremental == true && IncrementalCook == "Off") { IncrementalCook = "ModifiedAndDependencies"; }
		OldPak = OldCompressed = OldIoStore = OldIncremental = null;
		return this;
	}

	static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
	public static Profile Load(string FilePath) => (JsonSerializer.Deserialize<Profile>(File.ReadAllText(FilePath)) ?? new Profile()).Upgrade();
	public void Save(string FilePath)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
		File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
	}
	public Profile Clone() => JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(this))!;

	public static Profile Shipping() => new();
	public static Profile Development() => new() { Name = "Development", Config = "Development", IncludeDebugFiles = true, Suffix = "dev", Zip = false };

	public static List<Profile> LoadAll(ProjectInfo Project)
	{
		var Out = new List<Profile>();
		if (Directory.Exists(Project.ProfileDir))
		{
			foreach (var F in Directory.GetFiles(Project.ProfileDir, "*.json").OrderBy(F => F)) { try { Out.Add(Load(F)); } catch (JsonException) { } }
		}
		if (Out.Count == 0) { Out.Add(Shipping()); Out.Add(Development()); }
		return Out;
	}
}
