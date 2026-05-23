# 03.02-sqltools-compile-fixes: Fix SqlTools compile issues on .NET 10

## Objective
Resolve the source and binary compatibility issues that appear once `SqlTools` is retargeted to .NET 10.

## Scope
- WPF code-behind, view models, bootstrapper code, and any compatibility-related project adjustments inside `SqlTools`
- Focus on compile/build blockers rather than feature redesign

## Steps
1. Build the retargeted project and inspect the failing files.
2. Apply focused code fixes for .NET 10, package API changes, and WPF/MEF/Caliburn compatibility issues.
3. Rebuild until the project compiles cleanly.

## Done When
- `SqlTools` builds successfully on `net10.0-windows`.
- Compatibility fixes are applied only where needed and documented in task notes.

## Research
- After subtask `03.01`, `dotnet build SqlTools\\SqlTools.csproj` already succeeds on `net10.0-windows`.
- No compile-blocking C#, XAML, MEF, or Caliburn issues surfaced after retargeting and package alignment.
- The only remaining signals from the current build are non-blocking `NU1510` pruning warnings for some explicit BCL package references.

## Execution Notes
- Re-run build verification to confirm there are still no compile blockers.
- If the build remains clean, treat this subtask as complete with no code changes required.
