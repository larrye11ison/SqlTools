# .NET 8.0 Upgrade Plan

## Execution Steps

Execute steps below sequentially one by one in the order they are listed.

1. Validate that a .NET 8.0 SDK required for this upgrade is installed on the machine and if not, help to get it installed.
2. Ensure that the SDK version specified in global.json files is compatible with the .NET 8.0 upgrade.
3. Upgrade SqlTools\SqlTools.csproj to .NET 8.0

## Settings

This section contains settings and data used by execution steps.

### Aggregate NuGet packages modifications across all projects

NuGet packages used across all selected projects or their dependencies that need version update in projects that reference them.

| Package Name                                        | Current Version | New Version | Description                                                                      |
|:----------------------------------------------------|:---------------:|:-----------:|:---------------------------------------------------------------------------------|
| AvalonEdit                                          | 6.3.1.120       | 6.2.0.78    | Incompatible with .NET 8.0, downgrade to compatible version                      |
| Azure.Identity                                      | 1.11.4          | 1.17.1      | Deprecated - takes dependency on deprecated MSAL version                         |
| Caliburn.Micro                                      | 5.0.258         | 4.0.230     | Incompatible with .NET 8.0, downgrade to compatible version                      |
| Microsoft.Bcl.AsyncInterfaces                       | 10.0.0          | 8.0.0       | Recommended for .NET 8.0                                                         |
| Microsoft.Bcl.TimeProvider                          | 10.0.0          | 8.0.1       | Recommended for .NET 8.0                                                         |
| Microsoft.Data.SqlClient.SNI                        | 5.1.1           |             | No supported version found - will be removed                                     |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0        | 8.0.2       | Recommended for .NET 8.0                                                         |
| Microsoft.Extensions.Logging.Abstractions           | 10.0.0          | 8.0.3       | Recommended for .NET 8.0                                                         |
| Microsoft.Extensions.Options                        | 10.0.0          | 8.0.2       | Recommended for .NET 8.0                                                         |
| Microsoft.Extensions.Primitives                     | 10.0.0          | 8.0.0       | Recommended for .NET 8.0                                                         |
| Microsoft.Identity.Client                           | 4.61.3          | 4.79.2      | Deprecated - has API typo leading to potential security issue                    |
| Microsoft.Identity.Client.Extensions.Msal           | 4.61.3          | 4.79.2      | Deprecated - takes dependency on deprecated MSAL version                         |
| Microsoft.IdentityModel.Protocols                   | 6.35.0          | 8.15.0      | Deprecated - upgrade to LTS version                                              |
| Microsoft.IdentityModel.Protocols.OpenIdConnect     | 6.35.0          | 8.15.0      | Deprecated - upgrade to LTS version                                              |
| Microsoft.Xaml.Behaviors.Wpf                        | 1.1.135         | 1.1.39      | Incompatible with .NET 8.0, downgrade to compatible version                      |
| Newtonsoft.Json                                     | 13.0.1          | 13.0.4      | Recommended for .NET 8.0                                                         |
| PoorMansTSQLFormatter                               | 1.4.3.1         |             | No supported version found - will be removed                                     |
| Rx-Core                                             | 2.2.5           |             | Replace with System.Reactive.Core                                                |
| Rx-Interfaces                                       | 2.2.5           |             | Replace with System.Reactive.Interfaces                                          |
| Rx-Linq                                             | 2.2.5           |             | Replace with System.Reactive.Linq                                                |
| Rx-PlatformServices                                 | 2.2.5           |             | Replace with System.Reactive.PlatformServices                                    |
| Rx-Xaml                                             | 2.2.5           |             | Replace with System.Reactive.Linq (Xaml-specific package no longer needed)       |
| System.Buffers                                      | 4.6.1           |             | Included with .NET 8.0 framework - will be removed                               |
| System.Configuration.ConfigurationManager           | 6.0.1           | 8.0.1       | Recommended for .NET 8.0                                                         |
| System.Diagnostics.DiagnosticSource                 | 10.0.0          | 8.0.1       | Recommended for .NET 8.0                                                         |
| System.IdentityModel.Tokens.Jwt                     | 6.35.0          | 8.15.0      | Deprecated - upgrade to LTS version                                              |
| System.IO.FileSystem.AccessControl                  | 5.0.0           |             | Included with .NET 8.0 framework - will be removed                               |
| System.IO.Pipelines                                 | 10.0.0          | 8.0.0       | Recommended for .NET 8.0                                                         |
| System.Memory                                       | 4.6.3           |             | Included with .NET 8.0 framework - will be removed                               |
| System.Memory.Data                                  | 1.0.2           | 8.0.1       | Recommended for .NET 8.0                                                         |
| System.Numerics.Vectors                             | 4.6.1           |             | Included with .NET 8.0 framework - will be removed                               |
| System.Reactive.Core                                |                 | 6.0.1       | Replacement for Rx-Core                                                          |
| System.Reactive.Interfaces                          |                 | 6.0.0       | Replacement for Rx-Interfaces                                                    |
| System.Reactive.Linq                                |                 | 6.0.1       | Replacement for Rx-Linq and Rx-Xaml                                              |
| System.Reactive.PlatformServices                    |                 | 6.0.0       | Replacement for Rx-PlatformServices                                              |
| System.Runtime.InteropServices.RuntimeInformation   | 4.3.0           |             | Included with .NET 8.0 framework - will be removed                               |
| System.Security.AccessControl                       | 6.0.0           | 6.0.1       | Recommended for .NET 8.0                                                         |
| System.Security.Cryptography.ProtectedData          | 4.7.0           | 8.0.0       | Recommended for .NET 8.0                                                         |
| System.Security.Permissions                         | 6.0.0           | 8.0.0       | Recommended for .NET 8.0                                                         |
| System.Security.Principal.Windows                   | 5.0.0           |             | Included with .NET 8.0 framework - will be removed                               |
| System.Text.Encoding                                | 4.3.0           |             | Included with .NET 8.0 framework - will be removed                               |
| System.Text.Encodings.Web                           | 10.0.0          | 8.0.0       | Recommended for .NET 8.0                                                         |
| System.Text.Json                                    | 10.0.0          | 8.0.6       | Recommended for .NET 8.0                                                         |
| System.Threading.Tasks.Extensions                   | 4.6.3           |             | Included with .NET 8.0 framework - will be removed                               |
| System.ValueTuple                                   | 4.6.1           |             | Included with .NET 8.0 framework - will be removed                               |

