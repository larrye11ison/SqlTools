# 03.01-sqltools-retarget-packages Progress Detail

## Summary
Retargeted `SqlTools` to `net10.0-windows` and updated the explicitly incompatible package references identified by the assessment.

## What Changed
- Updated `SqlTools/SqlTools.csproj` target framework from `net8.0-windows` to `net10.0-windows`.
- Changed incompatible package references to supported versions:
  - `AvalonEdit` → `6.2.0.78`
  - `Caliburn.Micro` → `4.0.230`
  - `Microsoft.Xaml.Behaviors.Wpf` → `1.1.39`
- Left the rest of the package graph unchanged for this subtask to minimize change scope.

## Validation
- `dotnet build SqlTools\SqlTools.csproj` ✅

## Notes
- The retargeted project built successfully immediately after the framework and package updates.
- The build emitted `NU1510` pruning warnings for several explicit BCL package references, but these are warnings only and do not block the upgrade.
- Because the project now builds on `net10.0-windows`, the follow-up compile-fix subtask is effectively reduced to verification rather than emergency remediation.
