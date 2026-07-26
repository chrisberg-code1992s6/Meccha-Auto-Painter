param(
    [Parameter(Mandatory = $true)]
    [int]$ProcessId,
    [Parameter(Mandatory = $true)]
    [string]$OutDir,
    [Parameter(Mandatory = $true)]
    [string]$RunnerPath,
    [Parameter(Mandatory = $true)]
    [string[]]$RunnerArguments,
    [ValidateRange(100, 2000)]
    [int]$SampleMs = 250,
    [ValidateRange(1, 30)]
    [int]$IdleSeconds = 2,
    [ValidateRange(0, 30)]
    [int]$PostSeconds = 3
)

$ErrorActionPreference = "Stop"

function Resolve-SafeOutputDirectory([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/")
    $root = [System.IO.Path]::GetPathRoot($full).TrimEnd("\", "/")
    if ([string]::IsNullOrWhiteSpace($full) -or $full -eq $root) {
        throw "Refusing to use a filesystem root as GPU sample output: $Path"
    }
    return $full
}

function Initialize-ProcessGpuCounterPaths([int]$TargetProcessId) {
    $counter = Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop
    $script:GpuCounterPaths = @(
        $counter.CounterSamples |
            Where-Object { $_.Path -match ("pid_{0}_" -f $TargetProcessId) } |
            ForEach-Object { [string]$_.Path }
    )
    if ($script:GpuCounterPaths.Count -eq 0) {
        throw "No GPU Engine counters were exposed for process $TargetProcessId."
    }
}

function Get-ProcessGpuSamples([int]$TargetProcessId, [string]$Phase, [double]$ElapsedMs) {
    if ($null -eq $script:GpuCounterPaths -or $script:GpuCounterPaths.Count -eq 0) {
        Initialize-ProcessGpuCounterPaths $TargetProcessId
    }
    $counter = Get-Counter -Counter $script:GpuCounterPaths -ErrorAction Stop
    $rows = @()
    foreach ($sample in $counter.CounterSamples) {
        $path = [string]$sample.Path
        if ($path -notmatch ("pid_{0}_" -f $TargetProcessId)) {
            continue
        }
        $engineType = "unknown"
        if ($path -match 'engtype_([^\)]+)\)\\utilization percentage') {
            $engineType = $Matches[1]
        }
        $rows += [pscustomobject]@{
            phase = $Phase
            elapsed_ms = [math]::Round($ElapsedMs, 3)
            path = $path
            engine_type = $engineType
            utilization = [math]::Round([double]$sample.CookedValue, 3)
        }
    }
    return $rows
}

function Add-GpuSample(
    [System.Collections.Generic.List[object]]$Samples,
    [System.Diagnostics.Stopwatch]$Clock,
    [int]$TargetProcessId,
    [string]$Phase
) {
    foreach ($row in (Get-ProcessGpuSamples $TargetProcessId $Phase $Clock.Elapsed.TotalMilliseconds)) {
        $Samples.Add($row)
    }
}

function Get-PhaseSummary([object[]]$Samples, [string]$Phase) {
    $selected = @($Samples | Where-Object { $_.phase -eq $Phase })
    $values = @($selected | ForEach-Object { [double]$_.utilization })
    if ($values.Count -eq 0) {
        return [pscustomobject]@{ phase = $Phase; samples = 0; mean = -1.0; p95 = -1.0; maximum = -1.0; engines = @{} }
    }
    $sorted = @($values | Sort-Object)
    $p95Index = [math]::Min($sorted.Count - 1, [math]::Floor(($sorted.Count - 1) * 0.95))
    $engineSummary = [ordered]@{}
    foreach ($group in ($selected | Group-Object engine_type)) {
        $engineValues = @($group.Group | ForEach-Object { [double]$_.utilization })
        $engineSummary[$group.Name] = [pscustomobject]@{
            samples = $engineValues.Count
            mean = [math]::Round(($engineValues | Measure-Object -Average).Average, 3)
            maximum = [math]::Round(($engineValues | Measure-Object -Maximum).Maximum, 3)
        }
    }
    return [pscustomobject]@{
        phase = $Phase
        samples = $values.Count
        mean = [math]::Round(($values | Measure-Object -Average).Average, 3)
        p95 = [math]::Round($sorted[$p95Index], 3)
        maximum = [math]::Round(($values | Measure-Object -Maximum).Maximum, 3)
        engines = $engineSummary
    }
}

$OutDir = Resolve-SafeOutputDirectory $OutDir
$RunnerPath = [System.IO.Path]::GetFullPath($RunnerPath)
if (-not (Test-Path -LiteralPath $RunnerPath -PathType Leaf)) {
    throw "Runner was not found: $RunnerPath"
}
Get-Process -Id $ProcessId -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Prime the counter provider before the timed idle interval and keep the
# process-specific paths. Querying the wildcard on every sample reinitializes
# the full GPU provider and can take several seconds on some Windows builds.
Initialize-ProcessGpuCounterPaths $ProcessId
Get-ProcessGpuSamples $ProcessId "warmup" 0 | Out-Null

$samples = [System.Collections.Generic.List[object]]::new()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$idleDeadline = $clock.Elapsed + [TimeSpan]::FromSeconds($IdleSeconds)
while ($clock.Elapsed -lt $idleDeadline) {
    Add-GpuSample $samples $clock $ProcessId "idle"
    Start-Sleep -Milliseconds $SampleMs
}

$oldResearchGate = $env:MECCHA_RESEARCH_ARTIFACTS
$env:MECCHA_RESEARCH_ARTIFACTS = "1"
$runnerExitCode = $null
try {
    $runner = Start-Process -FilePath $RunnerPath `
        -ArgumentList $RunnerArguments `
        -PassThru `
        -RedirectStandardOutput (Join-Path $OutDir "runner.stdout.txt") `
        -RedirectStandardError (Join-Path $OutDir "runner.stderr.txt")
    while (-not $runner.HasExited) {
        Add-GpuSample $samples $clock $ProcessId "active"
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
    Add-GpuSample $samples $clock $ProcessId "post"
    Start-Sleep -Milliseconds $SampleMs
}

$samples | Export-Csv -LiteralPath (Join-Path $OutDir "gpu-samples.csv") -NoTypeInformation -Encoding UTF8
$summary = [ordered]@{
    captured_utc = [DateTimeOffset]::UtcNow.ToString("O")
    process_id = $ProcessId
    sample_ms = $SampleMs
    runner_path = $RunnerPath
    runner_arguments = $RunnerArguments
    runner_exit_code = $runnerExitCode
    idle = Get-PhaseSummary $samples.ToArray() "idle"
    active = Get-PhaseSummary $samples.ToArray() "active"
    post = Get-PhaseSummary $samples.ToArray() "post"
    note = "GPU Engine utilization is a counter sample, not a RenderThread or frame-time trace. Values can be split across engines and processes."
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir "gpu-summary.json") -Encoding UTF8
$summary | ConvertTo-Json -Depth 8
