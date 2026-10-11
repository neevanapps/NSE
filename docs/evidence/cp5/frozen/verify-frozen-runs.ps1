# Frozen-binary recorded check (CP5 follow-up). Never builds. Run from D:\Projects\NSE-phase1 in a shell where
# NSE_RECORDED_SOURCE is already set (its value is never printed or written).
$ErrorActionPreference = 'Stop'
Set-Location 'D:\Projects\NSE-phase1'
if ([string]::IsNullOrEmpty($env:NSE_RECORDED_SOURCE)) { throw 'NSE_RECORDED_SOURCE is not set in this shell.' }

$bin = 'tools\TradingRunDiff\bin\Release\net10.0'
$dll = Join-Path $bin 'TradingRunDiff.dll'
$manifestFile = 'artifacts\frozen\manifest-before-runs.txt'
$expected = @{}
foreach ($l in Get-Content $manifestFile) { if ($l -match '^\s+(\S+\.dll)\s+([0-9A-F]{64})$') { $expected[$Matches[1]] = $Matches[2] } }
$commit = ((Get-Content $manifestFile | Where-Object { $_ -like 'source commit*' }) -split ':\s*')[1].Trim()

function Test-Frozen([string]$when) {
    $bad = @()
    foreach ($n in $expected.Keys) {
        $h = (Get-FileHash (Join-Path $bin $n) -Algorithm SHA256).Hash
        if ($h -ne $expected[$n]) { $bad += "$n ($h)" }
    }
    $head = (git rev-parse HEAD).Trim()
    $clean = ((git status --porcelain) | Measure-Object).Count -eq 0
    if ($bad.Count -gt 0 -or $head -ne $commit -or -not $clean) { throw "FROZEN CHECK FAILED $when : changed assemblies [$($bad -join ', ')], head=$head (expected $commit), clean=$clean" }
    Write-Host "frozen check OK $when ($($expected.Count) assemblies identical to the manifest, commit $commit, tree clean)"
}

$env:NSE_RUN_DAY = '2026-09-22'
$env:NSE_RUN_MODE = 'both'
$results = @{}
Test-Frozen 'before mixed'
foreach ($script in 'mixed', 'tiny') {
    $env:NSE_POLL_SCRIPT = $script
    $env:NSE_OUT = "artifacts\frozen\run-$script"
    New-Item -ItemType Directory -Force $env:NSE_OUT | Out-Null
    Write-Host "=== running '$script' (dotnet <dll>, no build) ==="
    & dotnet $dll
    $results[$script] = $LASTEXITCODE
    Test-Frozen "after $script"
}

function Digests([string]$file, [string]$section) {
    $d = @{}; $in = $false
    foreach ($l in Get-Content $file) {
        if ($l -match '^\[(.+)\]') { $in = ($Matches[1] -like "$section*") }
        elseif ($in -and $l -match '^\s+channel (\S+)\s+lines\s+(\d+)\s+sha256 (\S+)') { $d[$Matches[1]] = "$($Matches[2]) $($Matches[3])" }
    }
    $d
}
$mixed = 'artifacts\frozen\run-mixed\trading-run-diff-2026-09-22-both-mixed.txt'
$tiny = 'artifacts\frozen\run-tiny\trading-run-diff-2026-09-22-both-tiny.txt'
$summary = @()
$summary += "exit codes: mixed=$($results['mixed']) tiny=$($results['tiny']) (0 = equivalent)"
foreach ($f in $mixed, $tiny) { $summary += "$f : $(((Select-String -Path $f -Pattern '^(EQUIVALENT|DIVERGENT)').Line))" }
$ok = $true
foreach ($name in 'quotes','bars','readiness','candles','opportunities','outputs','rowQuarantine','audit','summaries') {
    $mA = (Digests $mixed 'historical')[$name]; $mB = (Digests $mixed 'live')[$name]
    $tA = (Digests $tiny 'historical')[$name]; $tB = (Digests $tiny 'live')[$name]
    $same = ($mA -eq $mB) -and ($mA -eq $tA) -and ($mA -eq $tB)
    if (-not $same) { $ok = $false }
    $summary += ("{0,-14} mixed-hist={1}  mixed-live={2}  tiny-hist={3}  tiny-live={4}  all-equal={5}" -f $name, $mA, $mB, $tA, $tB, $same)
}
$summary += "cross-run + within-run channel digests all equal: $ok"
$summary += "build identity in both reports: " + ((Select-String -Path $mixed, $tiny -Pattern 'git commit').Line -join ' | ')
$summary | Tee-Object -FilePath 'artifacts\frozen\frozen-run-summary.txt'
Get-ChildItem artifacts\frozen -Recurse -File | Select-Object FullName, Length
