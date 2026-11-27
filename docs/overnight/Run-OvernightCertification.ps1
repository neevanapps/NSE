<#
.SYNOPSIS
  Owner-run, fail-fast: 09:45 certification, Top-5 parity in three modes, then benchmarks A/B/C twice. One run at a time.
.DESCRIPTION
  Reads niftysignal_vm_copy as research_ro (password from pgpass.conf only). Writes only to the scratch cluster on 127.0.0.1:54340 and to D:\perf-scratch.
  Re-running resumes: a step with a .done marker for the same source tree is skipped. Nothing is deleted; earlier logs are renamed.
  -PlanOnly checks the source identity and prints the steps; it opens no database and starts nothing.
#>
param([switch]$PlanOnly, [int]$Repeats = 2, [string[]]$Only)      # -Only B-full,C-full runs just these benchmarks, in this order

$ErrorActionPreference = 'Stop'
$Repo = 'D:\Projects\NSE-release-20261124'
$Base = 'D:\perf-scratch\nse-78d9b8b'
$Out = 'D:\perf-scratch\out'
$State = "$Out\overnight"
$Pg = 'C:\Program Files\PostgreSQL\18\bin'
$Data = 'D:\perf-scratch\pg'
$Tree = 'aa906ab302ce7e770d03c1c4002aa40e5a15808e'
$BaseSha = '78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe'
$Day = '2026-11-23'
$Source = 'Host=localhost;Port=5432;Database=niftysignal_vm_copy;Username=research_ro'

function Fail($message) { throw "STOPPED: $message" }

function Get-SourceTree {
    $env:GIT_INDEX_FILE = 'D:\perf-scratch\release.index'
    try {
        if (Test-Path $env:GIT_INDEX_FILE) { Remove-Item $env:GIT_INDEX_FILE }
        $ErrorActionPreference = 'Continue'      # git writes line-ending warnings to stderr
        git -C $Repo add -A 2>$null; if ($LASTEXITCODE -ne 0) { Fail 'git add (temporary index) failed' }
        git -C $Repo rm -r -q --cached docs 2>$null | Out-Null; if ($LASTEXITCODE -ne 0) { Fail 'git rm (temporary index) failed' }
        $hash = git -C $Repo write-tree; if ($LASTEXITCODE -ne 0) { Fail 'git write-tree failed' }
        return "$hash".Trim()
    }
    finally { Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue }
}

# Runs one native command, tees it to a log, records the duration, and stops on a non-zero exit unless -Tolerate.
function Invoke-Step([string]$Name, [scriptblock]$Command, [switch]$Tolerate) {
    $done = "$State\$Name.done"
    if ((Test-Path $done) -and ((Get-Content $done -Raw) -match $Tree)) { Write-Host "SKIP $Name (already completed)"; return }
    $log = "$State\$Name.log"
    if (Test-Path $log) { Rename-Item $log ("$Name.{0:yyyyMMdd-HHmmss}.previous.log" -f (Get-Date)) }
    Write-Host ("=== {0}  start {1:HH:mm:ss}" -f $Name, (Get-Date))
    $start = Get-Date
    $ErrorActionPreference = 'Continue'
    & $Command 2>&1 | Tee-Object -FilePath $log | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $seconds = [int]((Get-Date) - $start).TotalSeconds
    Add-Content "$State\durations.csv" ('{0},{1:s},{2},{3},{4}' -f $Name, $start, $seconds, $code, $Tree)
    Write-Host ("=== {0}  exit {1} after {2} s" -f $Name, $code, $seconds)
    if ($code -ne 0) {
        if ($Tolerate) { Write-Warning "$Name exited $code (baseline run: recorded, continuing)"; return }
        Fail "$Name exited $code. Evidence: $log"
    }
    Set-Content $done $Tree
}

function Assert-Certified([string]$Label) {
    $path = "$Out\certification-$Label-20261123.json"
    if (-not (Test-Path $path)) { Fail "certification report missing: $path" }
    $report = Get-Content $path -Raw | ConvertFrom-Json
    if ($report.verdict -ne 'CRITERIA MET on the supplied day') { Fail "$Label verdict: $($report.verdict)" }
    if ($report.commit -ne "tree-$Tree") { Fail "$Label was produced by $($report.commit), not tree-$Tree" }
    Write-Host "CERTIFIED $Label"
}

# ---- source identity (no database)
$actual = Get-SourceTree
if ($actual -ne $Tree) { Fail "source tree is $actual, expected $Tree. A changed source needs new certifications." }
$head = "$(git -C $Base rev-parse HEAD)".Trim()
if ($head -ne $BaseSha) { Fail "baseline worktree is at $head" }
if (git -C $Base status --short) { Fail 'baseline worktree has local changes' }
Write-Host "Source identity OK: candidate $Tree, baseline $BaseSha"
Assert-Certified 'C2-certify-1430'

