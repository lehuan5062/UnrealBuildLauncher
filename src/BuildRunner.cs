using System.Diagnostics;
using System.IO.Compression;

namespace UELauncher;

public sealed class BuildReport
{
	public int ExitCode { get; set; }
	public bool Cancelled { get; set; }
	public string LogFile { get; set; } = "";
	public string OutputDir { get; set; } = "";
	public string ZipPath { get; set; } = "";
	public long Bytes { get; set; }
	public long ZipBytes { get; set; }
	public int Files { get; set; }
	public List<string> DebugFiles { get; } = new();
	public bool Success => ExitCode == 0 && !Cancelled;
	public string Summary()
	{
		if (Cancelled) { return "Cancelled."; }
		if (ExitCode != 0) { return $"FAILED (exit {ExitCode}). Log: {LogFile}"; }
		var S = "BUILD SUCCESSFUL.";
		if (OutputDir.Length > 0) { S += $"\r\nOutput: {OutputDir}\r\n{Files} files, {Bytes / 1048576.0:0.0} MB. Debug files: {DebugFiles.Count}" + (DebugFiles.Count > 0 ? " (" + string.Join(", ", DebugFiles.Take(3).Select(Path.GetFileName)) + ")" : ""); }
		if (ZipPath.Length > 0) { S += $"\r\nZip: {ZipPath} ({ZipBytes / 1048576.0:0.0} MB)"; }
		return S + $"\r\nLog: {LogFile}";
	}
}

public sealed class BuildRunner
{
	static readonly string[] DebugExt = { ".pdb", ".debug", ".sym", ".dsym", ".psym" };
	static readonly (string Marker, string Phase)[] Phases = { ("BUILD COMMAND STARTED", "Build"), ("COOK COMMAND STARTED", "Cook"), ("STAGE COMMAND STARTED", "Stage"), ("PACKAGE COMMAND STARTED", "Package"), ("ARCHIVE COMMAND STARTED", "Archive"), ("RUN COMMAND STARTED", "Run") };
	Process? _proc;
	volatile bool _cancel;
	public event Action<string>? Line;
	public event Action<string>? Phase;

	public void Cancel()
	{
		_cancel = true;
		try { _proc?.Kill(true); } catch (Exception E) when (E is InvalidOperationException or System.ComponentModel.Win32Exception) { }
	}

	public async Task<BuildReport> RunAsync(Profile P, ProjectInfo Project, EngineInfo E)
	{
		var Plan = UatCommand.Make(P, Project, E);
		var LogDir = Path.Combine(Project.Dir, "Saved", "UELauncher");
		Directory.CreateDirectory(LogDir);
		var Report = new BuildReport { LogFile = Path.Combine(LogDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{P.Name}.log") };
		using var Log = new StreamWriter(Report.LogFile) { AutoFlush = true };
		void Emit(string S)
		{
			lock (Log) { Log.WriteLine(S); }
			foreach (var (Marker, Name) in Phases) { if (S.Contains(Marker, StringComparison.Ordinal)) { Phase?.Invoke(Name); } }
			Line?.Invoke(S);
		}
		foreach (var W in Plan.Warnings) { Emit("[UELauncher] warning: " + W); }
		foreach (var N in Plan.Notes) { Emit("[UELauncher] " + N); }
		if (Plan.Errors.Count > 0)
		{
			foreach (var X in Plan.Errors) { Emit("[UELauncher] error: " + X); }
			Report.ExitCode = 3;
			Phase?.Invoke("Failed");
			return Report;
		}
		if (Plan.ArchiveTemp.Length > 0 && Directory.Exists(Plan.ArchiveTemp)) { Directory.Delete(Plan.ArchiveTemp, true); }
		Emit($"[UELauncher] RunUAT {Plan.CommandLine}");
		var Psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{E.RunUat}\" {Plan.CommandLine}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Project.Dir };
		using var Proc = new Process { StartInfo = Psi };
		Proc.OutputDataReceived += (_, D) => { if (D.Data != null) { Emit(D.Data); } };
		Proc.ErrorDataReceived += (_, D) => { if (D.Data != null) { Emit(D.Data); } };
		_proc = Proc;
		Proc.Start();
		Proc.BeginOutputReadLine();
		Proc.BeginErrorReadLine();
		await Proc.WaitForExitAsync();
		Proc.WaitForExit();
		_proc = null;
		Report.ExitCode = Proc.ExitCode;
		Report.Cancelled = _cancel;
		if (Report.Success && Plan.ArchiveTemp.Length > 0)
		{
			Phase?.Invoke("Finishing");
			await Task.Run(() => Finish(P, Plan, Report));
		}
		else if (Plan.ArchiveTemp.Length > 0 && Directory.Exists(Plan.ArchiveTemp)) { Directory.Delete(Plan.ArchiveTemp, true); }
		Emit("[UELauncher] " + Report.Summary());
		Phase?.Invoke(Report.Success ? "Done" : Report.Cancelled ? "Cancelled" : "Failed");
		return Report;
	}

	static void Finish(Profile P, UatPlan Plan, BuildReport R)
	{
		var Inner = Directory.Exists(Plan.ArchiveTemp) ? Directory.GetDirectories(Plan.ArchiveTemp) : Array.Empty<string>();
		if (Inner.Length != 1) { R.ExitCode = 90; return; }
		var Base = $"{DateTime.Now:yyyyMMdd}-{(string.IsNullOrWhiteSpace(P.Suffix) ? P.Config.ToLowerInvariant() : P.Suffix)}";
		var Name = Base;
		for (var I = 2; Directory.Exists(Path.Combine(Plan.OutputRoot, Name)) || File.Exists(Path.Combine(Plan.OutputRoot, Name + ".zip")); ++I) { Name = $"{Base}-{I}"; }
		R.OutputDir = Path.Combine(Plan.OutputRoot, Name);
		Directory.Move(Inner[0], R.OutputDir);
		Directory.Delete(Plan.ArchiveTemp, true);
		foreach (var F in new DirectoryInfo(R.OutputDir).EnumerateFiles("*", SearchOption.AllDirectories))
		{
			++R.Files;
			R.Bytes += F.Length;
			if (DebugExt.Contains(F.Extension, StringComparer.OrdinalIgnoreCase)) { R.DebugFiles.Add(F.FullName); }
		}
		if (P.Zip)
		{
			R.ZipPath = R.OutputDir + ".zip";
			ZipFile.CreateFromDirectory(R.OutputDir, R.ZipPath, CompressionLevel.Optimal, false);
			R.ZipBytes = new FileInfo(R.ZipPath).Length;
		}
	}
}
