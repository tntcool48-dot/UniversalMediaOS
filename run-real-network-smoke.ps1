param(
    [string]$Query = "Frieren",
    [string]$Episode = "1",
    [ValidateSet("sub", "dub")]
    [string]$Audio = "sub",
    [ValidateRange(1, 12)]
    [int]$MaxSites = 3,
    [ValidateRange(35, 180)]
    [int]$BudgetSeconds = 75
)

$ErrorActionPreference = "Stop"
$scraper = Join-Path $PSScriptRoot "UniversalMediaOS.Core\scraper.py"
$python = (Get-Command python -ErrorAction Stop).Source

if (-not (Test-Path -LiteralPath $scraper)) {
    throw "Scraper not found: $scraper"
}

function Invoke-ScraperJson {
    param([string[]]$Arguments)

    $output = & $python $scraper @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "scraper.py exited with code $LASTEXITCODE"
    }

    try {
        return $output | ConvertFrom-Json
    }
    catch {
        throw "scraper.py returned invalid JSON: $output"
    }
}

$searchWatch = [Diagnostics.Stopwatch]::StartNew()
$search = Invoke-ScraperJson -Arguments @("search", $Query)
$searchWatch.Stop()
if (@($search).Count -eq 0) {
    throw "Real-network search returned no results for '$Query'."
}

$resolveWatch = [Diagnostics.Stopwatch]::StartNew()
$resolved = Invoke-ScraperJson -Arguments @(
    "resolve",
    $Query,
    $Episode,
    $MaxSites.ToString(),
    $Audio,
    $BudgetSeconds.ToString())
$resolveWatch.Stop()

if ($resolved.error) {
    throw "Real-network resolution failed: $($resolved.error)"
}

$resolvedUri = $null
if (-not [Uri]::TryCreate([string]$resolved.url, [UriKind]::Absolute, [ref]$resolvedUri) -or
    $resolvedUri.Scheme -notin @("http", "https")) {
    throw "Resolver returned an invalid playback URL."
}

[pscustomobject]@{
    Query = $Query
    SearchResults = @($search).Count
    SearchSeconds = [Math]::Round($searchWatch.Elapsed.TotalSeconds, 2)
    ResolveSeconds = [Math]::Round($resolveWatch.Elapsed.TotalSeconds, 2)
    Site = $resolved.site
    RequiresWebView = [bool]$resolved.requires_webview
    UrlHost = $resolvedUri.Host
} | Format-List