$runs = foreach ($n in 1..$Repeats) { foreach ($build in 'A', 'B', 'C') { foreach ($kind in 'full', 'restart') {
    [pscustomobject]@{ Label = "$build-$kind$(if ($n -gt 1) { "-$n" })"; Build = $build; Kind = $kind } } } }
if ($Only) {
    $runs = foreach ($label in $Only) {
        $match = @($runs | Where-Object { $_.Label -eq $label })
        if ($match.Count -ne 1) { Fail "unknown benchmark label: $label" }
        $match[0]
    }
}
if ($PlanOnly) {
    Write-Host 'Plan: build; certify 09:45; Top-5 parity off, futures, both;'; $runs | ForEach-Object { Write-Host "  benchmark $($_.Label)" }
    return
}

New-Item -ItemType Directory -Force $State | Out-Null

# ---- nothing else heavy may be running
$busy = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='AdaptiveLivePerformance.exe' OR Name='testhost.exe'" |
    Where-Object { $_.Name -ne 'dotnet.exe' -or $_.CommandLine -match 'AdaptiveLivePerformance|OptionsDepth5HostBenchmark|vstest' }
if ($busy) { Fail "another benchmark or test process is running (PID $($busy.ProcessId -join ', ')). Wait for it, then run this script again." }

# ---- isolated scratch cluster
& "$Pg\pg_ctl.exe" -D $Data status | Out-Null
if ($LASTEXITCODE -ne 0) {
    & "$Pg\pg_ctl.exe" -D $Data -o '-h 127.0.0.1 -p 54340' -l D:\perf-scratch\pg.log -w start
    if ($LASTEXITCODE -ne 0) { Fail 'scratch cluster did not start' }
}
$settings = @(& "$Pg\psql.exe" -X -w -h 127.0.0.1 -p 54340 -U postgres -d postgres -At -c 'show port' -c 'show data_directory' -c 'show listen_addresses')
if ($settings.Count -ne 3 -or $settings[0] -ne '54340' -or $settings[1] -ne 'D:/perf-scratch/pg' -or $settings[2] -ne '127.0.0.1') { Fail "unexpected scratch cluster: $($settings -join ' | ')" }

Invoke-Step 'build-baseline' { dotnet build "$Base\tools\AdaptiveLivePerformance\AdaptiveLivePerformance.csproj" -c Release --disable-build-servers }
Invoke-Step 'build-candidate' { dotnet build "$Repo\tools\AdaptiveLivePerformance\AdaptiveLivePerformance.csproj" -c Release --disable-build-servers }
Invoke-Step 'build-tests' { dotnet build "$Repo\NiftySignal.Tests\NiftySignal.Tests.csproj" -c Release --disable-build-servers }

Get-ChildItem Env: | Where-Object { $_.Name -like 'ADAPTIVE_PERF_*' -or $_.Name -like 'NS_DEPTH5_*' } | ForEach-Object { Remove-Item "Env:\$($_.Name)" }
$env:ADAPTIVE_PERF_SOURCE = $Source
$env:ADAPTIVE_PERF_ADMIN = 'Host=127.0.0.1;Port=54340;Database=postgres;Username=postgres'
$env:ADAPTIVE_PERF_OUT = $Out

# ---- 1. mandatory early-restart certification
$early = 'C2-certify-0945'
$earlyJson = "$Out\certification-$early-20261123.json"
if (Test-Path $earlyJson) { Write-Host "$early already has a report: not run again" }
else {
    if (Test-Path "$Out\$early.log") { Rename-Item "$Out\$early.log" ("$early.{0:yyyyMMdd-HHmmss}.previous.log" -f (Get-Date)) }
    $env:ADAPTIVE_PERF_LABEL = $early; $env:ADAPTIVE_PERF_STAGES = 'certify'; $env:ADAPTIVE_PERF_COMMIT = "tree-$Tree"
    $env:ADAPTIVE_PERF_CERTIFY_DAY = $Day; $env:ADAPTIVE_PERF_CERTIFY_RESTART_IST = '09:45:00'; $env:ADAPTIVE_PERF_CERTIFY_DOWNTIME_SECONDS = '60'
    Invoke-Step $early { dotnet run -c Release --no-build --project "$Repo\tools\AdaptiveLivePerformance" }
}
Assert-Certified $early

