# SqlLense

[![Proudly Vibe Coded - Neon Flame](https://vibecoded.fyi/badges/terminal/main/proudly-vibe-coded-neon-flame.svg)](https://vibecoded.fyi/)

A Visual Studio 2022 extension (and Roslyn analyzer) that finds **T-SQL inside ordinary C# strings**
and checks it while you type. You don't need attributes or special types.

- **Syntax errors** get red squiggles on the exact offending token, including inside verbatim, raw
  (`"""`), interpolated and concatenated strings.
- **Schema errors** cover unknown tables, views, columns, aliases, stored procedures and procedure
  parameters, checked against a cached snapshot of your database.
- **Quick fixes** offer *Did you mean `Customers`?* (keeping `[bracket]` quoting and schema-qualifying
  names when needed) and *Ignore SQL in this string*.
- **Completion** suggests tables after `FROM`/`JOIN`, alias-aware columns after `c.`, schema members,
  and procedures after `EXEC`.
- **Syntax highlighting** colors SQL keywords, identifiers, functions, variables, numbers and comments
  inside C# strings.
- **Quick Info** shows a table's columns, a column's type and nullability, and a procedure's parameters
  on hover.

```csharp
const string Sql = """
    SELECT c.Name, o.Totl
    FROM Customers c
    JOIN Orders o ON o.CustomerId = c.Id
    """;
// SQLL003 on "Totl": Invalid column name 'Totl'. Did you mean 'Total'?

var n = conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM Custmers WHERE Region = {region}");
// SQLL002 on "Custmers": Invalid object name 'Custmers'. Did you mean 'Customers'?
```

## How SQL is detected

No markers are needed. A string is treated as SQL when:

1. it starts with a statement keyword (`SELECT`, `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `WITH`,
   `EXEC`, `CREATE`/`ALTER`/`DROP`, `TRUNCATE`, `DECLARE`, `SET`, `IF`, `BEGIN`), after skipping whitespace,
   comments, `(` and `;`;
2. that keyword is written **ALL UPPER** or **all lower** case. Sentence case such as `"Select an item"`
   reads as UI text;
3. the statement has the shape its keyword requires, for example `SELECT … FROM`, `UPDATE … SET`,
   `INSERT INTO`, `DELETE FROM`, `EXEC name`, or `WITH x AS (`.

Rules 2 and 3 are skipped when the string clearly goes to SQL:

- it is an argument to Dapper (`Query*`, `Execute*`), EF Core (`FromSqlRaw`, `ExecuteSqlRaw`,
  `SqlQuery`, …) or `new SqlCommand(...)`;
- it is assigned to `CommandText`;
- it initializes a variable, field or property whose name ends in `Sql` or `Query`.

Comments control detection explicitly:

```csharp
var q = /*lang=sql*/ "Select * From Users";   // force: analyze this string as SQL
// sqllense:ignore
var s = "SELECT whatever, this is intentionally not valid";   // opt out (a quick fix inserts this)
```

**Test projects are skipped.** Their SQL is often deliberately invalid or written against fixtures, so
no diagnostics are reported in a project with `IsTestProject=true` (set by `Microsoft.NET.Test.Sdk`) or
one that references xUnit, NUnit, MSTest or TUnit. Set `SqlLenseAnalyzeTests=true` to analyze them anyway.

**Interpolation holes and non-constant concatenation operands** become placeholders. A hole in a value
position becomes a parameter, and one in a name position (`FROM {table}`) is never validated. Syntax
errors next to a placeholder are suppressed. Constant operands (`const string Columns = "Id, Name";`)
are resolved and validated.

## Getting started (Visual Studio)

1. Install the VSIX. Build it from this repo (see below), or download the `vsix` artifact from CI.
2. Open your solution and run **Tools > SqlLense: Configure Connection...**. Enter a connection
   string, for example `Server=localhost;Database=Shop;Integrated Security=true;TrustServerCertificate=true`,
   and optionally a password (stored separately and masked).
3. Run **Tools > SqlLense: Refresh Database Schema**. It is also on the solution's right-click menu.
   You can also enable *Refresh schema when the solution opens*.

Syntax checking works immediately without a database; schema checks start once a snapshot exists.

### Where settings and schemas live

Nothing is written to your repository.

| What | Where |
|---|---|
| Connection settings | VS per-user settings store, keyed by solution folder. The connection string and password are encrypted with DPAPI for your Windows account. |
| Schema snapshot | `%LOCALAPPDATA%\SqlLense\schemas\<solution-folder>-<hash>\<database>.json` |
| Snapshot root override | environment variable `SQLLENSE_SCHEMA_DIR` |

The analyzer finds the snapshot from the solution folder, the nearest folder above the source files
that contains a `.sln`/`.slnx`. Every host therefore resolves the same file: the IDE, the out-of-process
analyzer host and `dotnet build`.

### Command line (`sqllense` tool)

The CLI uses `Microsoft.Data.SqlClient`, so every authentication mode works, including Entra ID
(`Authentication=Active Directory Default`). The VSIX deliberately uses the SQL client built into
.NET Framework, so it never loads extra SQL client binaries into `devenv.exe`.

```sh
dotnet pack src/SqlLense.Cli -o artifacts && dotnet tool install -g SqlLense.Cli --add-source artifacts
sqllense refresh --connection "Server=...;Database=Shop;Authentication=Active Directory Default"
sqllense where                     # prints the snapshot path for the current solution
```

## Diagnostics

| Id | Meaning |
|---|---|
| SQLL001 | SQL syntax error |
| SQLL002 | Unknown table, view or function |
| SQLL003 | Unknown column |
| SQLL004 | Qualifier (alias/table) that is not in scope, e.g. `x.Id` |
| SQLL005 | Ambiguous column |
| SQLL006 | Unknown stored procedure (system `sp_`/`xp_` procedures are ignored) |
| SQLL007 | Unknown stored procedure parameter |

All diagnostics default to *error*. Change severity per rule in `.editorconfig`, for example
`dotnet_diagnostic.SQLL005.severity = warning`.

Validation is conservative. It stays silent when columns cannot be known, for example with temp tables
created elsewhere, table-valued functions without metadata, `OPENJSON`, `PIVOT`, cross-database or linked
server names, `sys.*` and `INFORMATION_SCHEMA.*`. CTEs, derived tables, table variables, `#temp` tables
created in the same string, `inserted`/`deleted`, `UPDATE alias … FROM`, `MERGE` and `ORDER BY` select
aliases are all understood.

## Command-line builds and CI

Reference the `SqlLense` NuGet package (`dotnet pack src/SqlLense.Package`) to get the same diagnostics
from `dotnet build`. Use either the package or the VSIX in a given Visual Studio project, not both,
otherwise diagnostics are reported twice.

MSBuild properties (surfaced to the analyzer by the package):

| Property | Effect |
|---|---|
| `SqlLenseEnabled` | `false` disables analysis for the project |
| `SqlLenseSchemaPath` | explicit snapshot file, e.g. one produced in CI by `sqllense refresh --out` |
| `SqlLenseDatabase` | snapshot name for projects that use a different database (default `default`) |
| `SqlLenseAnalyzeTests` | `true` analyzes test projects too (skipped by default) |

## Performance

The design goal is that you never feel it while typing.

- **Cheap rejection:** almost every string is dismissed after an allocation-free check of its leading
  keyword (~50 ns), before any SQL work is done.
- **Parse cache:** parsing is memoized per SQL text, and validation per SQL text and snapshot. Editing
  one method never re-parses the SQL in another.
- **Schema cache:** the snapshot is loaded once, indexed with hash lookups, and only re-read when the
  file's timestamp changes (checked at most every 2 seconds). No network access happens during analysis.
- **Mostly syntactic:** the semantic model is only touched to resolve `const` operands of concatenations
  that already look like SQL. Source maps from SQL offsets back to C# positions are built lazily, only
  when a diagnostic is actually reported.
- **Highlighting off the UI thread:** highlighting and Quick Info run on a debounced background thread
  using Roslyn's incrementally parsed syntax tree. Tags are translated forward to newer snapshots, so
  typing never waits on analysis.

`benchmarks/SqlLense.Benchmarks` (BenchmarkDotNet, `--job short`, Linux container):

| Scenario | Result |
|---|---|
| Analyze one CTE/JOIN statement, uncached (parse + validate) | ~0.43 ms |
| Same statement, cached | ~125 ns, 88 B |
| Reject a prose string (`"Select an item from the list"`) | ~52 ns, 0 B |
| Full analyzer pass, 20k-line file with 2,000 SQL strings | ~370 ms, vs ~300 ms for a no-op analyzer on the same file (the Roslyn baseline) |
| Recompute highlighting regions for that file | ~26 ms (background, debounced) |

In the IDE, analyzers run per changed document, so ordinary files cost a small fraction of this.

## Repository layout

| Project | Target | Purpose |
|---|---|---|
| `src/SqlLense.Core` | netstandard2.0 | T-SQL parsing (Microsoft ScriptDom), schema model and snapshot format, validation, detection heuristics, classification, completion engine |
| `src/SqlLense.Analyzers` | netstandard2.0 | Roslyn analyzer: SQL extraction from C# expressions, source mapping, diagnostics |
| `src/SqlLense.CodeFixes` | netstandard2.0 | Code fixes, completion provider, editor services (highlighting and Quick Info logic) |
| `src/SqlLense.SchemaReader` | netstandard2.0 | Reads a SQL Server catalog over any `DbConnection` |
| `src/SqlLense.Vsix` | net472 | Visual Studio package: options, commands, classifier, Quick Info |
| `src/SqlLense.Cli` | net8.0 | `sqllense` dotnet tool |
| `src/SqlLense.Package` | — | Packs the `SqlLense` NuGet analyzer package |

## Building

```sh
dotnet test tests/SqlLense.Core.Tests
dotnet test tests/SqlLense.Analyzers.Tests
```

The VSIX C# compiles on any OS (`dotnet build src/SqlLense.Vsix`). Producing the `.vsix` and debugging
it needs Windows with Visual Studio 2022 and the *Visual Studio extension development* workload. Open
`SqlLense.sln`, set `SqlLense.Vsix` as the startup project and press F5 to launch the experimental
instance (`/rootSuffix Exp`).

## Limitations and roadmap

- T-SQL (SQL Server) only. The dialect-specific code is isolated in `SqlLense.Core`.
- One database per solution in the UI. The snapshot format, locator and `SqlLenseDatabase` property
  already support several; the options page will grow a per-project mapping.
- SQL built across statements (`StringBuilder`, `+=`) is not analyzed.
- Razor (`.cshtml`/`.razor`) buffers are not highlighted.
- Highlighting uses fixed mid-tone defaults that work on light and dark themes. Customize them under
  *Fonts and Colors > "SqlLense - SQL …"*.