### Project upgrade details

This section contains details about each project upgrade and modifications that need to be done in the project.

#### SqlTools\SqlTools.csproj modifications

Project conversion:
  - Project file needs to be converted from old .NET Framework format to SDK-style format

Project properties changes:
  - Target framework should be changed from `net48` to `net8.0-windows`

NuGet packages changes:
  - AvalonEdit should be updated from `6.3.1.120` to `6.2.0.78` (*incompatible with .NET 8.0*)
  - Azure.Identity should be updated from `1.11.4` to `1.17.1` (*deprecated package*)
  - Caliburn.Micro should be updated from `5.0.258` to `4.0.230` (*incompatible with .NET 8.0*)
  - Microsoft.Bcl.AsyncInterfaces should be updated from `10.0.0` to `8.0.0` (*recommended for .NET 8.0*)
  - Microsoft.Bcl.TimeProvider should be updated from `10.0.0` to `8.0.1` (*recommended for .NET 8.0*)
  - Microsoft.Data.SqlClient.SNI should be removed (*no supported version found*)
  - Microsoft.Extensions.DependencyInjection.Abstractions should be updated from `10.0.0` to `8.0.2` (*recommended for .NET 8.0*)
  - Microsoft.Extensions.Logging.Abstractions should be updated from `10.0.0` to `8.0.3` (*recommended for .NET 8.0*)
  - Microsoft.Extensions.Options should be updated from `10.0.0` to `8.0.2` (*recommended for .NET 8.0*)
  - Microsoft.Extensions.Primitives should be updated from `10.0.0` to `8.0.0` (*recommended for .NET 8.0*)
  - Microsoft.Identity.Client should be updated from `4.61.3` to `4.79.2` (*deprecated package*)
  - Microsoft.Identity.Client.Extensions.Msal should be updated from `4.61.3` to `4.79.2` (*deprecated package*)
  - Microsoft.IdentityModel.Protocols should be updated from `6.35.0` to `8.15.0` (*deprecated package*)
  - Microsoft.IdentityModel.Protocols.OpenIdConnect should be updated from `6.35.0` to `8.15.0` (*deprecated package*)
  - Microsoft.Xaml.Behaviors.Wpf should be updated from `1.1.135` to `1.1.39` (*incompatible with .NET 8.0*)
  - Newtonsoft.Json should be updated from `13.0.1` to `13.0.4` (*recommended for .NET 8.0*)
  - PoorMansTSQLFormatter should be removed (*no supported version found*)
  - Rx-Core should be removed and replaced with System.Reactive.Core 6.0.1
  - Rx-Interfaces should be removed and replaced with System.Reactive.Interfaces 6.0.0
  - Rx-Linq should be removed and replaced with System.Reactive.Linq 6.0.1
  - Rx-PlatformServices should be removed and replaced with System.Reactive.PlatformServices 6.0.0
  - Rx-Xaml should be removed and replaced with System.Reactive.Linq 6.0.1
  - System.Buffers should be removed (*included with .NET 8.0 framework*)
  - System.Configuration.ConfigurationManager should be updated from `6.0.1` to `8.0.1` (*recommended for .NET 8.0*)
  - System.Diagnostics.DiagnosticSource should be updated from `10.0.0` to `8.0.1` (*recommended for .NET 8.0*)
  - System.IdentityModel.Tokens.Jwt should be updated from `6.35.0` to `8.15.0` (*deprecated package*)
  - System.IO.FileSystem.AccessControl should be removed (*included with .NET 8.0 framework*)
  - System.IO.Pipelines should be updated from `10.0.0` to `8.0.0` (*recommended for .NET 8.0*)
  - System.Memory should be removed (*included with .NET 8.0 framework*)
  - System.Memory.Data should be updated from `1.0.2` to `8.0.1` (*recommended for .NET 8.0*)
  - System.Numerics.Vectors should be removed (*included with .NET 8.0 framework*)
  - System.Runtime.InteropServices.RuntimeInformation should be removed (*included with .NET 8.0 framework*)
  - System.Security.AccessControl should be updated from `6.0.0` to `6.0.1` (*recommended for .NET 8.0*)
  - System.Security.Cryptography.ProtectedData should be updated from `4.7.0` to `8.0.0` (*recommended for .NET 8.0*)
  - System.Security.Permissions should be updated from `6.0.0` to `8.0.0` (*recommended for .NET 8.0*)
  - System.Security.Principal.Windows should be removed (*included with .NET 8.0 framework*)
  - System.Text.Encoding should be removed (*included with .NET 8.0 framework*)
  - System.Text.Encodings.Web should be updated from `10.0.0` to `8.0.0` (*recommended for .NET 8.0*)
  - System.Text.Json should be updated from `10.0.0` to `8.0.6` (*recommended for .NET 8.0*)
  - System.Threading.Tasks.Extensions should be removed (*included with .NET 8.0 framework*)
  - System.ValueTuple should be removed (*included with .NET 8.0 framework*)

Other changes:
  - app.config file will no longer be needed after migration to .NET 8.0 (assembly binding redirects are handled automatically)
  - packages.config file will be removed as SDK-style projects use PackageReference format