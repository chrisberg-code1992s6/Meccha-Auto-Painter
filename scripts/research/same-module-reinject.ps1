param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][string]$BridgePath,
    [Parameter(Mandatory = $true)][string]$InjectorPath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Iterations = 10
)

$ErrorActionPreference = "Stop"

function Convert-HexToBytes([string]$Hex) {
    if (($Hex.Length % 2) -ne 0) { throw "Hex value has odd length." }
    $bytes = New-Object byte[] ($Hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16)
    }
    return $bytes
}

function Write-U32([byte[]]$Buffer, [int]$Offset, [uint32]$Value) {
    $encoded = [BitConverter]::GetBytes($Value)
    [Array]::Copy($encoded, 0, $Buffer, $Offset, 4)
}

function Quote-ProcessArg([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function New-StartBlock([int]$TargetPid, [long]$CreationTime, [string]$TargetExe, [string]$Hash) {
    $instanceId = [Guid]::NewGuid().ToString("N")
    $tokenBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($tokenBytes)
    $rng.Dispose()
    $block = New-Object byte[] 128
    Write-U32 $block 0 0x3153434D
    Write-U32 $block 4 128
    Write-U32 $block 8 1
    Write-U32 $block 12 ([uint32]$TargetPid)
    [Array]::Copy((Convert-HexToBytes $instanceId), 0, $block, 16, 16)
    [Array]::Copy($tokenBytes, 0, $block, 32, 32)
    [Array]::Copy((Convert-HexToBytes $Hash), 0, $block, 64, 32)
    Write-U32 $block 96 0
    Write-U32 $block 100 0
    Write-U32 $block 104 0
    Write-U32 $block 108 1
    Write-U32 $block 112 0
    Write-U32 $block 116 0
    return [pscustomobject]@{
        Block = $block
        InstanceId = $instanceId
        Token = (($tokenBytes | ForEach-Object { $_.ToString("x2") }) -join "")
        CreationTime = $CreationTime
        TargetExe = $TargetExe
    }
}

function Invoke-Injector([object]$Start) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $script:InjectorPath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Arguments = @(
        (Quote-ProcessArg "--direct"),
        (Quote-ProcessArg $Start.BlockPid.ToString()),
        (Quote-ProcessArg $Start.CreationTime.ToString()),
        (Quote-ProcessArg $Start.TargetExe),
        (Quote-ProcessArg $script:BridgePath)
    ) -join " "
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Could not start the injector." }
    $process.StandardInput.BaseStream.Write($Start.Block, 0, $Start.Block.Length)
    $process.StandardInput.BaseStream.Flush()
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "Injector failed: exit=$($process.ExitCode) stdout=$stdout stderr=$stderr"
    }
    $line = ($stdout -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Last 1)
    if (-not $line) { throw "Injector produced no result: $stderr" }
    return ($line | ConvertFrom-Json)
}

function Invoke-BridgeCommand([int]$Port, [string]$InstanceId, [string]$Token, [string]$Command) {
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $client = [System.Net.Sockets.TcpClient]::new()
    $client.Connect("127.0.0.1", $Port)
    $stream = $client.GetStream()
    $reader = [System.IO.StreamReader]::new($stream, $encoding, $false, 4096, $true)
    $writer = [System.IO.StreamWriter]::new($stream, $encoding, 4096, $true)
    $writer.AutoFlush = $true
    $hello = @{ type = "hello"; bootstrap_protocol = 1; instance_id = $InstanceId; token = $Token } |
        ConvertTo-Json -Compress
    $writer.WriteLine($hello)
    $helloReply = $reader.ReadLine()
    if (-not $helloReply) { throw "Bridge returned no hello reply." }
    $writer.WriteLine($Command)
    $reply = $reader.ReadToEnd()
    $writer.Dispose()
    $reader.Dispose()
    $stream.Dispose()
    $client.Dispose()
    return [pscustomobject]@{
        Hello = ($helloReply | ConvertFrom-Json)
        Reply = ($reply | ConvertFrom-Json)
    }
}

function Wait-EventWatchStopped([string]$Path) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            try {
                $snapshot = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
                if ($snapshot.stage -eq "event_watch_stopped") { return $snapshot }
            }
            catch {
            }
        }
        Start-Sleep -Milliseconds 50
    }
    throw "Event-watch did not reach event_watch_stopped: $Path"
}

if ($Iterations -lt 1 -or $Iterations -gt 20) { throw "Iterations must be between 1 and 20." }
if (-not (Test-Path -LiteralPath $BridgePath -PathType Leaf)) { throw "BridgePath not found." }
if (-not (Test-Path -LiteralPath $InjectorPath -PathType Leaf)) { throw "InjectorPath not found." }
$target = Get-Process -Id $TargetPid
$targetExe = $target.Path
$creationTime = $target.StartTime.ToUniversalTime().ToFileTimeUtc()
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $BridgePath).Hash.ToLowerInvariant()
$utf8 = [System.Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $eventWatchPath = Join-Path $OutDir ("eventwatch-{0:00}.json" -f $iteration)
    [IO.File]::WriteAllText($BridgePath + ".eventwatch.path", $eventWatchPath + [Environment]::NewLine, $utf8)
    $start = New-StartBlock $TargetPid $creationTime $targetExe $hash
    $start | Add-Member -NotePropertyName BlockPid -NotePropertyValue $TargetPid
    $injector = Invoke-Injector $start
    if (-not $injector.success -or $injector.state -ne "listening") {
        throw "Iteration $iteration did not start: $($injector | ConvertTo-Json -Compress)"
    }
    $commands = Invoke-BridgeCommand ([int]$injector.port) $start.InstanceId $start.Token '{"type":"ping"}'
    if (-not $commands.Hello.success -or -not $commands.Reply.success) {
        throw "Iteration $iteration ping failed: $($commands | ConvertTo-Json -Compress)"
    }
    $shutdown = Invoke-BridgeCommand ([int]$injector.port) $start.InstanceId $start.Token '{"type":"shutdown"}'
    if (-not $shutdown.Hello.success -or -not $shutdown.Reply.success) {
        throw "Iteration $iteration shutdown failed: $($shutdown | ConvertTo-Json -Compress)"
    }
    $eventWatch = Wait-EventWatchStopped $eventWatchPath
    $result = [pscustomobject]@{
        iteration = $iteration
        instance_id = $start.InstanceId
        injector_state = $injector.state
        port = $injector.port
        ping_stage = $commands.Reply.stage
        shutdown_stage = $shutdown.Reply.stage
        eventwatch_stage = $eventWatch.stage
        eventwatch_generation = $eventWatch.generation
        shutdown_metadata = $shutdown.Reply.metadata
    }
    $results.Add($result)
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir ("iteration-{0:00}.json" -f $iteration)) -Encoding utf8
}

[pscustomobject]@{
    target_pid = $TargetPid
    bridge_path = $BridgePath
    bridge_hash = $hash
    iterations = $results.Count
    success = ($results.Count -eq $Iterations)
    results = $results
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir "summary.json") -Encoding utf8
Write-Output (Get-Content -Raw -LiteralPath (Join-Path $OutDir "summary.json"))
