# SqlLense

[![Proudly Vibe Coded - Neon Flame](https://vibecoded.fyi/badges/terminal/main/proudly-vibe-coded-neon-flame.svg)](https://vibecoded.fyi/)

A Visual Studio 2022 extension (and Roslyn analyzer) that finds **T-SQL inside ordinary C# strings**
and checks it while you type. You don't need attributes or special types.

- **Syntax errors** get red squiggles on the exact offending token, including inside verbatim, raw
  (`"""`), interpolated, concatenated and `string.Format` strings, and SQL built up with `+=` or a
  `StringBuilder`.
- **Schema errors** cover unknown tables, views, columns, aliases, stored procedures and procedure
  parameters, checked against a cached snapshot of your database.
- **Quick fixes** offer *Did you mean `Customers`?* (keeping `[bracket]` quoting and schema-qualifying
  names when needed) and *Ignore SQL in this string*.
- **Completion** suggests tables after `FROM`/`JOIN`, alias-aware columns after `c.`, schema members,
  and procedures after `EXEC`.
- **Syntax highlighting** colors SQL keywords, identifiers, functions, variables, numbers and comments
  inside C# strings, with built-in color presets.
- **Alias colors** give every alias in a query its own color, both where it is declared and everywhere
  it is used, so `c.Name` and `FROM Customers c` match at a glance.
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

**Interpolation holes and non-constant concatenation operands** become placeholders. A hole in a value
position becomes a parameter, and one in a name position (`FROM {table}`) is never validated. Syntax
errors next to a placeholder are suppressed. Constant operands (`const string Columns = "Id, Name";`)
are resolved and validated, and so are locals that are assigned a string once and never changed:

```csharp
var where = "WHERE Region = @region";
var sql = $"SELECT Id FROM Customers {where}";   // validated as one statement
```

**Format strings** work the same way. In `string.Format`, `AppendFormat` and EF Core's `FromSqlRaw`,
`ExecuteSqlRaw` and `SqlQueryRaw`, `{0}`-style items become placeholders (`{{`/`}}` are unescaped), and
`string.Format` arguments that are constants are substituted. Any other literal containing `{0}` items is
read as a format string too, so `const string Sql = "... WHERE Id = {0}";` is not a syntax error.

**SQL built up over several statements** is validated as one statement when it is assembled in a local
`string` or `StringBuilder`:

```csharp
var sql = "SELECT Id, Name FROM Customers WHERE 1 = 1";
if (region != null)
    sql += " AND Region = @region";                     // optional clauses are included
sql += string.Format(" ORDER BY {0}", sortColumn);

var sb = new StringBuilder("SELECT o.Id FROM Orders o");
sb.AppendLine(" WHERE o.Total > @min")
  .AppendFormat(" AND o.CustomerId = {0}", id);
```

`+=`, `x = x + ...`, `Append`, `AppendLine` and `AppendFormat` are followed through the rest of the block.
The body of an `if` is included; for `if`/`else` and `switch` only the first branch is followed, and a
loop that appends becomes a single placeholder. A call that consumes the variable ends the statement,
and an unknown change (say, `sql = sql.Replace(...)`) ends it with a placeholder. Errors land on the piece
that contains them, and `// sqllense:ignore` on any piece silences the whole statement.

## Getting started (Visual Studio)

1. Install SqlLense from the Visual Studio Marketplace (**Extensions > Manage Extensions**), or download
   the `.vsix` from a GitHub release, the `vsix` artifact from CI, or build it from this repo (see below).
2. Open your solution and run **Tools > SqlLense: Configure Connection...**. Enter a connection
   string, for example `Server=localhost;Database=Shop;Integrated Security=true;TrustServerCertificate=true`,
   and optionally a password (stored separately and masked).
3. Run **Tools > SqlLense: Refresh Database Schema**. It is also on the solution's right-click menu.
   You can also enable *Refresh schema when the solution opens*.

Syntax checking works immediately without a database; schema checks start once a snapshot exists.

### Colors and themes

Each SQL token class is listed under **Tools > Options > Environment > Fonts and Colors** as
*SqlLense - SQL Keyword*, *Identifier* and so on.

**Alias colors** are on by default. Every table alias, column alias and CTE name in a query gets its
own color, numbered in the order the aliases are declared. Colors are generated automatically and never
repeat within a query, and they adapt to the editor background so they stay readable in light, dark and
blue themes. **Tools > Options > SqlLense > General** has a switch for each part:

| Setting | Colors |
|---|---|
| Color aliases | Turns alias coloring on or off. Needs *SQL syntax highlighting*. |
| Alias declarations | `c` in `FROM Customers c` |
| Alias references | `c` in `c.Name`, `Total` in `ORDER BY Total`, `Recent` in `FROM Recent` |
| Aliased table names | `Customers` in `FROM Customers c`, shown in `c`'s color |
| Column aliases and CTE names | `SUM(x) AS Total` and `WITH Recent AS (...)`. When off, only table aliases are colored. |

Aliases are resolved with T-SQL scoping, so an alias redeclared in a subquery gets a new color, and a
correlated subquery's `c.Id` matches the outer `c`. While a query is still being typed and does not parse
yet, a token scan keeps table aliases, CTE names and `AS` column aliases colored.

**Color presets** (*Apply color preset* on the same page) write a whole scheme into the Fonts and Colors
entries above: *SqlLense default*, *SSMS-like*, *Azure Data Studio-like*, *DataGrip / JetBrains*,
*Dracula* and *Solarized*. Each preset has a light and a dark variant, and the one matching the current
theme is used. Presets set foreground colors and bold only; the editor background is not changed. You
can still adjust single colors in Fonts and Colors afterwards. Apply the preset again after switching
between a light and a dark theme.

### Where settings and schemas live

Nothing is written to your repository.

| What | Where |
|---|---|
| Connection settings | VS per-user settings store, keyed by solution folder. The connection string and password are encrypted with DPAPI for your Windows account. |
| Editor and color settings | VS per-user settings store (`SqlLense\Editor`). Preset colors live in Fonts and Colors. |
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

## Performance

The design goal is that you never feel it while typing.

- **Cheap rejection:** almost every string is dismissed after an allocation-free check of its leading
  keyword (~50 ns), before any SQL work is done.
- **Parse cache:** parsing is memoized per SQL text, and validation per SQL text and snapshot. Editing
  one method never re-parses the SQL in another.
- **Schema cache:** the snapshot is loaded once, indexed with hash lookups, and only re-read when the
  file's timestamp changes (checked at most every 2 seconds). No network access happens during analysis.
- **Mostly syntactic:** the semantic model is only touched to resolve `const` operands of concatenations
  that already look like SQL. Following a built-up string is a syntactic walk of one block, and only
  happens for a string that starts with a keyword and whose variable a quick text scan shows is
  appended to later. Source maps from SQL offsets back to C# positions are built lazily, only
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
| Full analyzer pass, 20k-line file with 2,000 SQL strings | ~360 ms, vs ~350 ms for a no-op analyzer on the same file (the Roslyn baseline) |
| Recompute highlighting regions for that file | ~22 ms (background, debounced) |
| Full analyzer pass, 40k-line file with 4,000 SQL statements built with `+=`, `StringBuilder` and `string.Format` | ~2.4 s, vs ~2.3 s for the no-op analyzer |
| Recompute highlighting regions for that file | ~220 ms |

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

## Releasing

Pushing a `vMAJOR.MINOR.PATCH` tag runs `.github/workflows/release.yml`: it runs the tests, builds the
VSIX stamped with the tag's version, publishes it to the Visual Studio Marketplace with
`publishManifest.json` (this README is the listing page), and attaches the `.vsix` to a GitHub release.

```sh
git tag v0.2.0 && git push origin v0.2.0
```

The Marketplace upload needs an Azure DevOps personal access token with the **Marketplace (Manage)**
scope and *All accessible organizations*, stored as the `VS_MARKETPLACE_TOKEN` secret on a GitHub
environment named `marketplace` (add required reviewers there to approve each release). Without that
secret the upload is skipped; upload the `.vsix` from the GitHub release at
https://marketplace.visualstudio.com/manage instead. The publisher ID in `publishManifest.json` must
match your Marketplace publisher, and the Marketplace rejects a version it has already seen, so each
release needs a new tag.

## Limitations and roadmap

- T-SQL (SQL Server) only. The dialect-specific code is isolated in `SqlLense.Core`.
- One database per solution in the UI. The snapshot format, locator and `SqlLenseDatabase` property
  already support several; the options page will grow a per-project mapping.
- SQL built across statements is followed within one block only: not through fields, helper methods,
  `string.Join`, or every branch of an `if`/`else` (only the first branch is checked).
- Razor (`.cshtml`/`.razor`) buffers are not highlighted.
- Highlighting uses fixed mid-tone defaults that work on light and dark themes. Customize them under
  *Fonts and Colors > "SqlLense - SQL …"*.
