using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UELauncher;

public sealed class FlagInfo
{
	public string Name { get; set; } = "";
	public string Description { get; set; } = "";
}

/// <summary>What one engine folder offers. Everything here is read from that folder.</summary>
public sealed class EngineInfo
{
	public string Root { get; set; } = "";
	public int Major { get; set; }
	public int Minor { get; set; }
	public int Patch { get; set; }
	public long Changelist { get; set; }
	public string Branch { get; set; } = "";
	public bool IsInstalled { get; set; }
	public string FlagSource { get; set; } = "";
	public string Stamp { get; set; } = "";
	public List<FlagInfo> Flags { get; set; } = new();
	public Dictionary<string, List<string>> Configs { get; set; } = new();
	public Dictionary<string, List<string>> Architectures { get; set; } = new();
	public bool IsPromoted { get; set; }
	public LauncherInfo Launcher { get; set; } = new();

	/// <summary>Switches the cook commandlet looks for (for example SkipZenStore). UAT hands them over through -AdditionalCookerOptions.</summary>
	public List<string> CookSwitches { get; set; } = new();

	HashSet<string>? _switches;
	public bool HasCookSwitch(string Switch) => (_switches ??= new(CookSwitches, StringComparer.OrdinalIgnoreCase)).Contains(Switch);

	HashSet<string>? _names;
	public bool Has(string Flag) => (_names ??= new(Flags.Select(F => F.Name), StringComparer.OrdinalIgnoreCase)).Contains(Flag);
	public string? First(IEnumerable<string> Aliases) => Aliases.FirstOrDefault(Has);
	public string Version => $"{Major}.{Minor}.{Patch}";
	public string Kind => IsInstalled ? "launcher" : "source";
	public bool Supported => Major > 5 || (Major == 5 && Minor >= 6);
	public string RunUat => Path.Combine(Root, "Engine", "Build", "BatchFiles", "RunUAT.bat");
}

