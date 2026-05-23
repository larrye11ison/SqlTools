# 04-cross-project-validation: Validate the full upgraded solution

Run the final cross-project validation after the Avalonia dependency cleanup, SqlPhanos MVVM modernization, and SqlTools compatibility work are complete. This task ensures the solution restores, builds, and tests cleanly as a coherent .NET 10 codebase.

This validation task is also the checkpoint for confirming that the final dependency graph reflects the intended architecture: official Avalonia packages only, no ReactiveUI in the modernized UI layer, and no regressions in key editor or SQL-highlighting workflows.

**Done when**: The full solution restores and builds on .NET 10, relevant tests pass, and the final dependency/state checks confirm official Avalonia packages plus CommunityToolkit.Mvvm-only UI modernization goals.

## Research
- `SqlPhanos` builds successfully on `net10.0` with official Avalonia packages and TextMate-based SQL syntax highlighting.
- `SqlTools` builds successfully on `net10.0-windows` after package and target framework alignment.
- The forked `AvaloniaEdit` source projects and submodule have been removed from the modernization path.
- The remaining known issues are non-blocking: `NU1510` pruning warnings in CLI builds and a Visual Studio-only `AVLIC0001` analyzer warning on `SqlPhanos.csproj`.

## Execution Notes
- Run full-solution restore/build validation.
- Re-check for remaining ReactiveUI usage and any lingering local/forked Avalonia references.
- Check for discoverable tests; if none are present, document that outcome explicitly.
