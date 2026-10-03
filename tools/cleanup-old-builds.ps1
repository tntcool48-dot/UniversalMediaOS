# One-time cleanup of the obsolete build outputs inventoried on October 1, 2026.
# Preview: .\tools\cleanup-old-builds.ps1
# Delete only the validated generated outputs: .\tools\cleanup-old-builds.ps1 -Apply
[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$auditRoot = Join-Path $workspaceRoot '.artifacts\storage-audit-20261001'
$inventory = Get-Content -LiteralPath (Join-Path $auditRoot 'inventory.json') -Raw | ConvertFrom-Json
if ($inventory.workspace -ne $workspaceRoot) { throw 'Inventory belongs to another workspace.' }
$allowedRoots = @((Join-Path $workspaceRoot '.artifacts'), (Join-Path $workspaceRoot '.agents'))

function Select-BuildBranches([string]$Directory, [string[]]$EvidencePaths) {
    $protected = @($EvidencePaths | Where-Object {
        $_.StartsWith($Directory + '\', [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($protected.Count -eq 0) { $Directory; return }
    # Keep evidence at its existing path. Remove only branches without it.
    foreach ($child in Get-ChildItem -LiteralPath $Directory -Directory -Force) {
        Select-BuildBranches -Directory $child.FullName -EvidencePaths $protected
    }
}

$tracked = @(& git -C $workspaceRoot ls-files -- .artifacts .agents)
if ($LASTEXITCODE -ne 0 -or $tracked.Count -gt 0) { throw 'Artifact roots contain tracked files or Git verification failed.' }

$candidates = @($inventory.generated_targets | ForEach-Object {
    if ((Split-Path -Path $_.path -Leaf) -notin @('bin','obj')) { throw 'Unexpected generated directory.' }
    [string]$_.path
})
foreach ($entry in $inventory.additional_build_outputs) {
    if (@($entry.preserve_entries).Count -gt 0) { throw "Output contains source/evidence: $($entry.path)" }
    if (Test-Path -LiteralPath $entry.path) {
        $hasAssembly = Test-Path -LiteralPath (Join-Path $entry.path 'UniversalMediaOS.WPF.dll') -PathType Leaf
        $hasManifest = (Test-Path -LiteralPath (Join-Path $entry.path 'UniversalMediaOS.WPF.deps.json') -PathType Leaf) -or
            (Test-Path -LiteralPath (Join-Path $entry.path 'UniversalMediaOS.Tests.E2E.deps.json') -PathType Leaf)
        if (-not ($hasAssembly -and $hasManifest)) { throw "Missing build markers: $($entry.path)" }
    }
    $candidates += [string]$entry.path
}

$resolvedTargets = @()
foreach ($candidate in $candidates) {
    $full = [System.IO.Path]::GetFullPath($candidate)
    $allowed = $false
    foreach ($allowedRoot in $allowedRoots) {
        if ($full.StartsWith($allowedRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) { $allowed = $true }
    }
    if (-not $allowed) { throw "Target outside generated-output roots: $full" }
    if (-not (Test-Path -LiteralPath $full)) { continue }
    $resolved = (Resolve-Path -LiteralPath $full).ProviderPath
    if ($resolved -ne $full) { throw "Unexpected resolved target: $resolved" }
    $cursor = Get-Item -LiteralPath $resolved
    while ($cursor.FullName -ne $workspaceRoot) {
        if (($cursor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Target ancestry contains a link.' }
        $cursor = $cursor.Parent
        if ($null -eq $cursor) { throw 'Target ancestry left the workspace.' }
    }
    $links = @(Get-ChildItem -LiteralPath $resolved -Directory -Recurse -Force -Attributes ReparsePoint)
    if ($links.Count -gt 0) { throw "Target contains links: $resolved" }
    $leaf = Split-Path -Path $resolved -Leaf
    $sensitive = @(Get-ChildItem -LiteralPath $resolved -Recurse -File -Force |
        Where-Object {
            # LibVLC includes packaged web-control icons, not QA screenshots.
            if ($_.Extension -eq '.png' -and $_.FullName -match '\\libvlc\\[^\\]+\\lua\\http\\') { return $false }
            # These compiler-generated source files are recreated with obj.
            if ($leaf -eq 'obj' -and $_.Extension -eq '.cs' -and
                $_.Name -match '(\.g(\.i)?\.cs$|AssemblyInfo\.cs$|AssemblyAttributes\.cs$)') { return $false }
            $_.Extension -in @('.cs','.csproj','.sln','.xaml','.db','.sqlite','.trx','.log','.mp4','.mkv','.png')
        })
    if ($sensitive.Count -gt 0) {
        $resolvedTargets += @(Select-BuildBranches -Directory $resolved -EvidencePaths @($sensitive.FullName))
        continue
    }
    $resolvedTargets += $resolved
}

$deleteTargets = @()
foreach ($target in @($resolvedTargets | Sort-Object Length)) {
    $covered = $false
    foreach ($parent in $deleteTargets) {
        if ($target.StartsWith($parent + '\', [System.StringComparison]::OrdinalIgnoreCase)) { $covered = $true; break }
    }
    if (-not $covered) { $deleteTargets += $target }
}
foreach ($process in @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath })) {
    foreach ($target in $deleteTargets) {
        if ($process.ExecutablePath.StartsWith($target + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Close the process using an old build first: $($process.Name)"
        }
    }
}

Write-Host "Validated obsolete build folders: $($deleteTargets.Count)"
Write-Host 'Current project builds, source, profiles, media and QA results are preserved.'
if (-not $Apply) {
    $deleteTargets | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $auditRoot 'preview-targets.json') -Encoding UTF8
    Write-Host 'Preview only. Run this same script with -Apply to remove these folders.'
    return
}
$freeBefore = (Get-PSDrive -Name C).Free
$deleted = @()
$failed = @()
foreach ($target in $deleteTargets) {
    try {
        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
        $deleted += $target
    } catch {
        $failed += @{ path = $target; reason = $_.Exception.Message }
    }
}
$freeAfter = (Get-PSDrive -Name C).Free
$result = @{ deleted_targets = $deleted; failed_targets = $failed; free_before_bytes = $freeBefore;
    free_after_bytes = $freeAfter; free_increase_bytes = ($freeAfter - $freeBefore) }
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $auditRoot 'cleanup.json') -Encoding UTF8
Write-Host ("Reclaimed {0:N2} GB; deleted {1} folders; {2} failures." -f
    (($freeAfter - $freeBefore) / 1e9), $deleted.Count, $failed.Count)
if ($failed.Count -gt 0) { Write-Warning 'Some folders were retained. See cleanup.json for details.' }
