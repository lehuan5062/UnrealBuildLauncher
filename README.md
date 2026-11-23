# UE Launcher

A desktop replacement for Unreal's Project Launcher that drives `BuildCookRun` directly, so every packaging flag is reachable (for example `-nodebuginfo`, which keeps `.pdb` files out of a Shipping build). Works with UE 5.6 and later, launcher-installed or built from source.

## Build

Run `build.cmd` (needs the .NET 9 SDK or newer). The result is `publish\UELauncher.exe`. Close the app first, because the build replaces its exe.

## Use

    UELauncher.exe [file.uproject]
    UELauncher.exe --engines
    UELauncher.exe --scan <engine folder> [--project <file.uproject>] [--platform <name>] [--config <name>] [--flags] [--force]
    UELauncher.exe --cli <profile name or file> --project <file.uproject> [--engine <folder>] [--dry-run]

Profiles are JSON in `<project>/Build/UELauncher/`. Builds are archived to `<project>/Saved/StagedBuilds/<date>-<suffix>` (never overwritten), with an optional zip and a report of size and leftover debug files.

The app reopens the last project, and for each project remembers its engine, its profile and any option changes you have not saved. This is kept per user in `%LOCALAPPDATA%\UELauncher\state.json`, so the project's profile files only change when you press Save. **Revert** discards the unsaved changes.

## What the engine scan reads

Nothing is keyed on a version number. For the selected engine folder and project, the app reads:

- **UAT flags** from `ProjectParams.cs` and `BuildCookRun.Automation.cs`, or the compiled automation assemblies, or `RunUAT -help`. An option whose flag is missing is greyed and never passed.
- **Content schemes** from the engine's Project Launcher source: the list, names, tooltips, what each one does (`SetProfileContentScheme`) and when it is blocked (`IsContentSchemeAvailable`). If a rule contains a term the app does not understand, the scheme stays selectable with a note.
- **Project settings** from the ini hierarchy (engine, platform, project). For example, Loose Files is blocked while the effective `bUseZenStore` is true, which is the default in UE 5.8 but not in 5.6.
- **Sections and order** from `CustomProfileTreeBuilder.cpp` and the Advanced Cook and Deprecated Properties extensions.
- **Platforms, configurations and architectures** from `InstalledPlatformConfigurations` on launcher engines.

Compress Pak Files and Use Io Store have three states, because UAT follows the project setting unless told otherwise: *Project setting* passes nothing, *On* passes `-compressed` / `-iostore`, *Off* passes `-ForceUncompressed` / `-skipiostore`.

**Zen Store** (Project setting / On / Off) overrides the project's Zen Store setting for one profile. Off adds `-SkipZenStore` to the cook commandlet's options (it beats the project setting; UAT forwards it through `-AdditionalCookerOptions`), so the cook writes loose files and Loose Files becomes selectable even when the project has Zen Store on, which is the default in UE 5.8. The scan reads which switches each engine's cook commandlet has (from its source, or from the editor module when the source is not installed) and greys the option if `SkipZenStore` is missing.

Not included: Epic's Boot Test, Build Sync, UGS Sync, Profile Wizard and Insights extensions, and listing connected devices. Type device names as `Platform@Device`; other flags are in the All engine flags tab.

## License

GNU General Public License v3.0 (see [LICENSE](LICENSE)).