public static class EngineScanner
{
	const int ScanVersion = 4;
	static readonly Regex SwitchRx = new(@"Switches\.Contains\(\s*TEXT\(""(\w+)""\)\s*\)", RegexOptions.Compiled);
	static readonly string CookCommandletSource = Path.Combine("Engine", "Source", "Editor", "UnrealEd", "Private", "Commandlets", "CookCommandlet.cpp");
	static readonly string UnrealEdDll = Path.Combine("Engine", "Binaries", "Win64", "UnrealEditor-UnrealEd.dll");
	static readonly Regex InstalledRx = new(@"^\+InstalledPlatformConfigurations=\((.*)\)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
	static readonly Regex FieldRx = new(@"(\w+)=""?([^"",)]*)""?", RegexOptions.Compiled);
	static readonly string[] AllConfigs = { "Debug", "DebugGame", "Development", "Test", "Shipping" };
	static readonly Regex HelpRx = new(@"\[(?:Param)?Help\(\s*""([^""]+)""\s*,\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
	static readonly Regex ParseRx = new(@"(?:ParseParam\w*|Get\w*ParamValueIfNotSpecified)\(.*", RegexOptions.Compiled);
	static readonly Regex QuotedRx = new(@"""([A-Za-z][A-Za-z0-9_]*)=?""", RegexOptions.Compiled);
	static readonly Regex ConfigRx = new(@"^UnrealGame(?:-[A-Za-z0-9]+-(Debug|DebugGame|Test|Shipping))?\.target$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

	public static bool IsEngineRoot(string Root) => File.Exists(Path.Combine(Root, "Engine", "Build", "Build.version"));

	/// <summary>Accepts the engine root or its Engine subfolder.</summary>
	public static string NormalizeRoot(string Folder)
	{
		var Full = Path.GetFullPath(Folder).TrimEnd('\\', '/');
		if (!IsEngineRoot(Full) && Path.GetFileName(Full).Equals("Engine", StringComparison.OrdinalIgnoreCase)) { Full = Path.GetDirectoryName(Full)!; }
		if (!IsEngineRoot(Full)) { throw new InvalidOperationException($"Not an Unreal Engine folder (no Engine\\Build\\Build.version): {Folder}"); }
		return Full;
	}

	public static EngineInfo Scan(string Folder, bool Force = false)
	{
		var Root = NormalizeRoot(Folder);
		var Stamp = MakeStamp(Root);
		var CacheFile = Path.Combine(CacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToLowerInvariant())))[..16] + ".json");
		if (!Force && File.Exists(CacheFile))
		{
			try
			{
				var Cached = JsonSerializer.Deserialize<EngineInfo>(File.ReadAllText(CacheFile));
				if (Cached?.Stamp == Stamp) { return Cached; }
			}
			catch (JsonException) { }
		}
		using var Doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "Engine", "Build", "Build.version")));
		var V = Doc.RootElement;
		var Info = new EngineInfo
		{
			Root = Root, Stamp = Stamp,
			Major = V.GetProperty("MajorVersion").GetInt32(), Minor = V.GetProperty("MinorVersion").GetInt32(), Patch = V.GetProperty("PatchVersion").GetInt32(),
			Changelist = V.TryGetProperty("Changelist", out var Cl) ? Cl.GetInt64() : 0,
			Branch = V.TryGetProperty("BranchName", out var B) ? B.GetString() ?? "" : "",
			IsInstalled = File.Exists(Path.Combine(Root, "Engine", "Build", "InstalledBuild.txt")),
			IsPromoted = V.TryGetProperty("IsPromotedBuild", out var Promoted) && Promoted.GetInt32() != 0
		};
		ScanFlags(Info);
		ScanPlatforms(Info);
		ScanCookSwitches(Info);
		Info.Launcher = LauncherScan.Scan(Root, Info.Has);
		Directory.CreateDirectory(CacheDir);
		File.WriteAllText(CacheFile, JsonSerializer.Serialize(Info));
		return Info;
	}

	static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UELauncher", "cache");
	static string UatSource => Path.Combine("Engine", "Source", "Programs", "AutomationTool");

	static string MakeStamp(string Root)
	{
		var Parts = new[] { Path.Combine("Engine", "Build", "Build.version"), Path.Combine("Engine", "Build", "InstalledBuild.txt"), Path.Combine("Engine", "Config", "BaseEngine.ini"), Path.Combine(UatSource, "AutomationUtils", "ProjectParams.cs"), Path.Combine(UatSource, "Scripts", "BuildCookRun.Automation.cs"), Path.Combine("Engine", "Binaries", "DotNET", "AutomationTool", "AutomationTool.dll"), CookCommandletSource, UnrealEdDll }.Concat(LauncherScan.SourceFiles());
		return ScanVersion + ":" + string.Join("|", Parts.Select(P => { var F = new FileInfo(Path.Combine(Root, P)); return F.Exists ? F.LastWriteTimeUtc.Ticks + "/" + F.Length : "-"; }));
	}

	static void ScanFlags(EngineInfo Info)
	{
		var Found = new Dictionary<string, FlagInfo>(StringComparer.OrdinalIgnoreCase);
		var Dir = Path.Combine(Info.Root, UatSource);
		var Sources = new[] { Path.Combine(Dir, "AutomationUtils", "ProjectParams.cs"), Path.Combine(Dir, "Scripts", "BuildCookRun.Automation.cs") }.Where(File.Exists).ToArray();
		foreach (var Source in Sources)
		{
			var Text = File.ReadAllText(Source);
			foreach (Match M in HelpRx.Matches(Text)) { Add(Found, M.Groups[1].Value, M.Groups[2].Value.Replace("\\\"", "\"")); }
			foreach (Match L in ParseRx.Matches(Text))
			{
				foreach (Match Q in QuotedRx.Matches(L.Value)) { Add(Found, Q.Groups[1].Value, ""); }
			}
		}
		if (Found.Count > 0) { Info.FlagSource = "UAT source"; }
		if (Found.Count == 0) { ScanAssemblies(Info, Found); }
		if (Found.Count == 0) { ScanHelpOutput(Info, Found); }
		Info.Flags = Found.Values.OrderBy(F => F.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>The switches the engine's cook commandlet understands: read from its source, or when the source is not installed, searched for in the editor module.</summary>
	static void ScanCookSwitches(EngineInfo Info)
	{
		var Found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var Source = Path.Combine(Info.Root, CookCommandletSource);
		if (File.Exists(Source)) { foreach (Match M in SwitchRx.Matches(File.ReadAllText(Source))) { Found.Add(M.Groups[1].Value); } }
		if (Found.Count == 0)
		{
			var Dll = Path.Combine(Info.Root, UnrealEdDll);
			if (File.Exists(Dll)) { foreach (var Name in OptionCatalog.AllCookSwitches) { if (ContainsUtf16(Dll, Name)) { Found.Add(Name); } } }
		}
		Info.CookSwitches = Found.OrderBy(S => S, StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>True if the file contains the text as UTF-16, which is how TEXT("...") literals are stored. Reads in blocks, so a large module is not loaded whole.</summary>
	static bool ContainsUtf16(string FilePath, string Text)
	{
		var Pattern = Encoding.Unicode.GetBytes(Text);
		var Buffer = new byte[4 * 1024 * 1024 + Pattern.Length];
		try
		{
			using var Stream = File.OpenRead(FilePath);
			var Carry = 0;
			int Read;
			while ((Read = Stream.Read(Buffer, Carry, Buffer.Length - Carry)) > 0)
			{
				var Length = Carry + Read;
				if (Buffer.AsSpan(0, Length).IndexOf(Pattern) >= 0) { return true; }
				Carry = Math.Min(Pattern.Length - 1, Length);
				Buffer.AsSpan(Length - Carry, Carry).CopyTo(Buffer);
			}
		}
		catch (IOException) { }
		return false;
	}

	static void Add(Dictionary<string, FlagInfo> Found, string RawName, string Description)
	{
		var Name = RawName.Trim().TrimStart('-').TrimEnd('=');
		var Eq = Name.IndexOf('=');
		if (Eq > 0) { Name = Name[..Eq]; }
		if (Name.Length == 0 || !Regex.IsMatch(Name, @"^[A-Za-z][A-Za-z0-9_]*$")) { return; }
		if (Found.TryGetValue(Name, out var F)) { if (F.Description.Length == 0) { F.Description = Description; } }
		else { Found[Name] = new FlagInfo { Name = Name, Description = Description }; }
	}

	/// <summary>No UAT source in this engine: read the same Help attributes from the compiled automation assemblies.</summary>
	static void ScanAssemblies(EngineInfo Info, Dictionary<string, FlagInfo> Found)
	{
		var Dir = Path.Combine(Info.Root, "Engine", "Binaries", "DotNET", "AutomationTool");
		if (!Directory.Exists(Dir)) { return; }
		var Known = new HashSet<string>(OptionCatalog.AllFlags, StringComparer.OrdinalIgnoreCase);
		foreach (var Dll in Directory.EnumerateFiles(Dir, "*.dll", SearchOption.AllDirectories).Where(P => Path.GetFileName(P).StartsWith("AutomationUtils", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(P).StartsWith("AutomationScripts", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(P).Equals("AutomationTool.dll", StringComparison.OrdinalIgnoreCase)))
		{
			try
			{
				using var Pe = new PEReader(File.OpenRead(Dll));
				if (!Pe.HasMetadata) { continue; }
				var Md = Pe.GetMetadataReader();
				foreach (var H in Md.CustomAttributes)
				{
					var A = Md.GetCustomAttribute(H);
					if (!AttributeTypeName(Md, A).EndsWith("HelpAttribute", StringComparison.Ordinal)) { continue; }
					try
					{
						var R = Md.GetBlobReader(A.Value);
						if (R.ReadUInt16() != 1) { continue; }
						var Name = R.ReadSerializedString();
						var Desc = R.ReadSerializedString();
						if (Name != null && Desc != null) { Add(Found, Name, Desc); }
					}
					catch (BadImageFormatException) { }
				}
				var U = MetadataTokens.UserStringHandle(1);
				while (!U.IsNil)
				{
					var S = Md.GetUserString(U);
					if (Known.Contains(S.TrimEnd('='))) { Add(Found, S, ""); }
					U = Md.GetNextHandle(U);
				}
			}
			catch (BadImageFormatException) { }
		}
		if (Found.Count > 0) { Info.FlagSource = "UAT assemblies"; }
	}

	static string AttributeTypeName(MetadataReader Md, CustomAttribute A)
	{
		EntityHandle Parent = A.Constructor.Kind switch
		{
			HandleKind.MemberReference => Md.GetMemberReference((MemberReferenceHandle)A.Constructor).Parent,
			HandleKind.MethodDefinition => Md.GetMethodDefinition((MethodDefinitionHandle)A.Constructor).GetDeclaringType(),
			_ => default
		};
		return Parent.Kind switch
		{
			HandleKind.TypeReference => Md.GetString(Md.GetTypeReference((TypeReferenceHandle)Parent).Name),
			HandleKind.TypeDefinition => Md.GetString(Md.GetTypeDefinition((TypeDefinitionHandle)Parent).Name),
			_ => ""
		};
	}

	static void ScanHelpOutput(EngineInfo Info, Dictionary<string, FlagInfo> Found)
	{
		if (!File.Exists(Info.RunUat)) { return; }
		var Psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{Info.RunUat}\" BuildCookRun -help\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
		using var P = System.Diagnostics.Process.Start(Psi);
		if (P == null) { return; }
		P.ErrorDataReceived += (_, _) => { };
		P.BeginErrorReadLine();
		var Read = P.StandardOutput.ReadToEndAsync();
		if (!P.WaitForExit(180000)) { P.Kill(true); return; }
		foreach (Match M in Regex.Matches(Read.Result, @"^\s*-([A-Za-z][A-Za-z0-9_]*)=?\S*\s+(.*)$", RegexOptions.Multiline)) { Add(Found, M.Groups[1].Value, M.Groups[2].Value.Trim()); }
		if (Found.Count > 0) { Info.FlagSource = "RunUAT -help"; }
	}

	static void ScanPlatforms(EngineInfo Info)
	{
		var Engine = Path.Combine(Info.Root, "Engine");
		var PlatformDir = Path.Combine(Engine, "Platforms");
		if (!Info.IsInstalled)
		{
			var Platforms = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "Win64" };
			if (Directory.Exists(PlatformDir))
			{
				foreach (var D in Directory.GetDirectories(PlatformDir)) { var N = Path.GetFileName(D); Platforms.Add(N.Equals("Windows", StringComparison.OrdinalIgnoreCase) ? "Win64" : N); }
			}
			foreach (var P in Platforms) { Info.Configs[P] = AllConfigs.ToList(); }
			Info.Architectures["Win64"] = new List<string> { "x64", "arm64" };
			return;
		}

		// The list Epic's own launcher uses: an entry counts only if the file it requires was installed.
		var BaseEngine = Path.Combine(Engine, "Config", "BaseEngine.ini");
		if (File.Exists(BaseEngine))
		{
			foreach (Match Line in InstalledRx.Matches(File.ReadAllText(BaseEngine)))
			{
				var Fields = FieldRx.Matches(Line.Groups[1].Value).ToDictionary(F => F.Groups[1].Value, F => F.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
				if (!Fields.TryGetValue("PlatformName", out var Platform) || !Fields.TryGetValue("Configuration", out var Config)) { continue; }
				if (!Fields.TryGetValue("PlatformType", out var Type) || Type != "Game") { continue; }
				if (Fields.TryGetValue("RequiredFile", out var Required) && Required.Length > 0 && !File.Exists(Path.Combine(Info.Root, Required))) { continue; }
				if (!Info.Configs.TryGetValue(Platform, out var C)) { Info.Configs[Platform] = C = new(); }
				if (!C.Contains(Config)) { C.Add(Config); }
				if (Fields.TryGetValue("Architecture", out var Arch) && Arch.Length > 0)
				{
					if (!Info.Architectures.TryGetValue(Platform, out var A)) { Info.Architectures[Platform] = A = new(); }
					if (!A.Contains(Arch)) { A.Add(Arch); }
				}
			}
			if (Info.Configs.Count > 0)
			{
				foreach (var K in Info.Configs.Keys.ToList()) { var Have = Info.Configs[K]; Info.Configs[K] = AllConfigs.Where(Have.Contains).ToList(); }
				return;
			}
		}

		var BinaryRoots = new List<string> { Path.Combine(Engine, "Binaries") };
		if (Directory.Exists(PlatformDir)) { BinaryRoots.AddRange(Directory.GetDirectories(PlatformDir).Select(D => Path.Combine(D, "Binaries"))); }
		foreach (var BinRoot in BinaryRoots.Where(Directory.Exists))
		{
			foreach (var Dir in Directory.GetDirectories(BinRoot))
			{
				var Name = Path.GetFileName(Dir);
				var Cfg = Directory.GetFiles(Dir, "UnrealGame*.target").Select(F => ConfigRx.Match(Path.GetFileName(F))).Where(M => M.Success).Select(M => M.Groups[1].Success ? M.Groups[1].Value : "Development").ToList();
				if (Cfg.Count == 0) { continue; }
				if (!Info.Configs.TryGetValue(Name, out var L)) { Info.Configs[Name] = L = new(); }
				L.AddRange(Cfg);
			}
		}
		foreach (var K in Info.Configs.Keys.ToList()) { var Have = Info.Configs[K]; Info.Configs[K] = AllConfigs.Where(Have.Contains).ToList(); }
	}

	/// <summary>Engines this PC knows about, from the Epic launcher's list and the registry; only folders that really are engine roots.</summary>
	public static List<(string Name, string Root)> Discover()
	{
		var Out = new List<(string Name, string Root)>();
		void Try(string Name, string? Root)
		{
			if (string.IsNullOrWhiteSpace(Root)) { return; }
			try
			{
				var Full = Path.GetFullPath(Root).TrimEnd('\\', '/');
				if (IsEngineRoot(Full) && !Out.Any(X => X.Root.Equals(Full, StringComparison.OrdinalIgnoreCase))) { Out.Add((Name, Full)); }
			}
			catch (Exception Ex) when (Ex is ArgumentException or IOException or NotSupportedException) { }
		}
		var Dat = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
		if (File.Exists(Dat))
		{
			try
			{
				using var Doc = JsonDocument.Parse(File.ReadAllText(Dat));
				foreach (var Entry in Doc.RootElement.GetProperty("InstallationList").EnumerateArray())
				{
					var Loc = Entry.TryGetProperty("InstallLocation", out var L) ? L.GetString() : null;
					if (Loc != null) { Try(Path.GetFileName(Loc.TrimEnd('\\')), Loc); }
				}
			}
			catch (Exception Ex) when (Ex is JsonException or KeyNotFoundException or IOException) { }
		}
		using (var K = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine"))
		{
			if (K != null) { foreach (var S in K.GetSubKeyNames()) { using var V = K.OpenSubKey(S); Try("UE_" + S, V?.GetValue("InstalledDirectory") as string); } }
		}
		using (var K = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds"))
		{
			if (K != null) { foreach (var S in K.GetValueNames()) { Try(S, K.GetValue(S) as string); } }
		}
		return Out;
	}

	/// <summary>The engine a project asks for: "5.8" or a source-build GUID.</summary>
	public static string? Resolve(string Association)
	{
		if (string.IsNullOrEmpty(Association)) { return null; }
		using (var K = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds"))
		{
			if (K?.GetValue(Association) is string R && IsEngineRoot(R)) { return Path.GetFullPath(R); }
		}
		using (var K = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\EpicGames\Unreal Engine\{Association}"))
		{
			if (K?.GetValue("InstalledDirectory") is string R && IsEngineRoot(R)) { return Path.GetFullPath(R); }
		}
		foreach (var (_, Root) in Discover())
		{
			try
			{
				using var Doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "Engine", "Build", "Build.version")));
				if ($"{Doc.RootElement.GetProperty("MajorVersion").GetInt32()}.{Doc.RootElement.GetProperty("MinorVersion").GetInt32()}" == Association) { return Root; }
			}
			catch (Exception Ex) when (Ex is JsonException or KeyNotFoundException or IOException) { }
		}
		return null;
	}
}
