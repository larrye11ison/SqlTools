# 04-cross-project-validation: Validate the full upgraded solution

Run the final cross-project validation after the Avalonia dependency cleanup, SqlPhanos MVVM modernization, and SqlTools compatibility work are complete. This task ensures the solution restores, builds, and tests cleanly as a coherent .NET 10 codebase.

This validation task is also the checkpoint for confirming that the final dependency graph reflects the intended architecture: official Avalonia packages only, no ReactiveUI in the modernized UI layer, and no regressions in key editor or SQL-highlighting workflows.

**Done when**: The full solution restores and builds on .NET 10, relevant tests pass, and the final dependency/state checks confirm official Avalonia packages plus CommunityToolkit.Mvvm-only UI modernization goals.
