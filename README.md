# SqlPhanos

*(formerly SqlTools)*

A Windows desktop app (built on Avalonia UI, .NET 10) for searching, browsing, scripting, and bulk-exporting objects on Microsoft SQL Servers — plus a built-in ad-hoc query tool that exports results straight to Excel.

SqlPhanos started as a focused "find the object I'm looking for" tool. It's grown into a much broader SQL Server workbench: live object scripting with dependent-object discovery, encrypted-object decryption, CLR object decompilation, whole-database bulk export with delta detection, and query-to-Excel export — all built around a ScriptDom-based SQL formatter with a round-trip safety net that refuses to hand you corrupted output.

<!-- SCREENSHOT: Main window — connection panel + search results grid + an open script tab, showing the overall layout -->

## Why does this exist?

In day-to-day prod support work across servers with dozens of databases and thousands of objects, the recurring need was: find a stored proc/view/table by name (often without remembering its full name *or* which database it's in), see its definition, and understand what's around it — without hand-writing the same `sys.objects` query for the hundredth time, and without the search UI in SQL Server Management Studio getting in the way.

SqlPhanos does that search across every database on a server at once, then keeps growing to cover the other things that search usually leads to next: scripting the object out, checking what depends on it, exporting a whole database's worth of objects for a diff or a rebuild, and running an ad-hoc query without leaving the app.

## Features

### Search

* Search by Object Name, Schema Name, and/or Object Definition (matches inside object bodies, including column names).
* Searches **every user database on the server concurrently**, with per-database failure isolation — one bad database doesn't abort the rest of the search.
* Finds tables, views, stored procedures, scalar/table-valued/inline table-valued functions, triggers, sequences, user-defined table types, CLR-backed equivalents of the above, and more (constraints, rules, synonyms, service broker queues, plan guides).
* Client-side filtering on top of results: a general filter box plus dedicated Name/Schema/DB/Type filter boxes, with `!` to negate a term.
* Two results views — a card list or a full sortable/resizable data grid — toggle with `Ctrl+R`.

<!-- SCREENSHOT: Search panel with a set of results in the data grid view, filter boxes visible -->

### Script viewing

* Click "Script" to script an object live from the server into its own tab, with cancel support and a refresh button.
* Toggle between the server's **Original** script and a **Reformatted** version (`Ctrl+M`) — caret position is preserved across the toggle.
* Syntax highlighting and incremental find-in-document.
* **Dependent objects panel** — after scripting an object, related objects (e.g. triggers on a table) appear as clickable buttons that open their own tabs.
* **Encrypted object support** — `WITH ENCRYPTION` objects can be decrypted in place (read-only, via a Dedicated Administrator Connection) after an explicit consent prompt; nothing is altered on the server.
* **CLR object support** — CLR-backed procs/functions/triggers show both their thin T-SQL wrapper and a decompiled C# view of the actual implementation, with a "Save As DLL..." export.

<!-- SCREENSHOT: A scripted object tab showing Original/Reformatted toggle and the dependent-objects button strip -->

### Script Databases (bulk export)

Export every object in one or more databases to individual `.sql` files on disk:

* Pick a connection, pick an output folder, check off any subset of the server's databases, and run — with live per-database progress and elapsed time.
* **Delta or full re-export** — if the output folder already has content, choose to re-script only what changed since last run (based on SQL Server's own modify metadata) or reset and start clean.
* Optional **reformat all scripted code** through the same formatter used in the code viewer.
* Optional **normalize auto-generated constraint names** (e.g. `PK__Orders__3213E83F...` → `PK_Orders_OrderID`) so scheduled exports don't churn on cosmetic renames.
* Triggers are scripted as their own standalone files, matching how they're treated everywhere else in the app.
* Explicit object-level **GRANT/DENY permissions** and full **DRI** (keys, indexes, defaults, checks) are included in every script.
* CLR assemblies backing scripted objects are exported once each, as both `.dll` and decompiled `.cs`.
* A live in-tab warnings list surfaces any object where the formatter's safety check rejected its own output — with a "Copy All" button for bug reports.

<!-- SCREENSHOT: Script Databases tab mid-run, showing per-database progress bars and the database checklist -->

### New Query (QueryXLerator)

An ad-hoc query tab, powered by the SqlPhanos.QueryXLerator engine:

* Free-form SQL editor against the selected connection, with the same reformat button (`Ctrl+M`) as the code viewer.
* **Execute** runs the query and writes every result set straight to an **XLSX workbook** — one worksheet per result set — with a configurable table style.
* Column header suffixes control the output: `/sum`, `/average`, etc. add a totals-row aggregate; `/$` and `/%` apply currency/percent formatting; a column named `__tabname__` sets the worksheet's tab name instead of being written as data.

<!-- SCREENSHOT: A New Query tab with results and the resulting XLSX file's formatting -->

### Other

* Multiple saved connection profiles (Windows Auth or SQL login, with an optional Trust Server Certificate toggle).
* Configurable editor font family/size, and a setting for whether opening parens in column/parameter lists go on their own line.
* Version shown in the title bar (derived from git tags via MinVer); an in-app "Update Available" notice checks GitHub Releases and can download/install/relaunch a new version for you.

## The formatter's safety net

SqlPhanos's SQL formatter is built on `Microsoft.SqlServer.TransactSql.ScriptDom` and works from the real token stream rather than raw text manipulation. Every time it formats something, it re-tokenizes its own output and verifies it represents the *same SQL* as the input before showing it to you. If that check fails, you get the original, unformatted text back instead — with a clear notice that formatting was skipped rather than silently correcting output that might be corrupted. This applies everywhere formatting happens: the code viewer, ad-hoc queries, and bulk database export.

## Requirements

* Windows 10/11 (the app targets Windows and publishes a `win-x64` build; there's no current Linux/macOS support).
* .NET 10.
* A Microsoft SQL Server instance to connect to.

## Status

SqlPhanos is under active development. Expect rough edges in places — if the formatter ever produces something that looks wrong, its round-trip safety net should catch it and fall back to the original text rather than show you corrupted SQL, but please report anything that looks off.
