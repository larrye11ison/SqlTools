# 01-official-avalonia-editor-dependencies Progress Detail

## Summary
Replaced the production solution's dependency on the local `external/AvaloniaEdit` source projects with official NuGet packages and updated `SqlPhanos` to build on `net10.0` using those package-based editor dependencies.

## What Changed
- Updated `SqlPhanos/SqlPhanos.csproj` to target `net10.0`.
- Added official editor packages `Avalonia.AvaloniaEdit` and `AvaloniaEdit.TextMate`.
- Removed unsupported `Avalonia.ReactiveUI` and `Avalonia.Diagnostics` references from `SqlPhanos` because the targeted official package versions were not available from the NuGet feeds in use.
- Added `Dock.Avalonia.Themes.Fluent` and switched `SqlPhanos/App.axaml` to use `DockFluentTheme` from the official Dock theme package.
- Removed the local Avalonia editor projects from `SqlTools.sln` so the main production solution no longer loads the forked source projects.
- Updated `SqlPhanos` startup and view code to stop relying on ReactiveUI bootstrapping where it blocked official-package restore:
  - `Program.cs` no longer calls `UseReactiveUI()`.
  - `Views/SearchView.axaml.cs` now inherits from `UserControl`.
  - `ViewModels/SearchResultsViewModel.cs` now uses MVVM Toolkit property change hooks instead of ReactiveUI `WhenAnyValue`.
- Updated Avalonia XAML to match current APIs required by the official package set:
  - `PlaceholderText` instead of obsolete `Watermark`.
  - `ScrollViewer.HorizontalScrollBarVisibility` / `ScrollViewer.VerticalScrollBarVisibility` attached properties.
- Removed the obsolete Avalonia binding-plugin workaround from `App.axaml.cs` because it no longer compiled against the current package set.

## Validation
- `dotnet restore SqlTools.sln` ✅
- `dotnet build SqlPhanos\SqlPhanos.csproj` ✅

## Notes
- Visual Studio still reports `AVLIC0001` mentioning `Avalonia.Controls.TreeDataGrid` on `SqlPhanos.csproj`, but CLI restore/build succeeds. This appears to be an IDE/analyzer issue rather than a blocking build failure for the current task.
- ReactiveUI removal is not fully complete across `SqlPhanos`; this task removed the dependency paths that blocked official-package adoption, while broader MVVM Toolkit cleanup remains in task `02-sqlphanos-mvvm-toolkit-ui`.
