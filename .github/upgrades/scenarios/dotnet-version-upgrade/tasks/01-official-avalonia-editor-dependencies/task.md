# 01-official-avalonia-editor-dependencies: Replace local Avalonia editor dependencies

Replace the in-repo `external/AvaloniaEdit` dependency chain with official NuGet packages and align the editor-related projects on supported package references for .NET 10. This task covers the low-risk foundation needed to satisfy the requirement to rely only on official Avalonia packages rather than local or forked source.

This includes updating project references and package references for the Avalonia editor components so downstream UI work builds against the official distribution model instead of the checked-in source projects.

**Done when**: No production project depends on the local `external/AvaloniaEdit` projects, official NuGet packages are referenced instead, and the affected projects restore successfully on .NET 10.

## Research
- `SqlPhanos.csproj` currently targets `net8.0` and still references `Avalonia.ReactiveUI`; it does not directly reference the local `external/AvaloniaEdit` projects.
- The solution still includes `external/AvaloniaEdit/src/AvaloniaEdit/AvaloniaEdit.csproj` and `external/AvaloniaEdit/src/AvaloniaEdit.TextMate/AvaloniaEdit.TextMate.csproj` as first-class projects.
- `get_supported_package_version` reports `Avalonia.AvaloniaEdit` 12.0.0 and `AvaloniaEdit.TextMate` 12.0.0 as supported for `SqlPhanos` on `net10.0`.
- `AvaloniaEdit.TextMate.Avalonia` does not resolve as a supported package, so the replacement path should use `Avalonia.AvaloniaEdit` plus `AvaloniaEdit.TextMate`.
- `Program.cs` still called `UseReactiveUI()`, and `SearchView`/`SearchResultsViewModel` still used ReactiveUI APIs through `Avalonia.ReactiveUI`; those references had to be removed because official NuGet feeds do not provide the previously referenced 12.0.3 ReactiveUI package.
- Official NuGet resolution also does not provide `Avalonia.Diagnostics` 12.0.3, so that package must not remain in the project if the app is to restore from official feeds only.
- Official Dock packages also require package-based fluent theme resources, and current Avalonia XAML expects `PlaceholderText` plus `ScrollViewer.*ScrollBarVisibility` attached properties instead of older property forms.

## Execution Plan
1. Identify all local Avalonia editor project and source references that must be removed from the solution and production projects.
2. Replace them with official NuGet package references and retarget affected editor-related projects to `net10.0` where needed for this task boundary.
3. Remove the obsolete solution entries for the local Avalonia editor projects if they are no longer needed by production code.
4. Make any minimal bootstrapping and XAML compatibility changes required for official-package restore/build if unsupported local-feed packages or outdated Avalonia APIs are still referenced.
5. Restore/build the affected scope to confirm the official-package dependency chain is valid before moving to UI-layer cleanup.
