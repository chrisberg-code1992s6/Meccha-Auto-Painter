param(
    [Parameter(Mandatory = $true)][string]$SourceExe,
    [string]$LaunchRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "MecchaCamouflage\launch"),
    [ValidateRange(0, 10000)][int]$DiagnosticStrokeLimit = 0
)

$ErrorActionPreference = "Stop"

function Test-FileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Equals(
        $ExpectedHash,
        [System.StringComparison]::OrdinalIgnoreCase)
}

$sourcePath = (Resolve-Path -LiteralPath $SourceExe -ErrorAction Stop).ProviderPath
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Executable not found: $sourcePath. Run make build first."
}

$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
$exeName = Split-Path -Leaf $sourcePath
$exeBaseName = [System.IO.Path]::GetFileNameWithoutExtension($exeName)
$activeRuntimes = @(Get-Process -Name $exeBaseName -ErrorAction SilentlyContinue)
if ($activeRuntimes.Count -gt 0) {
    $activeProcessIds = $activeRuntimes | ForEach-Object { $_.Id } | Sort-Object
    throw "MecchaCamouflage is already running (pid $($activeProcessIds -join ', ')). Close it normally before running make start; do not force-stop it while paint is active."
}
$stageDirectory = Join-Path $LaunchRoot ("{0}-{1}" -f $exeBaseName, $sourceHash.Substring(0, 16))
$stagedExe = Join-Path $stageDirectory $exeName

New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
if (-not (Test-FileHash -Path $stagedExe -ExpectedHash $sourceHash)) {
    $stagingExe = "$stagedExe.staging-$PID-$([Guid]::NewGuid().ToString('N'))"
    try {
        Copy-Item -LiteralPath $sourcePath -Destination $stagingExe -Force
        if (-not (Test-FileHash -Path $stagingExe -ExpectedHash $sourceHash)) {
            throw "Staged executable hash did not match source: $stagingExe"
        }
        Move-Item -LiteralPath $stagingExe -Destination $stagedExe -ErrorAction Stop
    }
    finally {
        if (Test-Path -LiteralPath $stagingExe) {
            Remove-Item -LiteralPath $stagingExe -Force -ErrorAction SilentlyContinue
        }
    }
}

$startProcessArguments = @{ FilePath = $stagedExe; PassThru = $true }
if ($DiagnosticStrokeLimit -gt 0) {
    $startProcessArguments.ArgumentList = @("--diagnostic-stroke-limit", $DiagnosticStrokeLimit.ToString())
}
$process = Start-Process @startProcessArguments
Write-Host "Staged runtime exe: $stagedExe"
Write-Host "Started runtime pid: $($process.Id)"
