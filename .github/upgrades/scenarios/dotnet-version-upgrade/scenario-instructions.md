## Scenario
- **Scenario ID**: dotnet-version-upgrade
- **Goal**: Upgrade SqlTools to .NET 10 while removing ReactiveUI in favor of CommunityToolkit.Mvvm and replacing local or forked Avalonia dependencies with official NuGet packages.

## Strategy
- **Selected**: Hybrid
- **Rationale**: Avalonia editor and SqlPhanos work are low-risk modernization tasks, while `SqlTools` carries most of the .NET 10 compatibility risk because of its incompatible packages and concentrated WPF API breakage.

### Execution Constraints
- Complete official Avalonia dependency replacement before UI-layer cleanup that depends on editor integration.
- Finish Avalonia and SqlPhanos modernization before starting the higher-risk `SqlTools` compatibility work.
- Validate each project group before moving to the next dependent group.
- Run full cross-project restore/build validation after all groups complete.

## Preferences
### Flow Mode
- **Mode**: Automatic
- **Commit Strategy**: After Each Phase

### Source Control
- **Repository Root**: `D:\code\SqlTools`
- **Source Branch**: `reimplement-using-avalonia`
- **Working Branch**: `upgrade-to-NET10`
- **Pending Changes Handling**: Committed before starting scenario (`Save work before starting dotnet-version-upgrade`)

## User Preferences
### Technical Preferences
- **Target Framework**: .NET 10 (`net10.0`)
- **MVVM Framework**: Remove ReactiveUI and use only CommunityToolkit.Mvvm
- **UI Architecture**: Ensure the UI layer properly follows MVVM patterns as intended by CommunityToolkit.Mvvm
- **Avalonia Dependencies**: Use only official Avalonia NuGet packages from NuGet; do not rely on local or forked Avalonia code
- **SqlPhanos Requirement**: Syntax highlighting for scripted SQL definitions in SqlPhanos is mandatory; Avalonia is preferred unless another approach is more reliable

## Key Decisions Log
- Initialized the .NET version upgrade workflow on branch `upgrade-to-NET10` targeting .NET 10 with CommunityToolkit.Mvvm-only UI guidance and official Avalonia NuGet packages.
- Selected the Hybrid strategy so Avalonia dependency/UI modernization can complete before the higher-risk WPF compatibility work in `SqlTools`.
