# Copilot Instructions

## Project Guidelines
- Syntax highlighting for scripted SQL definitions in SqlPhanos is mandatory. Avalonia is preferred for the implementation, but not required if another approach is more reliable.
- For SqlTools modernization work: remove ReactiveUI, use only CommunityToolkit.Mvvm patterns in the UI layer, target .NET 10, and use only official Avalonia NuGet packages instead of local or forked Avalonia code. Additionally, remove the local forked AvaloniaEdit and AvaloniaEdit.TextMate projects from the main solution and rely on official NuGet packages instead.
- For SqlPhanos connection profile persistence, load persisted connections from disk only once at startup, then save immediately on each profile/settings change; use simple JSON in AppData and never persist passwords.
- For this upgrade workflow, continue automatically through the remaining tasks until all four are complete unless blocked.