using System.Runtime.InteropServices;

namespace UELauncher;

static class Program
{
	[DllImport("kernel32.dll")] static extern bool AttachConsole(int Pid);

	[STAThread]
	static int Main(string[] Args)
	{
		if (Args.Length == 5 && Args[0] == "--ui-selftest")
		{
			// --ui-selftest <file.uproject> <out folder> <second engine> <profile.json>
			ApplicationConfiguration.Initialize();
			Application.Run(new MainForm(Args[1], Args[2..]));
			return 0;
		}
		if (Args.Length == 5 && Args[0] == "--ui-selftest-memory")
		{
			// --ui-selftest-memory <file.uproject> <out folder> <second engine> <set|check|after>
			ApplicationConfiguration.Initialize();
			Application.Run(new MainForm(Args[4] == "set" ? Args[1] : null, Args[2..4], Args[4]));
			return 0;
		}
		if (Args.Length > 0 && Args[0].StartsWith("--", StringComparison.Ordinal))
		{
			if (!Console.IsOutputRedirected) { AttachConsole(-1); }
			Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
			try { return Cli(Args).GetAwaiter().GetResult(); }
			catch (Exception E) when (E is InvalidOperationException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { Console.WriteLine("error: " + E.Message); return 2; }
		}
		ApplicationConfiguration.Initialize();
		Application.Run(new MainForm(Args.FirstOrDefault()));
		return 0;
	}

	static string? Opt(string[] A, string Name) { var I = Array.IndexOf(A, Name); return I >= 0 && I + 1 < A.Length ? A[I + 1] : null; }

	static async Task<int> Cli(string[] A)
	{
		switch (A[0])
		{
			case "--engines":
				foreach (var (Name, Root) in EngineScanner.Discover()) { var Found = EngineScanner.Scan(Root); Console.WriteLine($"{Name}\t{Found.Version}\t{Found.Kind}\t{Root}"); }
				return 0;
			case "--scan":
			{
				var E = EngineScanner.Scan(A[1], A.Contains("--force"));
				Console.WriteLine($"UE {E.Version} CL {E.Changelist} {E.Kind} {E.Branch}\nflags: {E.Flags.Count} from {E.FlagSource}");
				Console.WriteLine($"promoted: {E.IsPromoted}  launcher data: {E.Launcher.Source}  incremental: {(E.Launcher.ThreeWayIncremental ? "three-way" : "on/off")}");
				foreach (var (P, C) in E.Configs.OrderBy(K => K.Key).Select(K => (K.Key, K.Value))) { Console.WriteLine($"{P}: {string.Join(", ", C)}  [{string.Join(", ", E.Architectures.GetValueOrDefault(P) ?? new())}]"); }
				var ScanProject = Opt(A, "--project") is { } Pp ? ProjectInfo.Load(Pp) : null;
				var Platform = Opt(A, "--platform") ?? "Win64";
				var Config = Opt(A, "--config") ?? "Shipping";
				var Zen = Opt(A, "--zen") ?? "project";
				var S = ProjectSettings.Read(E, ScanProject, Platform);
				Console.WriteLine($"cook switches: {(E.CookSwitches.Count == 0 ? "(none found)" : string.Join(", ", E.CookSwitches))}");
				Console.WriteLine($"settings ({ScanProject?.Name ?? "engine defaults"}, {Platform}): zenstore={S.UseZenStore} iostore={S.UseIoStore} compressed={S.Compressed} pakstreaming={S.EnablePakStreaming} remotezen={S.AllowRemoteNetworkService}");
				var CanSkipZen = E.HasCookSwitch("SkipZenStore");
				if (!Zen.Equals("project", StringComparison.OrdinalIgnoreCase))
				{
					S = S.WithZenStore(Zen);
					Console.WriteLine($"Zen Store set to {Zen}: effective zenstore={S.UseZenStore}{(CanSkipZen || !Zen.Equals("off", StringComparison.OrdinalIgnoreCase) ? "" : "  (this engine has no -SkipZenStore, so Off cannot be applied)")}");
				}
				Console.WriteLine($"content schemes ({Config}):");
				foreach (var I in E.Launcher.Schemes)
				{
					var St = LauncherScan.Evaluate(E.Launcher, I.Id, S, Platform, Config, CanSkipZen);
					Console.WriteLine($"  {I.Name}: {(St.Available ? "available" : "blocked - " + St.Reason)}{(St.Note.Length > 0 ? "  note: " + St.Note : "")}");
				}
				foreach (var Sec in OptionCatalog.Layout(E))
				{
					Console.WriteLine($"[{Sec.Title}]");
					foreach (var O in Sec.Items) { Console.WriteLine($"  {O.Label}{(O.Flags.Length == 0 || O.Flags.Any(E.Has) ? "" : "  (NOT IN THIS ENGINE: -" + string.Join(", -", O.Flags) + ")")}"); }
				}
				if (A.Contains("--flags")) { foreach (var F in E.Flags) { Console.WriteLine($"-{F.Name}\t{F.Description}"); } }
				return 0;
			}
			case "--cli":
			{
				var ProjectPath = Opt(A, "--project") ?? throw new InvalidOperationException("--project <file.uproject> is required");
				var Project = ProjectInfo.Load(ProjectPath);
				var EngineRoot = Opt(A, "--engine") ?? EngineScanner.Resolve(Project.EngineAssociation) ?? throw new InvalidOperationException($"engine '{Project.EngineAssociation}' not found: pass --engine <folder>");
				var Engine = EngineScanner.Scan(EngineRoot);
				var Wanted = A[1];
				var Chosen = File.Exists(Wanted) ? Profile.Load(Wanted) : Profile.LoadAll(Project).FirstOrDefault(X => X.Name.Equals(Wanted, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"no profile '{Wanted}'");
				var Plan = UatCommand.Make(Chosen, Project, Engine);
				foreach (var W in Plan.Warnings) { Console.WriteLine("warning: " + W); }
				foreach (var N in Plan.Notes) { Console.WriteLine("note: " + N); }
				foreach (var X in Plan.Errors) { Console.WriteLine("error: " + X); }
				if (A.Contains("--dry-run")) { Console.WriteLine($"{Engine.RunUat} {Plan.CommandLine}"); return Plan.Errors.Count > 0 ? 3 : 0; }
				if (Plan.Errors.Count > 0) { return 3; }
				var Runner = new BuildRunner();
				Runner.Phase += X => Console.WriteLine("== " + X);
				var R = await Runner.RunAsync(Chosen, Project, Engine);
				Console.WriteLine(R.Summary());
				return R.Success ? 0 : (R.ExitCode != 0 ? R.ExitCode : 1);
			}
		}
		Console.WriteLine("usage: UELauncher [file.uproject] | --engines | --scan <engine> [--project <file.uproject>] [--platform <name>] [--config <name>] [--zen project|on|off] [--flags] [--force] | --cli <profile> --project <file.uproject> [--engine <folder>] [--dry-run]");
		return 2;
	}
}
