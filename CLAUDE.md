# Agent Instructions

## Don't launch the app

Build and test only (`dotnet build`, `dotnet test`) after making changes. Do not start SqlPhanos.exe or any other project's executable — the user runs and verifies the app themselves.

## The formatter's round-trip safety net

`SqlCanonicalizationService.FormatForDisplayCore` (in `SqlPhanos.CodeFormatting/SqlCanonicalizationService.cs`) re-tokenizes its own output and verifies it represents the same real SQL as the input before returning it (see `IsRoundTripSafe` / `TryFindRoundTripMismatch`). This exists because the formatter has repeatedly been found to silently corrupt SQL — collapsing comments into dead code, gluing tokens together, stripping content out of string literals — almost always because some code path did raw text manipulation instead of working from ScriptDom's tokens. On a mismatch, the original unformatted text is returned instead (`SqlFormatResult.SafetyCheckPassed = false`), and callers must surface that to the user rather than silently showing corrupted output.

- Treat any weakening of this check (widening a tolerance, adding an exception) as a change that needs real justification — it exists specifically because formatter output that "looks fine" has repeatedly turned out not to be.
- When adding new formatter behavior, prefer working from the ScriptDom token stream over raw string/regex manipulation. Real bugs found in this project — keyword-shaped text matched inside comments, whitespace-trimming that reached into string literal content, line-ending normalization altering comment text — all trace back to code that treated SQL as plain text instead of parsed tokens.

## Anonymize real-world SQL before any of it touches a committed file

When the user pastes or references real SQL from their production systems — a stored proc, a bug repro, a warnings dump, anything from an actual database — and any part of it is going into a unit test, source comment, or any other file that will be committed:

- **Every identifier must be anonymized before it is written to disk**: table names, column names, variable names, database names, procedure/schema names, CTE names, and any alias that reveals something. This is not optional, and it is not limited to names that "look" obviously sensitive — assume all of it is.
- **Do not rely on manually eyeballing the SQL and renaming what looks identifying.** That approach has already failed twice in this project — real identifiers slipped through two separate review passes because they didn't stand out at a glance.
- **Instead, parse the sample with ScriptDom** (`TSql160Parser` / `GetTokenStream`, the same approach used throughout `SqlCanonicalizationService`) and use the token stream to drive the substitution: every `Identifier` / `QuotedIdentifier` / `Variable` token that is not a reserved keyword and not a recognized built-in function name (see `BuiltInFunctionNames`) is a candidate for anonymization. Map each distinct real identifier to a generic placeholder (`Table1`, `ColumnA`, `#TempB`, ...) consistently within the sample. This is far more reliable than manual review, since it can't miss an identifier just because it didn't look distinctive.
- **Comment text and string literal contents need the same scrutiny** as identifiers — a person's initials, a real date tied to a real incident, or a paraphrased business rule can be just as identifying as a table name.
- **Verify the anonymized version still exercises the same bug** (reformat it and confirm it fails/passes the round-trip check the same way the original did, or otherwise reproduces the behavior under test) before it goes into the test — anonymizing must not accidentally change what the test is actually covering.
