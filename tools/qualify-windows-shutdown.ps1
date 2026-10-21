<#
.SYNOPSIS
Qualifies resume persistence through a real Windows sign-out or restart, with an isolated profile on the second monitor.

.DESCRIPTION
Prepare  Creates the run folder: an isolated profile and a generated two-hour film with library metadata.
Start    Launches the current build with that profile, opens the film in the Player and leaves it playing.
         Then sign out or restart Windows while it plays; do not pause or seek.
Check    After signing back in: reports what the ended session saved (log, journal, database), then relaunches the
         app, reopens the film, confirms it restores that position and closes it normally.
Stop     Abandons a started run without ending the Windows session: closes the app normally and stops the heartbeat.

Nothing here ends the Windows session. Only the run folder is written; no real profile, library or download is used.
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('Prepare', 'Start', 'Check', 'Stop')][string]$Mode,
    [string]$Run
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 has no $PSScriptRoot while it evaluates parameter defaults.
if (-not $Run) { $Run = Join-Path $PSScriptRoot '..\.artifacts\implementation\windows-shutdown-run' }
$Run = [IO.Path]::GetFullPath($Run)
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$profileRoot = Join-Path $Run 'profile'
$appData = Join-Path $profileRoot 'Roaming\UniversalMediaOS'
$logPath = Join-Path $appData 'app.log'
$database = Join-Path $profileRoot 'media_os.db'
$film = Join-Path $Run 'media\film\media.mkv'
$receiptPath = Join-Path $Run 'receipt.json'
$heartbeat = Join-Path $Run 'heartbeat.txt'
$work = 'av:fixture:film:windows-shutdown'
$unit = 'feature'
$title = 'Shutdown QA film'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$automation = [System.Windows.Automation.AutomationElement]

function Find-Named($scope, [string]$name, [int]$seconds = 15) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($automation::NameProperty, $name)
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $element = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "'$name' did not appear within $seconds seconds."
}

function Get-MainWindow([int]$processId, [int]$seconds = 30) {
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($automation::ProcessIdProperty, $processId)),
        (New-Object System.Windows.Automation.PropertyCondition($automation::NameProperty, 'Universal Media OS')))
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $window = $automation::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($window) { return $window }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "The app's main window did not appear within $seconds seconds."
}

function Get-Pattern($element, $pattern) { $element.GetCurrentPattern($pattern) }

