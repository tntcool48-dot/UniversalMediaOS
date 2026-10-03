# Build and test an explicitly selected, isolated application artifact.
param (
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [string]$ArtifactsPath = (Join-Path $PSScriptRoot ".artifacts\implementation\build"),
    [string]$ExecutablePath = $env:UNIVERSAL_MEDIA_OS_EXE,
    [string]$Filter
)

$ErrorActionPreference = "Stop"
$artifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsPath)
$configurationFolder = $Configuration.ToLowerInvariant()
$testAssembly = Join-Path $artifactsRoot "bin\UniversalMediaOS.Tests.E2E\$configurationFolder\UniversalMediaOS.Tests.E2E.dll"

if (-not $NoBuild) {
    dotnet build (Join-Path $PSScriptRoot "UniversalMediaOS.sln") -c $Configuration --artifacts-path $artifactsRoot
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
    $testArguments = @("test", $testAssembly, "--logger", "trx;LogFileName=recovery-e2e.trx", "--ResultsDirectory", (Join-Path $artifactsRoot "results"))
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { $testArguments += @("--filter", $Filter) }
    & dotnet @testArguments
    $testExitCode = $LASTEXITCODE
}
finally {
    $env:UNIVERSAL_MEDIA_OS_EXE = $previousExecutable
}
exit $testExitCode
