# Copilot Instructions

## General Guidelines
- Do not ask for confirmation before proceeding with the requested code refinements when the work is clear and actionable. When the requested code change is clear, proceed with implementation directly and do not stop after announcing a plan step or ask for approval again.
- Do not interrupt execution after capturing memory preferences; when the user asks for a concrete code change, complete the implementation in the same turn before stopping.
- When expected SQL formatting values appear inconsistent, ask the user for clarification before making conflicting formatter code changes.

## Project Guidelines
- Syntax highlighting for scripted SQL definitions in SqlPhanos is mandatory. Avalonia is preferred for the implementation, but not required if another approach is more reliable.
- For SqlTools modernization work: remove ReactiveUI, use only CommunityToolkit.Mvvm patterns in the UI layer, target .NET 10, and use only official Avalonia NuGet packages instead of local or forked Avalonia code. Additionally, remove the local forked AvaloniaEdit and AvaloniaEdit.TextMate projects from the main solution and rely on official NuGet packages instead.
- For SqlPhanos connection profile persistence, load persisted connections from disk only once at startup, then save immediately on each profile/settings change; use simple JSON in AppData and never persist passwords.
- For the SQL formatting feature, preserve the original SQL exactly as defined in SQL Server, add a clear UI toggle between original and formatted views, and use intelligent SQL formatting rules with nested block indentation and 120-character-aware wrapping for long IN clauses. Stop using threshold-based nested-function formatting; instead, always explode parentheses vertically: put every opening parenthesis on a new line, indent contents one level deeper, and put every closing parenthesis on its own dedented line. Ensure that nested function calls are handled consistently in all contexts, including SELECT lists, JOIN ON clauses, WHERE clauses, and other expressions. Each introduced newline within nested function expressions should increase indentation relative to the parent call.
- For nested-function SQL formatting, use a 75-character threshold measured excluding leading whitespace; keep expressions/argument groups on one line when length is <=75.
- For this upgrade workflow, continue automatically through the remaining tasks until all four are complete unless blocked.
