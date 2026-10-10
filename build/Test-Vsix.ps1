# Checks that a built .vsix contains every assembly the extension needs at run time.
# Used by CI and the release workflow. Pass the folder to search, or a .vsix path.
param(
    [Parameter(Mandatory = $true)][string]$Path
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$vsix = Get-ChildItem $Path -Recurse -Filter *.vsix | Select-Object -First 1
if (-not $vsix) { throw "No .vsix was produced" }
$zip = [IO.Compression.ZipFile]::OpenRead($vsix.FullName)
$names = $zip.Entries | ForEach-Object { $_.FullName }
$reader = New-Object IO.StreamReader($zip.GetEntry('extension.vsixmanifest').Open())
[xml]$manifest = $reader.ReadToEnd()
$reader.Dispose()
$zip.Dispose()
$names
$required = 'extension.vsixmanifest', 'SqlLense.Vsix.dll', 'SqlLense.Vsix.pkgdef', 'SqlLense.Core.dll',
  'SqlLense.Analyzers.dll', 'SqlLense.CodeFixes.dll', 'SqlLense.SchemaReader.dll',
  'Microsoft.SqlServer.TransactSql.ScriptDom.dll'
$missing = $required | Where-Object { $names -notcontains $_ }
if ($missing) { throw "Missing from VSIX: $($missing -join ', ')" }
# Roslyn only resolves analyzer dependencies that are registered as analyzer assets themselves.
$analyzers = $manifest.PackageManifest.Assets.Asset |
  Where-Object { $_.Type -eq 'Microsoft.VisualStudio.Analyzer' } | ForEach-Object { $_.Path }
$analyzers
$requiredAnalyzers = 'SqlLense.Analyzers.dll', 'SqlLense.Core.dll', 'Microsoft.SqlServer.TransactSql.ScriptDom.dll'
$missingAnalyzers = $requiredAnalyzers | Where-Object { $analyzers -notcontains $_ }
if ($missingAnalyzers) { throw "Not registered as analyzer assets: $($missingAnalyzers -join ', ')" }

"Version: $($manifest.PackageManifest.Metadata.Identity.Version)"
