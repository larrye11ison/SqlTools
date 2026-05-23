# .NET Version Upgrade Plan

## Overview

### Selected Strategy
**Hybrid** — Solution segmented into 2 groups with per-group strategies.
**Rationale**: The solution is heterogeneous despite its small size. The Avalonia-based editor/UI projects are low-risk net8.0 libraries/apps that mainly need framework and package source alignment, while `SqlTools` is a medium-risk WPF application with incompatible packages and the vast majority of API issues. Grouping the Avalonia modernization separately from the WPF compatibility work keeps dependency replacement, MVVM cleanup, and final validation ordered without mixing unrelated risk profiles.

**Target**: Upgrade SqlTools to .NET 10, remove ReactiveUI in favor of CommunityToolkit.Mvvm, and replace local or forked Avalonia dependencies with official NuGet packages.
**Scope**: 4 projects, mixed desktop UI technologies, moderate overall migration risk concentrated in the WPF application.

## Tasks

### 01-official-avalonia-editor-dependencies: Replace local Avalonia editor dependencies

Replace the in-repo `external/AvaloniaEdit` dependency chain with official NuGet packages and align the editor-related projects on supported package references for .NET 10. This task covers the low-risk foundation needed to satisfy the requirement to rely only on official Avalonia packages rather than local or forked source.

This includes updating project references and package references for the Avalonia editor components so downstream UI work builds against the official distribution model instead of the checked-in source projects.

**Done when**: No production project depends on the local `external/AvaloniaEdit` projects, official NuGet packages are referenced instead, and the affected projects restore successfully on .NET 10.

---

### 02-sqlphanos-mvvm-toolkit-ui: Modernize the SqlPhanos Avalonia UI layer

Update `SqlPhanos` to target .NET 10 while removing ReactiveUI usage and ensuring the UI follows CommunityToolkit.Mvvm patterns cleanly. This task focuses on the Avalonia application layer, including view-model wiring, bindings, commands, and any syntax-highlighting integration required for scripted SQL definitions.

The goal is not just to swap packages, but to leave the Avalonia UI in a shape that matches MVVM Toolkit intent: observable state and commands in view models, minimal code-behind, and clear binding-driven interaction patterns.

**Done when**: `SqlPhanos` targets .NET 10, no ReactiveUI package or code usage remains in the project, CommunityToolkit.Mvvm patterns drive the UI, and scripted SQL syntax highlighting still works.

---

### 03-sqltools-net10-compatibility: Upgrade the SqlTools desktop application

Upgrade `SqlTools` to .NET 10 and address the incompatible package and API issues identified in the assessment. This task covers the WPF application compatibility work, including target framework updates, package adjustments, and code fixes required by .NET 10.

Because this project carries most of the migration risk, it should happen after the Avalonia/editor dependency work stabilizes, keeping the WPF fixes isolated from the Avalonia modernization path.

**Done when**: `SqlTools` targets .NET 10, incompatible packages are resolved or replaced, required source and binary compatibility fixes are applied, and the application builds successfully.

---

### 04-cross-project-validation: Validate the full upgraded solution

Run the final cross-project validation after the Avalonia dependency cleanup, SqlPhanos MVVM modernization, and SqlTools compatibility work are complete. This task ensures the solution restores, builds, and tests cleanly as a coherent .NET 10 codebase.

This validation task is also the checkpoint for confirming that the final dependency graph reflects the intended architecture: official Avalonia packages only, no ReactiveUI in the modernized UI layer, and no regressions in key editor or SQL-highlighting workflows.

**Done when**: The full solution restores and builds on .NET 10, relevant tests pass, and the final dependency/state checks confirm official Avalonia packages plus CommunityToolkit.Mvvm-only UI modernization goals.