# .NET Version Upgrade Progress

## Overview

Upgrade SqlTools to .NET 10 while replacing local Avalonia editor source dependencies with official NuGet packages and removing ReactiveUI from the Avalonia UI layer. The work is organized as a hybrid plan that separates low-risk Avalonia modernization from higher-risk WPF compatibility work before final validation.

**Progress**: 0/4 tasks complete (0%) ![0%](https://progress-bar.xyz/0)

## Tasks

- 🔄 01-official-avalonia-editor-dependencies: Replace local Avalonia editor dependencies
- 🔲 02-sqlphanos-mvvm-toolkit-ui: Modernize the SqlPhanos Avalonia UI layer
- 🔲 03-sqltools-net10-compatibility: Upgrade the SqlTools desktop application
- 🔲 04-cross-project-validation: Validate the full upgraded solution