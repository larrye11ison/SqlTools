# 01-official-avalonia-editor-dependencies: Replace local Avalonia editor dependencies

Replace the in-repo `external/AvaloniaEdit` dependency chain with official NuGet packages and align the editor-related projects on supported package references for .NET 10. This task covers the low-risk foundation needed to satisfy the requirement to rely only on official Avalonia packages rather than local or forked source.

This includes updating project references and package references for the Avalonia editor components so downstream UI work builds against the official distribution model instead of the checked-in source projects.

**Done when**: No production project depends on the local `external/AvaloniaEdit` projects, official NuGet packages are referenced instead, and the affected projects restore successfully on .NET 10.