# ---- 2. Level-1 / Top-5 parity on the full tick table
$parity = "$State\depth5-20261123.txt"
$env:NSE_ADAPTIVE_MIGRATION_TEST_DB = 'Host=127.0.0.1;Port=54340;Database=postgres;Username=postgres'
$env:NS_DEPTH5_CONN = "$Source;Options=-c default_transaction_read_only=on"
$env:NS_DEPTH5_DAY = $Day; $env:NS_DEPTH5_FUT = '61471'; $env:NS_DEPTH5_WEEKLY = '2026-11-23'; $env:NS_DEPTH5_BARVOL = '13650'; $env:NS_DEPTH5_OUT = $parity
foreach ($mode in 'off', 'futures', 'both') {
    $env:NS_DEPTH5_OPT = $mode
    Invoke-Step "parity-$mode" { dotnet test "$Repo\NiftySignal.Tests\NiftySignal.Tests.csproj" -c Release --no-build --filter 'FullyQualifiedName~OptionsDepth5HostBenchmark' }
}
Remove-Item Env:\NS_DEPTH5_OPT, Env:\NSE_ADAPTIVE_MIGRATION_TEST_DB

# the test itself does not assert these: check the written lines (the newest per mode)
$lines = foreach ($mode in 'off', 'futures', 'both') {
    $line = Get-Content $parity | Where-Object { $_ -match "^opt_mode=$mode day=" } | Select-Object -Last 1
    if (-not $line) { Fail "parity $mode wrote no result line (the test was skipped)" }
    if ($line -notmatch ' bars=[1-9]\d* ') { Fail "parity ${mode}: no completed bar: $line" }
    if ($line -notmatch ' core_same_on_restart=True ') { Fail "parity ${mode}: core differs on restart" }
    if ($line -notmatch ' mismatches=0 failures=0 ') { Fail "parity ${mode}: mismatches or failures" }
    $line
}
$hashes = @($lines | ForEach-Object { [regex]::Match($_, 'core_hash=(\w+)').Groups[1].Value } | Sort-Object -Unique)
if ($hashes.Count -ne 1) { Fail "parity: core_hash differs between modes: $($hashes -join ', ')" }
$bars = @($lines | ForEach-Object { [regex]::Match($_, ' bars=(\d+) ').Groups[1].Value } | Sort-Object -Unique)
if ($bars.Count -ne 1) { Fail "parity: bar counts differ between modes: $($bars -join ', ')" }

# ---- 3. performance benchmarks (after the correctness gates, so a morning deadline finds those finished)
$env:ADAPTIVE_PERF_DAYS = $Day; $env:ADAPTIVE_PERF_LIVE_DAYS = $Day; $env:ADAPTIVE_PERF_COLD_DAYS = $Day; $env:ADAPTIVE_PERF_RESTART_DAYS = $Day
$env:ADAPTIVE_PERF_STRESS = '1'; $env:ADAPTIVE_PERF_RESTART_MODE = 'full'
# The live simulation defaults to ending at 15:35:00. The last bar of 23 November completes a few seconds later, so the live run had 227 bars and the replay 228 (A-full, 26 November).
# The tool uses only the hour and minute of this value (15:35:30 ran as 15:35:00 on 27 November), so it is 15:36. The live-versus-replay comparison itself is unchanged.
$env:ADAPTIVE_PERF_SIM_END_IST = '15:36'
foreach ($run in $runs) {
    # keep the reports of an earlier attempt: the tool overwrites them
    if (-not (Test-Path "$State\$($run.Label).done")) {
        foreach ($old in @("$Out\summary-$($run.Label).json", "$Out\dump-$($run.Label)-20261123.json")) {
            if (Test-Path $old) { Move-Item $old ("$old.{0:yyyyMMdd-HHmmss}.previous" -f (Get-Date)) }
        }
    }
    $env:ADAPTIVE_PERF_LABEL = $run.Label
    $env:ADAPTIVE_PERF_COMMIT = if ($run.Build -eq 'A') { '78d9b8b' } else { "tree-$Tree" }
    $env:ADAPTIVE_PERF_CHECKPOINTS = if ($run.Build -eq 'C') { '1' } else { '0' }
    $env:ADAPTIVE_PERF_STAGES = if ($run.Kind -eq 'full') { 'inputs,live,recovery,dashboard,plans,stress,digest' } else { 'restart' }
    $project = if ($run.Build -eq 'A') { "$Base\tools\AdaptiveLivePerformance" } else { "$Repo\tools\AdaptiveLivePerformance" }
    Invoke-Step $run.Label { dotnet run -c Release --no-build --project $project } -Tolerate:($run.Build -eq 'A')
}
Get-ChildItem "$Out\dump-*-20261123.json" | Get-FileHash | Format-Table Hash, Path -AutoSize | Out-String -Width 200 | Set-Content "$State\dump-hashes.txt"

if ((Get-SourceTree) -ne $Tree) { Fail 'the source tree changed while the runs were executing' }
$marker = if ($Only) { 'SELECTED-STEPS-COMPLETED' } else { 'ALL-STEPS-COMPLETED' }
Set-Content "$State\$marker.txt" ("{0:s} tree {1} {2}" -f (Get-Date), $Tree, ($runs.Label -join ','))
Write-Host "$marker. Review of the benchmark figures is still required before a GO. The scratch cluster is left running."