function Read-Shared([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try { (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $stream.Dispose() }
}

function Wait-LogLine([string]$text, [int]$fromLength, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $log = Read-Shared $logPath
        if ($log.Length -gt $fromLength) {
            $line = ($log.Substring($fromLength) -split "`n") | Where-Object { $_.Contains($text) } | Select-Object -Last 1
            if ($line) { return $line.Trim() }
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "The app log did not show '$text' within $seconds seconds."
}

function Get-Executable {
    foreach ($configuration in 'Debug', 'Release') {
        $candidate = Join-Path $repository "UniversalMediaOS.WPF\bin\$configuration\net9.0-windows10.0.17763.0\UniversalMediaOS.WPF.exe"
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw 'Build UniversalMediaOS.WPF first; no executable was found.'
}

function Start-App {
    $executable = Get-Executable
    $start = New-Object System.Diagnostics.ProcessStartInfo($executable)
    $start.WorkingDirectory = Split-Path $executable
    $start.UseShellExecute = $false
    $start.EnvironmentVariables['UNIVERSAL_MEDIA_OS_DATA_ROOT'] = $profileRoot
    $process = [System.Diagnostics.Process]::Start($start)
    [pscustomobject]@{ Process = $process; Executable = $executable; Window = (Get-MainWindow $process.Id) }
}

function Open-Film($window) {
    (Get-Pattern (Find-Named $window 'Player') ([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $null = Find-Named $window 'Play or pause'
    (Get-Pattern (Find-Named $window 'Playback options') ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    (Get-Pattern (Find-Named $window 'Playback volume') ([System.Windows.Automation.RangeValuePattern]::Pattern)).SetValue(0)
    (Get-Pattern (Find-Named $window 'Media URL or local file path') ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($film)
    (Get-Pattern (Find-Named $window 'Open media URL or path') ([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Close-App($app) {
    # The window's own close request, as its title-bar button makes. Process.CloseMainWindow can pick a tooltip.
    (Get-Pattern $app.Window ([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    if (-not $app.Process.WaitForExit(30000)) { throw "The app did not close within 30 seconds (PID $($app.Process.Id))." }
}

function Stop-Heartbeat($receipt) {
    $beat = Get-Process -Id $receipt.HeartbeatPid -ErrorAction SilentlyContinue
    if ($beat -and $beat.StartTime.ToString('o') -eq $receipt.HeartbeatStarted) { Stop-Process -Id $beat.Id }
}

switch ($Mode) {
    'Prepare' {
        if (Test-Path -LiteralPath $Run) { throw "The run folder already exists: $Run. Choose a new -Run folder." }
        New-Item -ItemType Directory -Force -Path $appData, (Split-Path $film) | Out-Null
        @{
            AutoManageServices = 'false'; EnableDebugLogging = 'true'; StartupMonitor = 'Secondary'
            DatabasePath = $database; DownloadDirectory = (Join-Path $profileRoot 'Downloads')
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $appData 'config.json') -Encoding UTF8
        # Two hours, so a delayed sign-out cannot reach the end and clear the position as completed.
        & ffmpeg -nostdin -hide_banner -v error -f lavfi -i 'testsrc=size=160x90:rate=5' -t 7200 -c:v mpeg4 -y $film
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed with exit code $LASTEXITCODE." }
        @{
            SchemaVersion = 1; WorkKey = $work; UnitKey = $unit; Title = $title; MediaFile = 'media.mkv'; CaptionFiles = @()
            Identity = @{ PrimaryId = @{ Provider = 'fixture'; Namespace = 'film'; Value = 'windows-shutdown' }
                Kind = 'Movie'; ContentForm = 'Feature'; Title = $title; Year = 2026 }
            Unit = @{}; SourceProvider = 'generated-fixture'; AudioNotice = 'Generated pattern; no spoken audio.'
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path (Split-Path $film) 'library-item.json') -Encoding UTF8
        Write-Host "Prepared $Run"
    }
    'Start' {
        if (-not (Test-Path -LiteralPath $film)) { throw 'Run -Mode Prepare first.' }
        if (Test-Path -LiteralPath $receiptPath) { throw "This run was already started: $receiptPath. Prepare a new -Run folder." }
        $logLength = (Read-Shared $logPath).Length
        $launched = Get-Date
        $app = Start-App
        Open-Film $app.Window
        $playing = Wait-LogLine "LibVLC playing event fired for: '$title'" $logLength
        # The last heartbeat marks when Windows ended this session, independent of the app's own log flush.
        $beat = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-NonInteractive', '-Command',
            "while (`$true) { [IO.File]::WriteAllText('$heartbeat', [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')); Start-Sleep -Milliseconds 250 }"
        $head = (& git -C $repository rev-parse HEAD).Trim()
        $dirty = [bool](& git -C $repository status --porcelain --untracked-files=no)
        [ordered]@{
            Launched = $launched.ToString('yyyy-MM-dd HH:mm:ss'); Pid = $app.Process.Id
            ProcessStarted = $app.Process.StartTime.ToString('o'); Executable = $app.Executable
            ExecutableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $app.Executable).Hash
            SourceHead = $head; TrackedChangesUncommitted = $dirty; Playing = $playing
            HeartbeatPid = $beat.Id; HeartbeatStarted = $beat.StartTime.ToString('o')
            Profile = $profileRoot; Log = $logPath; Database = $database; Work = $work; Unit = $unit; Title = $title
        } | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding UTF8
        Write-Host "The film is playing on the second monitor (PID $($app.Process.Id))."
        Write-Host 'Leave it playing (no pause or seek), then sign out or restart Windows.'
        Write-Host "After signing back in, run: powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Mode Check -Run `"$Run`""
    }
    'Stop' {
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        $process = Get-Process -Id $receipt.Pid -ErrorAction SilentlyContinue
        if ($process -and $process.StartTime.ToString('o') -eq $receipt.ProcessStarted) {
            Close-App ([pscustomobject]@{ Process = $process; Window = (Get-MainWindow $process.Id) })
        }
        Stop-Heartbeat $receipt
        Write-Host 'Stopped this run without ending the Windows session.'
    }
    'Check' {
        $resultPath = Join-Path $Run 'check-result.json'
        if (Test-Path -LiteralPath $resultPath) {
            # The relaunch below moves the saved position on, so a second analysis would not describe the ended session.
            Write-Host "This run was already checked; its result is $resultPath"
            Get-Content -LiteralPath $resultPath -Raw
            exit 0
        }
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        $process = Get-Process -Id $receipt.Pid -ErrorAction SilentlyContinue
        if ($process -and $process.StartTime.ToString('o') -eq $receipt.ProcessStarted) {
            throw 'The started app is still running: the Windows session has not ended. Sign out or restart first, or use -Mode Stop.'
        }
        Stop-Heartbeat $receipt
        $python = (Get-Command python -ErrorAction SilentlyContinue).Source
        if (-not $python) { $python = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe' }
        $analysisText = & $python -I (Join-Path $PSScriptRoot 'qualify-windows-shutdown.py') $Run
        if ($LASTEXITCODE -ne 0) { throw "The analysis failed with exit code $LASTEXITCODE." }
        $analysis = $analysisText | Out-String | ConvertFrom-Json

        # Reopen the film: the app must restore what the ended session saved (replaying the journal first).
        $logLength = (Read-Shared $logPath).Length
        $app = Start-App
        Open-Film $app.Window
        $loaded = Wait-LogLine "[Resume] Loaded saved position" $logLength
        $restoredLine = Wait-LogLine "[Resume] Restored '$title' to" $logLength
        $restored = [double]([regex]::Match($restoredLine, "to (\d+(\.\d+)?)s\.").Groups[1].Value)
        Start-Sleep -Seconds 2
        Close-App $app
        $restoreMatches = $null -ne $analysis.EffectivePosition -and [math]::Abs($restored - [double]$analysis.EffectivePosition) -le 0.1
        $result = [ordered]@{
            Analysis = $analysis; RelaunchLoaded = $loaded; RelaunchRestored = $restored; RestoreMatchesSaved = $restoreMatches
            RelaunchExecutableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $app.Executable).Hash
            Passed = [bool]$analysis.Passed -and $restoreMatches
        }
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding UTF8
        $result | ConvertTo-Json -Depth 6
        if (-not $result.Passed) { exit 1 }
    }
}
