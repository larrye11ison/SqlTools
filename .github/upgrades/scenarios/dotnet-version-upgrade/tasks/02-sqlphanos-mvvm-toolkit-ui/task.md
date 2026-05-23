# 02-sqlphanos-mvvm-toolkit-ui: Modernize the SqlPhanos Avalonia UI layer

Update `SqlPhanos` to target .NET 10 while removing ReactiveUI usage and ensuring the UI follows CommunityToolkit.Mvvm patterns cleanly. This task focuses on the Avalonia application layer, including view-model wiring, bindings, commands, and any syntax-highlighting integration required for scripted SQL definitions.

The goal is not just to swap packages, but to leave the Avalonia UI in a shape that matches MVVM Toolkit intent: observable state and commands in view models, minimal code-behind, and clear binding-driven interaction patterns.

**Done when**: `SqlPhanos` targets .NET 10, no ReactiveUI package or code usage remains in the project, CommunityToolkit.Mvvm patterns drive the UI, and scripted SQL syntax highlighting still works.

## Research
- There are no remaining `ReactiveUI`, `RxApp`, `WhenAnyValue`, `ReactiveUserControl`, or `UseReactiveUI` references under `SqlPhanos` after task 01 cleanup.
- The local forked `external/AvaloniaEdit` directory was a Git submodule, not just loose source. It has now been removed from the repository working tree because the main solution is package-based.
- Repository-wide search showed remaining `AvaloniaEdit` references were confined to the forked submodule itself plus the official package references already added to `SqlPhanos.csproj`.
- `SqlPhanos` still lacks actual syntax-highlighting integration in `SqlDocumentView`; the current document view is a read-only `TextBox`, so the remaining work in this task is to wire the official editor packages into the UI using MVVM-friendly patterns.
- Official package discovery confirms `AvaloniaEdit.TextMate.TextMate.InstallTextMate(AvaloniaEdit.TextEditor, TextMateSharp.Registry.IRegistryOptions, bool, Action<Exception>)` is the entry point for syntax highlighting.
- `TextMateSharp.Grammars.RegistryOptions` supports `GetLanguageByExtension`, `GetScopeByExtension`, and `LoadTheme`, which is enough to select a SQL grammar by file extension and keep the setup localized to the view.

## Execution Plan
1. Remove the obsolete forked AvaloniaEdit submodule from the repository now that the main solution is package-based.
2. Verify `SqlPhanos` is free of remaining ReactiveUI code and package usage.
3. Integrate the official Avalonia editor packages into `SqlDocumentView` so scripted SQL definitions regain syntax highlighting.
4. Keep view logic thin by driving editor state from `SqlDocumentViewModel` and other MVVM Toolkit view models.
5. Build `SqlPhanos` to validate the MVVM Toolkit-only UI path on `net10.0`.
