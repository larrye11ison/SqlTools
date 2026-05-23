# 03.03-sqltools-validation: Validate the upgraded SqlTools application

## Objective
Validate the upgraded `SqlTools` application after retargeting and compatibility fixes.

## Scope
- Project-level restore/build validation for `SqlTools`
- Any relevant test discovery or follow-up checks available in the workspace

## Steps
1. Run final restore/build validation for `SqlTools`.
2. Run any relevant automated tests if available.
3. Record the validation outcome and any accepted non-blocking issues.

## Done When
- `SqlTools` restores and builds successfully on .NET 10.
- Relevant tests are run or their absence is documented.
- The task has a clear validation record for the final cross-project step.

## Research
- `SqlTools` already builds successfully on `net10.0-windows` after retargeting and package alignment.
- The remaining build output currently consists of non-blocking `NU1510` package pruning warnings.
- Test discovery is required to determine whether this solution includes any runnable automated tests for `SqlTools`.

## Execution Notes
- Re-run restore/build validation from the upgraded state.
- If no tests are present, document that clearly instead of inventing extra validation work.
