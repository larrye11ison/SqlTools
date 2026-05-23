# 02-sqlphanos-mvvm-toolkit-ui: Modernize the SqlPhanos Avalonia UI layer

Update `SqlPhanos` to target .NET 10 while removing ReactiveUI usage and ensuring the UI follows CommunityToolkit.Mvvm patterns cleanly. This task focuses on the Avalonia application layer, including view-model wiring, bindings, commands, and any syntax-highlighting integration required for scripted SQL definitions.

The goal is not just to swap packages, but to leave the Avalonia UI in a shape that matches MVVM Toolkit intent: observable state and commands in view models, minimal code-behind, and clear binding-driven interaction patterns.

**Done when**: `SqlPhanos` targets .NET 10, no ReactiveUI package or code usage remains in the project, CommunityToolkit.Mvvm patterns drive the UI, and scripted SQL syntax highlighting still works.
