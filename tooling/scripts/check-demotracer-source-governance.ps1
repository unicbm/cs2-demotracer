param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$sourceRoot = Join-Path $RepoRoot "server\plugins\DemoTracer"
$entryPointPath = Join-Path $sourceRoot "DemoTracerPlugin.cs"
$defaultFileLimit = 1500
$legacyFileLimits = @{}

$errors = [System.Collections.Generic.List[string]]::new()
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File)
foreach ($file in $sourceFiles) {
    $lineCount = [System.IO.File]::ReadAllLines($file.FullName).Length
    $limit = if ($legacyFileLimits.ContainsKey($file.Name)) {
        $legacyFileLimits[$file.Name]
    } else {
        $defaultFileLimit
    }
    if ($lineCount -gt $limit) {
        $errors.Add("$($file.Name) has $lineCount lines; limit is $limit")
    }
}

$entryPointLines = [System.IO.File]::ReadAllLines($entryPointPath).Length
if ($entryPointLines -gt 600) {
    $errors.Add("DemoTracerPlugin.cs must remain a small composition root; found $entryPointLines lines")
}

$allSource = ($sourceFiles | ForEach-Object {
    [System.IO.File]::ReadAllText($_.FullName)
}) -join "`n"
$basePluginDeclarations = [regex]::Matches(
    $allSource,
    'public\s+sealed\s+partial\s+class\s+DemoTracerPlugin\s*:\s*BasePlugin').Count
if ($basePluginDeclarations -ne 1) {
    $errors.Add("expected exactly one DemoTracerPlugin BasePlugin entry point; found $basePluginDeclarations")
}

$entryPointSource = [System.IO.File]::ReadAllText($entryPointPath)
$entryPointFields = [regex]::Matches(
    $entryPointSource,
    '(?m)^\s{4}private\s+[^\r\n()]+\s+_[A-Za-z0-9_]+\s*(?:=|;)').Count
if ($entryPointFields -gt 50) {
    $errors.Add("DemoTracerPlugin.cs owns $entryPointFields private fields; limit is 50")
}

$econIndexSource = [System.IO.File]::ReadAllText(
    (Join-Path $sourceRoot "DemoTracerEconIndex.cs"))
if ($econIndexSource -match '\bstatic\s+ReplayEquipmentCatalog\b') {
    $errors.Add("replay equipment catalog must remain instance-owned")
}
if ($econIndexSource -match 'Assembly\.Location') {
    $errors.Add("replay equipment catalog must load only from the explicit module directory")
}

$apiProject = [System.IO.File]::ReadAllText(
    (Join-Path $RepoRoot "server\plugins\DemoTracerApi\DemoTracerApi.csproj"))
if ($apiProject -match '<PackageReference' -or $apiProject -match '<ProjectReference') {
    $errors.Add("DemoTracerApi must remain a contract-only assembly without runtime references")
}

if ($errors.Count -gt 0) {
    throw "DemoTracer source governance failed:`n - $($errors -join "`n - ")"
}

Write-Host (
    "DemoTracer source governance passed: files={0} entrypoint_lines={1} entrypoint_fields={2}" -f
        $sourceFiles.Count,
        $entryPointLines,
        $entryPointFields)
