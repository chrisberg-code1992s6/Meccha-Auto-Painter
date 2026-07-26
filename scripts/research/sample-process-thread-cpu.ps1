param(
    [Parameter(Mandatory = $true)]
    [int]$ProcessId,
    [Parameter(Mandatory = $true)]
    [string]$OutDir,
    [Parameter(Mandatory = $true)]
    [string]$RunnerPath,
    [Parameter(Mandatory = $true)]
    [string[]]$RunnerArguments,
    [ValidateRange(50, 1000)]
    [int]$SampleMs = 100,
    [ValidateRange(1, 30)]
    [int]$IdleSeconds = 2,
    [ValidateRange(0, 30)]
    [int]$PostSeconds = 2
)

$ErrorActionPreference = "Stop"

function Resolve-SafeOutputDirectory([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/")
    $root = [System.IO.Path]::GetPathRoot($full).TrimEnd("\", "/")
    if ([string]::IsNullOrWhiteSpace($full) -or $full -eq $root) {
        throw "Refusing to use a filesystem root as CPU sample output: $Path"
    }
    return $full
}

function Get-TrackedThreadId([int]$TargetProcessId) {
    $process = Get-Process -Id $TargetProcessId -ErrorAction Stop
    $thread = $process.Threads |
        Sort-Object { $_.TotalProcessorTime.TotalMilliseconds } -Descending |
        Select-Object -First 1
    if ($null -eq $thread) {
        throw "No thread was available for process $TargetProcessId."
    }
    return [int]$thread.Id
}

function Add-CpuSample(
    [System.Collections.Generic.List[object]]$Samples,
    [System.Diagnostics.Stopwatch]$Clock,
    [int]$TargetProcessId,
    [int]$ThreadId,
    [string]$Phase
) {
    $process = Get-Process -Id $TargetProcessId -ErrorAction Stop
    $thread = $process.Threads | Where-Object { $_.Id -eq $ThreadId } | Select-Object -First 1
    if ($null -eq $thread) {
        throw "Tracked game thread $ThreadId is no longer available in process $TargetProcessId."
    }
    $Samples.Add([pscustomobject]@{
        phase = $Phase
        elapsed_ms = [math]::Round($Clock.Elapsed.TotalMilliseconds, 4)
        process_cpu_ms = [math]::Round($process.TotalProcessorTime.TotalMilliseconds, 4)
        game_thread_cpu_ms = [math]::Round($thread.TotalProcessorTime.TotalMilliseconds, 4)
        working_set = [int64]$process.WorkingSet64
    })
}

function Get-IntervalStats([object[]]$Samples, [string]$Phase) {
    $selected = @($Samples | Where-Object { $_.phase -eq $Phase })
    $percentages = [System.Collections.Generic.List[double]]::new()
    for ($index = 1; $index -lt $selected.Count; $index++) {
        $elapsed = [double]$selected[$index].elapsed_ms - [double]$selected[$index - 1].elapsed_ms
        $cpu = [double]$selected[$index].game_thread_cpu_ms - [double]$selected[$index - 1].game_thread_cpu_ms
        if ($elapsed -gt 0 -and $cpu -ge 0) {
            $percentages.Add(($cpu / $elapsed) * 100.0)
        }
    }
    if ($percentages.Count -eq 0) {
        return [pscustomobject]@{ samples = 0; average = -1.0; p95 = -1.0; maximum = -1.0 }
    }
    $sorted = @($percentages | Sort-Object)
    $p95Index = [math]::Min($sorted.Count - 1, [math]::Floor(($sorted.Count - 1) * 0.95))
    return [pscustomobject]@{
        samples = $percentages.Count
        average = [math]::Round(($percentages | Measure-Object -Average).Average, 3)
        p95 = [math]::Round($sorted[$p95Index], 3)
        maximum = [math]::Round(($percentages | Measure-Object -Maximum).Maximum, 3)
    }
}

$OutDir = Resolve-SafeOutputDirectory $OutDir
$RunnerPath = [System.IO.Path]::GetFullPath($RunnerPath)
if (-not (Test-Path -LiteralPath $RunnerPath -PathType Leaf)) {
    throw "Runner was not found: $RunnerPath"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$trackedThreadId = Get-TrackedThreadId $ProcessId
$samples = [System.Collections.Generic.List[object]]::new()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$idleDeadline = $clock.Elapsed + [TimeSpan]::FromSeconds($IdleSeconds)
while ($clock.Elapsed -lt $idleDeadline) {
    Add-CpuSample $samples $clock $ProcessId $trackedThreadId "idle"
    Start-Sleep -Milliseconds $SampleMs
}

$oldResearchGate = $env:MECCHA_RESEARCH_ARTIFACTS
$env:MECCHA_RESEARCH_ARTIFACTS = "1"
try {
    $runner = Start-Process -FilePath $RunnerPath `
        -ArgumentList $RunnerArguments `
        -PassThru `
        -RedirectStandardOutput (Join-Path $OutDir "runner.stdout.txt") `
        -RedirectStandardError (Join-Path $OutDir "runner.stderr.txt")
    while (-not $runner.HasExited) {
        Add-CpuSample $samples $clock $ProcessId $trackedThreadId "active"
        Start-Sleep -Milliseconds $SampleMs
        $runner.Refresh()
    }
    $runner.WaitForExit()
    $runner.Refresh()
    $runnerExitCode = $runner.ExitCode
}
finally {
    $env:MECCHA_RESEARCH_ARTIFACTS = $oldResearchGate
}

$postDeadline = $clock.Elapsed + [TimeSpan]::FromSeconds($PostSeconds)
while ($clock.Elapsed -lt $postDeadline) {
    Add-CpuSample $samples $clock $ProcessId $trackedThreadId "post"
    Start-Sleep -Milliseconds $SampleMs
}

$samples | Export-Csv -LiteralPath (Join-Path $OutDir "cpu-samples.csv") -NoTypeInformation -Encoding UTF8
$summary = [ordered]@{
    captured_utc = [DateTimeOffset]::UtcNow.ToString("O")
    process_id = $ProcessId
    tracked_thread_id = $trackedThreadId
    selection = "largest cumulative TotalProcessorTime at sampler start"
    sample_ms = $SampleMs
    runner_path = $RunnerPath
    runner_arguments = $RunnerArguments
    runner_exit_code = $runnerExitCode
    idle = Get-IntervalStats $samples "idle"
    active = Get-IntervalStats $samples "active"
    post = Get-IntervalStats $samples "post"
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir "cpu-summary.json") -Encoding UTF8
$summary | ConvertTo-Json -Depth 5
