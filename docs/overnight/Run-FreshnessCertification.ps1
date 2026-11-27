<#
.SYNOPSIS
  Owner-run, fail-fast: the two mandatory real-data recovery certifications (restart 14:30 and 09:45) on the tree with the observer freshness gate. About 45 minutes.
.DESCRIPTION
  Reads niftysignal_vm_copy as research_ro (password from pgpass.conf only). Writes only to the scratch cluster on 127.0.0.1:54340 and to D:\perf-scratch.
  New labels (D-certify-*): the reports of tree aa906ab are not touched. Re-running resumes: a certification already met for this tree is not run again.
  -PlanOnly checks the source identity only; it opens no database and starts nothing.
#>
param([switch]$PlanOnly)

$ErrorActionPreference = 'Stop'
$Repo = 'D:\Projects\NSE-release-20261124'
$Out = 'D:\perf-scratch\out'
$State = "$Out\freshness"
$Pg = 'C:\Program Files\PostgreSQL\18\bin'
$Data = 'D:\perf-scratch\pg'
$Tree = '45008e381918ec091c37b1b9c861ea9a1fa03630'
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

function Test-Certified([string]$Label) {
    $path = "$Out\certification-$Label-20261123.json"
    if (-not (Test-Path $path)) { return $false }
    $report = Get-Content $path -Raw | ConvertFrom-Json
    return ($report.verdict -eq 'CRITERIA MET on the supplied day' -and $report.commit -eq "tree-$Tree")
}

$actual = Get-SourceTree
if ($actual -ne $Tree) { Fail "source tree is $actual, expected $Tree. A changed source needs new certifications." }
Write-Host "Source identity OK: $Tree"
if ($PlanOnly) { Write-Host 'Plan: build; certify restart 14:30; certify restart 09:45.'; return }

New-Item -ItemType Directory -Force $State | Out-Null

$busy = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='AdaptiveLivePerformance.exe' OR Name='testhost.exe'" |
    Where-Object { $_.Name -ne 'dotnet.exe' -or $_.CommandLine -match 'AdaptiveLivePerformance|Benchmark|vstest' }
if ($busy) { Fail "another benchmark or test process is running (PID $($busy.ProcessId -join ', ')). Wait for it, then run this script again." }

& "$Pg\pg_ctl.exe" -D $Data status | Out-Null
if ($LASTEXITCODE -ne 0) {
    & "$Pg\pg_ctl.exe" -D $Data -o '-h 127.0.0.1 -p 54340' -l D:\perf-scratch\pg.log -w start
    if ($LASTEXITCODE -ne 0) { Fail 'scratch cluster did not start' }
}
$settings = @(& "$Pg\psql.exe" -X -w -h 127.0.0.1 -p 54340 -U postgres -d postgres -At -c 'show port' -c 'show data_directory' -c 'show listen_addresses')
if ($settings.Count -ne 3 -or $settings[0] -ne '54340' -or $settings[1] -ne 'D:/perf-scratch/pg' -or $settings[2] -ne '127.0.0.1') { Fail "unexpected scratch cluster: $($settings -join ' | ')" }

$ErrorActionPreference = 'Continue'
dotnet build "$Repo\tools\AdaptiveLivePerformance\AdaptiveLivePerformance.csproj" -c Release --disable-build-servers 2>&1 | Tee-Object -FilePath "$State\build.log" | Out-Host
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($code -ne 0) { Fail "build failed. Evidence: $State\build.log" }

Get-ChildItem Env: | Where-Object { $_.Name -like 'ADAPTIVE_PERF_*' } | ForEach-Object { Remove-Item "Env:\$($_.Name)" }
$env:ADAPTIVE_PERF_SOURCE = $Source
$env:ADAPTIVE_PERF_ADMIN = 'Host=127.0.0.1;Port=54340;Database=postgres;Username=postgres'
$env:ADAPTIVE_PERF_OUT = $Out

foreach ($restart in '14:30:00', '09:45:00') {
    $label = "D-certify-$($restart.Substring(0, 5).Replace(':', ''))"
    if (Test-Certified $label) { Write-Host "SKIP $label (already met for this tree)"; continue }
    foreach ($old in @("$Out\certification-$label-20261123.json", "$State\$label.log")) { if (Test-Path $old) { Move-Item $old ("$old.{0:yyyyMMdd-HHmmss}.previous" -f (Get-Date)) } }
    $env:ADAPTIVE_PERF_LABEL = $label; $env:ADAPTIVE_PERF_STAGES = 'certify'; $env:ADAPTIVE_PERF_COMMIT = "tree-$Tree"
    $env:ADAPTIVE_PERF_CERTIFY_DAY = $Day; $env:ADAPTIVE_PERF_CERTIFY_RESTART_IST = $restart; $env:ADAPTIVE_PERF_CERTIFY_DOWNTIME_SECONDS = '60'
    Write-Host ("=== {0}  start {1:HH:mm:ss}" -f $label, (Get-Date))
    $start = Get-Date
    $ErrorActionPreference = 'Continue'
    dotnet run -c Release --no-build --project "$Repo\tools\AdaptiveLivePerformance" 2>&1 | Tee-Object -FilePath "$State\$label.log" | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Add-Content "$State\durations.csv" ('{0},{1:s},{2},{3},{4}' -f $label, $start, [int]((Get-Date) - $start).TotalSeconds, $code, $Tree)
    if ($code -ne 0) { Fail "$label exited $code. Evidence: $State\$label.log" }
    if (-not (Test-Certified $label)) { Fail "$label did not meet the criteria. Evidence: $Out\certification-$label-20261123.json" }
    Write-Host "CERTIFIED $label"
}

if ((Get-SourceTree) -ne $Tree) { Fail 'the source tree changed while the runs were executing' }
Set-Content "$State\CERTIFICATION-COMPLETED.txt" ("{0:s} tree {1}" -f (Get-Date), $Tree)
Write-Host 'CERTIFICATION-COMPLETED. Both restarts met the criteria. The scratch cluster is left running.'
