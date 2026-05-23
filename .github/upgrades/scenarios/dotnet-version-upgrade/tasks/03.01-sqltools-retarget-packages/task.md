# 03.01-sqltools-retarget-packages: Retarget SqlTools and align incompatible packages

## Objective
Retarget `SqlTools` from `net8.0-windows` to `net10.0-windows` and update the explicitly incompatible package references identified by the assessment.

## Scope
- `SqlTools/SqlTools.csproj`
- Any immediate build fallout caused by the target framework and package version changes

## Steps
1. Update the target framework to `net10.0-windows`.
2. Change incompatible package references to supported versions.
3. Build `SqlTools` and capture the first real compile issues that remain after retargeting.

## Done When
- `SqlTools.csproj` targets `net10.0-windows`.
- Incompatible package references are updated to supported versions.
- The remaining compile issues are understood and documented for the next subtask.

## Research
- `SqlTools.csproj` currently targets `net8.0-windows` and builds successfully before retargeting.
- Assessment identified three explicitly incompatible packages for the .NET 10 target: `AvalonEdit`, `Caliburn.Micro`, and `Microsoft.Xaml.Behaviors.Wpf`.
- Supported package version lookup returned these versions for `net10.0-windows`: `AvalonEdit` `6.2.0.78`, `Caliburn.Micro` `4.0.230`, and `Microsoft.Xaml.Behaviors.Wpf` `1.1.39`.
- The project file contains project-specific properties directly in `SqlTools.csproj`, so retargeting should happen in this file rather than in shared props.

## Execution Notes
- Make the minimum csproj changes first.
- Use the next build to reveal the real code or XAML compatibility issues for follow-up work in `03.02-sqltools-compile-fixes`.
