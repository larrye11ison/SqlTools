# 03-sqltools-net10-compatibility: Upgrade the SqlTools desktop application

Upgrade `SqlTools` to .NET 10 and address the incompatible package and API issues identified in the assessment. This task covers the WPF application compatibility work, including target framework updates, package adjustments, and code fixes required by .NET 10.

Because this project carries most of the migration risk, it should happen after the Avalonia/editor dependency work stabilizes, keeping the WPF fixes isolated from the Avalonia modernization path.

**Done when**: `SqlTools` targets .NET 10, incompatible packages are resolved or replaced, required source and binary compatibility fixes are applied, and the application builds successfully.
