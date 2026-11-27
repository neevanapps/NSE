<#
.SYNOPSIS
  Owner-run, fail-fast: same-day full-table Top-5 recovery, baseline 78d9b8b against the candidate, then the production 300 s limit on the candidate. One run at a time.
.DESCRIPTION
  Builds throw-away COPIES of both sources under D:\perf-scratch\cmp and adds the bench-only test files from docs\overnight\compare to them. No production source is changed.
  Reads niftysignal_vm_copy as research_ro (password from pgpass.conf only), in read-only transactions. Writes only to the scratch cluster on 127.0.0.1:54340 and to D:\perf-scratch.
  Default: both-mode baseline, candidate, baseline again (order and cache check); candidate on a small staged copy of the same rows; candidate with the 300 s limit and checkpoints.
  Re-running resumes: a step with a .done marker for the same source tree is skipped.
  -BuildOnly builds the copies and opens no database. -IncludeOff adds the Top-5 off pair.
  -Pooled adds the same full-table runs with observer connection pooling ON (the production default; the test helper turns it off), after any step still pending.
#>
param([switch]$BuildOnly, [switch]$IncludeOff, [switch]$Pooled)

$ErrorActionPreference = 'Stop'
$Repo = 'D:\Projects\NSE-release-20261124'
$Base = 'D:\perf-scratch\nse-78d9b8b'
$Root = 'D:\perf-scratch\cmp'
$State = 'D:\perf-scratch\out\compare'
$Pg = 'C:\Program Files\PostgreSQL\18\bin'
$Data = 'D:\perf-scratch\pg'
$Tree = 'aa906ab302ce7e770d03c1c4002aa40e5a15808e'
$BaseSha = '78d9b8bd84d0b9bb38dd91a80ae89f0dad8fd5fe'
$Overlay = "$Repo\docs\overnight\compare"

function Fail($message) { throw "STOPPED: $message" }

function Get-SourceTree {
    $env:GIT_INDEX_FILE = 'D:\perf-scratch\compare.index'
    try {
        if (Test-Path $env:GIT_INDEX_FILE) { Remove-Item $env:GIT_INDEX_FILE }
        $ErrorActionPreference = 'Continue'
        git -C $Repo add -A 2>$null; if ($LASTEXITCODE -ne 0) { Fail 'git add (temporary index) failed' }
        git -C $Repo rm -r -q --cached docs 2>$null | Out-Null; if ($LASTEXITCODE -ne 0) { Fail 'git rm (temporary index) failed' }
        $hash = git -C $Repo write-tree; if ($LASTEXITCODE -ne 0) { Fail 'git write-tree failed' }
        return "$hash".Trim()
    }
    finally { Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue }
}

$actual = Get-SourceTree
if ($actual -ne $Tree) { Fail "source tree is $actual, expected $Tree" }
if ("$(git -C $Base rev-parse HEAD)".Trim() -ne $BaseSha) { Fail 'baseline worktree is not at the baseline commit' }
if (git -C $Base status --short) { Fail 'baseline worktree has local changes' }
Write-Host "Source identity OK: candidate $Tree, baseline $BaseSha"

New-Item -ItemType Directory -Force $Root, $State | Out-Null

$busy = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='AdaptiveLivePerformance.exe' OR Name='testhost.exe'" |
    Where-Object { $_.Name -ne 'dotnet.exe' -or $_.CommandLine -match 'AdaptiveLivePerformance|RecoveryComparison|OptionsDepth5HostBenchmark|vstest' }
if ($busy) { Fail "another benchmark or test process is running (PID $($busy.ProcessId -join ', ')). Wait for it, then run this script again." }

# ---- scratch copies with the bench-only files (A = baseline, B = candidate)
$copies = @{ A = @{ Repo = $Base; Ref = $BaseSha; Files = @('RecoveryComparisonBenchmark.cs') }
             B = @{ Repo = $Repo; Ref = $Tree; Files = @('RecoveryComparisonBenchmark.cs', 'RecoveryTimeoutBenchmark.cs') } }
foreach ($name in 'A', 'B') {
    $c = $copies[$name]; $dir = "$Root\$name-$($c.Ref.Substring(0, 7))"; $c.Dir = $dir
    if (-not (Test-Path "$dir\NiftySignal.slnx")) {
        $zip = "$dir.zip"
        git -C $c.Repo archive --format=zip -o $zip $c.Ref; if ($LASTEXITCODE -ne 0) { Fail "git archive $name failed" }
        Expand-Archive -Path $zip -DestinationPath $dir -Force
    }
    foreach ($file in $c.Files) { Copy-Item "$Overlay\$file" "$dir\NiftySignal.Tests\AdaptiveObserver\$file" -Force }
    Write-Host "=== build $name"
    dotnet build "$dir\NiftySignal.Tests\NiftySignal.Tests.csproj" -c Release --disable-build-servers 2>&1 | Tee-Object -FilePath "$State\build-$name.log" | Select-String -Pattern 'error |Warn|Error|Elapsed' | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail "build $name failed. Evidence: $State\build-$name.log" }
}
Get-FileHash "$Overlay\*.cs" | Format-Table Hash, Path -AutoSize | Out-String -Width 200 | Set-Content "$State\overlay-hashes.txt"
if ($BuildOnly) { Write-Host 'BUILD-ONLY: no database was opened.'; return }

