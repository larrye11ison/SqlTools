# 02-sqlphanos-mvvm-toolkit-ui Progress Detail

## Summary
Completed the SqlPhanos MVVM Toolkit cleanup by confirming ReactiveUI is gone, removing the obsolete forked AvaloniaEdit submodule from the repository, and wiring the official AvaloniaEdit/TextMate packages into the SQL document view so scripted SQL now has syntax highlighting.

## What Changed
- Removed the forked `external/AvaloniaEdit` Git submodule from the repository and deleted `.gitmodules`.
- Updated `SqlPhanos/ViewModels/SqlDocumentViewModel.cs` to expose editor intent through a `SyntaxScopeName` property.
- Replaced the read-only `TextBox` in `SqlPhanos/Views/SqlDocumentView.axaml` with the official `AvaloniaEdit.TextEditor` control.
- Added TextMate setup in `SqlPhanos/Views/SqlDocumentView.axaml.cs` using the official package API:
  - installs TextMate through `AvaloniaEdit.TextMate.TextMate.InstallTextMate(...)`
  - chooses SQL grammar via the view-model scope name
  - switches editor theme between `ThemeName.DarkPlus` and `ThemeName.LightPlus`
  - disposes TextMate installation when the view detaches
- Kept MVVM responsibilities clean by leaving editor-specific host wiring in the view while the view model provides document content and syntax intent.
- Confirmed no remaining ReactiveUI usage exists under `SqlPhanos`.

## Validation
- `dotnet build SqlPhanos\SqlPhanos.csproj` ✅

## Notes
- Visual Studio still reports `AVLIC0001` on `SqlPhanos.csproj` mentioning `Avalonia.Controls.TreeDataGrid`, but CLI build succeeds. This remains a non-blocking IDE/analyzer issue for now.
- Because `AvaloniaEdit.TextEditor.Text` did not accept the available XAML binding forms in this setup, editor text synchronization is handled in code-behind while document state remains view-model-owned.
