# Build and test an explicitly selected, isolated application artifact.
param (
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [string]$ArtifactsPath = (Join-Path $PSScriptRoot ".artifacts\implementation\build"),
    [string]$ExecutablePath = $env:UNIVERSAL_MEDIA_OS_EXE,
    [string]$Filter,
    [switch]$FullSuite
)

$ErrorActionPreference = "Stop"
$hasFilter = -not [string]::IsNullOrWhiteSpace($Filter)
if ($FullSuite -and $hasFilter) {
    throw "Choose either -Filter for focused checks or -FullSuite for a full checkpoint, not both."
}
if (-not $FullSuite -and -not $hasFilter) {
    throw "Select -Filter for the affected checks, or explicitly choose -FullSuite for a full checkpoint."
}
$artifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsPath)
$configurationFolder = $Configuration.ToLowerInvariant()
$testAssembly = Join-Path $artifactsRoot "bin\UniversalMediaOS.Tests.E2E\$configurationFolder\UniversalMediaOS.Tests.E2E.dll"

if (-not $NoBuild) {
    dotnet build (Join-Path $PSScriptRoot "UniversalMediaOS.sln") -c $Configuration --artifacts-path $artifactsRoot --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $artifactsRoot "bin\UniversalMediaOS.WPF\$configurationFolder\UniversalMediaOS.WPF.exe"
}
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
foreach ($artifact in @($ExecutablePath, $testAssembly)) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Required test artifact is missing: $artifact"
    }
}

Write-Host "Testing executable: $ExecutablePath"
Write-Host "Executable SHA256: $((Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash)"
$previousExecutable = $env:UNIVERSAL_MEDIA_OS_EXE
try {
    $env:UNIVERSAL_MEDIA_OS_EXE = $ExecutablePath
    $testArguments = @("test", $testAssembly, "--nologo", "--logger", "console;verbosity=minimal", "--logger", "trx;LogFileName=recovery-e2e.trx", "--ResultsDirectory", (Join-Path $artifactsRoot "results"))
    if ($hasFilter) { $testArguments += @("--filter", $Filter) }
    & dotnet @testArguments
    $testExitCode = $LASTEXITCODE
}
finally {
    $env:UNIVERSAL_MEDIA_OS_EXE = $previousExecutable
}
exit $testExitCode