# ---- isolated scratch cluster
& "$Pg\pg_ctl.exe" -D $Data status | Out-Null
if ($LASTEXITCODE -ne 0) {
    & "$Pg\pg_ctl.exe" -D $Data -o '-h 127.0.0.1 -p 54340' -l D:\perf-scratch\pg.log -w start
    if ($LASTEXITCODE -ne 0) { Fail 'scratch cluster did not start' }
}
$settings = @(& "$Pg\psql.exe" -X -w -h 127.0.0.1 -p 54340 -U postgres -d postgres -At -c 'show port' -c 'show data_directory' -c 'show listen_addresses')
if ($settings.Count -ne 3 -or $settings[0] -ne '54340' -or $settings[1] -ne 'D:/perf-scratch/pg' -or $settings[2] -ne '127.0.0.1') { Fail "unexpected scratch cluster: $($settings -join ' | ')" }

Get-ChildItem Env: | Where-Object { $_.Name -like 'NS_*' -or $_.Name -like 'ADAPTIVE_PERF_*' } | ForEach-Object { Remove-Item "Env:\$($_.Name)" }
$env:NSE_ADAPTIVE_MIGRATION_TEST_DB = 'Host=127.0.0.1;Port=54340;Database=postgres;Username=postgres'
$env:NS_DEPTH5_CONN = 'Host=localhost;Port=5432;Database=niftysignal_vm_copy;Username=research_ro;Options=-c default_transaction_read_only=on'
$env:NS_DEPTH5_DAY = '2026-11-23'; $env:NS_DEPTH5_FUT = '61471'; $env:NS_DEPTH5_WEEKLY = '2026-11-23'; $env:NS_DEPTH5_BARVOL = '13650'

function Invoke-Run([string]$Name, [string]$Build, [string]$Filter) {
    $done = "$State\$Name.done"
    if ((Test-Path $done) -and ((Get-Content $done -Raw) -match $Tree)) { Write-Host "SKIP $Name (already completed)"; return }
    foreach ($old in @("$State\$Name.log", "$State\$Name.jsonl", "$State\$Name.jsonl.plans.txt")) { if (Test-Path $old) { Move-Item $old ("$old.{0:yyyyMMdd-HHmmss}.previous" -f (Get-Date)) } }
    Write-Host ("=== {0}  start {1:HH:mm:ss}" -f $Name, (Get-Date))
    $start = Get-Date
    $ErrorActionPreference = 'Continue'
    dotnet test "$($copies[$Build].Dir)\NiftySignal.Tests\NiftySignal.Tests.csproj" -c Release --no-build --filter $Filter 2>&1 | Tee-Object -FilePath "$State\$Name.log" | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $seconds = [int]((Get-Date) - $start).TotalSeconds
    Add-Content "$State\durations.csv" ('{0},{1:s},{2},{3},{4}' -f $Name, $start, $seconds, $code, $Tree)
    Write-Host ("=== {0}  exit {1} after {2} s" -f $Name, $code, $seconds)
    if ($code -ne 0) { Fail "$Name exited $code. Evidence: $State\$Name.log" }
    if (-not (Test-Path "$State\$Name.jsonl")) { Fail "$Name wrote no result (the test was skipped)" }
    Set-Content $done $Tree
}

function Invoke-Compare([string]$Name, [string]$Build, [string]$Mode, [string]$Staged) {
    $env:NS_DEPTH5_OPT = $Mode; $env:NS_CMP_LABEL = $Name; $env:NS_CMP_STAGED = $Staged; $env:NS_CMP_OUT = "$State\$Name.jsonl"
    Invoke-Run $Name $Build 'FullyQualifiedName~RecoveryComparisonBenchmark'
    Remove-Item Env:\NS_CMP_OUT
}

Invoke-Compare 'A-both-1' 'A' 'both' '0'
Invoke-Compare 'B-both-1' 'B' 'both' '0'
Invoke-Compare 'A-both-2' 'A' 'both' '0'
Invoke-Compare 'B-both-staged' 'B' 'both' '1'
if ($IncludeOff) { Invoke-Compare 'A-off-1' 'A' 'off' '0'; Invoke-Compare 'B-off-1' 'B' 'off' '0' }

# ---- production limit and checkpoints, candidate, full table
$env:NS_DEPTH5_OPT = 'both'; $env:NS_TIMEOUT_LIMIT = '300'; $env:NS_TIMEOUT_ATTEMPTS = '4'; $env:NS_TIMEOUT_CHECKPOINTS = '1'; $env:NS_TIMEOUT_OUT = "$State\B-timeout-checkpoints.jsonl"
Invoke-Run 'B-timeout-checkpoints' 'B' 'FullyQualifiedName~RecoveryTimeoutBenchmark'
Remove-Item Env:\NS_TIMEOUT_OUT

# ---- connection pooling as in production: shows how much of the time above is the test helper opening a new connection per operation
if ($Pooled) {
    $env:NS_CMP_POOLING = '1'
    Invoke-Compare 'A-both-pooled' 'A' 'both' '0'
    Invoke-Compare 'B-both-pooled' 'B' 'both' '0'
    $env:NS_DEPTH5_OPT = 'both'; $env:NS_TIMEOUT_OUT = "$State\B-timeout-pooled.jsonl"
    Invoke-Run 'B-timeout-pooled' 'B' 'FullyQualifiedName~RecoveryTimeoutBenchmark'
    Remove-Item Env:\NS_TIMEOUT_OUT, Env:\NS_CMP_POOLING
}

if ((Get-SourceTree) -ne $Tree) { Fail 'the source tree changed while the runs were executing' }
Set-Content "$State\COMPARISON-COMPLETED.txt" ("{0:s} tree {1}" -f (Get-Date), $Tree)
Write-Host 'COMPARISON-COMPLETED. The figures still need review. The scratch cluster is left running.'
