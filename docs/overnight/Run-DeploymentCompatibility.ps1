<#
.SYNOPSIS
  Owner-run: validates the deployment scripts under Windows PowerShell 5.1. About a minute. Run it with:
  powershell -NoProfile -File docs\overnight\Run-DeploymentCompatibility.ps1
.DESCRIPTION
  Local only: no VM, no service, no database. Reads the tracked (shipped) settings files, never an appsettings.Local.json of an installation.
  1. records what the 5.1 JSON parser does with each shipped settings file as it is (the original defect);
  2. runs tools\Test-DeploymentSafety.ps1 and tools\TestDeploymentPackaging.ps1.
  Logs go to D:\perf-scratch\out\deploy-ps51\<time>.
#>
$ErrorActionPreference = 'Stop'
$Repo = 'D:\Projects\NSE-release-20261124'
$Tree = '4e4d39876aca868c64c1f2f71f7c13a9b84089c5'
$Out = Join-Path 'D:\perf-scratch\out\deploy-ps51' (Get-Date -Format 'yyyyMMdd-HHmmss')

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

if ($PSVersionTable.PSVersion.Major -ne 5) { Fail "this is PowerShell $($PSVersionTable.PSVersion); run it with powershell.exe (Windows PowerShell 5.1)." }
New-Item -ItemType Directory -Force $Out | Out-Null
$summary = Join-Path $Out 'summary.txt'
function Note([string] $line) { Write-Host $line; Add-Content -LiteralPath $summary -Value $line -Encoding UTF8 }

Note "PowerShell $($PSVersionTable.PSVersion) $($PSVersionTable.PSEdition)"
$actual = Get-SourceTree
Note "Source tree (without docs): $actual"
if ($actual -ne $Tree) { Fail "expected source tree $Tree" }

Note ''
Note '--- shipped settings files, parsed as the scripts did before the fix (Get-Content -Raw | ConvertFrom-Json)'
foreach ($project in 'Dashboard', 'Host', 'Observer', 'ExecutionService') {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Repo "NiftySignal.$project") -File -Filter 'appsettings*.json' | Where-Object { $_.Name -notlike '*.Local.json' }) {
        try { Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json | Out-Null; Note "NiftySignal.$project\$($file.Name): parses" }
        catch {
            # tracked files only (no secrets); the message can repeat the whole file, so it is cut
            $message = ($_.Exception.Message -replace '\s+', ' ')
            if ($message.Length -gt 200) { $message = $message.Substring(0, 200) + ' ...' }
            Note "NiftySignal.$project\$($file.Name): FAILS"
            Note "    exception: $($_.Exception.GetType().FullName)"
            Note "    id:        $($_.FullyQualifiedErrorId)"
            Note "    message:   $message"
        }
    }
}

$failed = @()
foreach ($test in 'Test-DeploymentSafety.ps1', 'TestDeploymentPackaging.ps1') {
    Note ''
    Note "--- tools\$test"
    $log = Join-Path $Out ($test -replace '\.ps1$', '.log')
    $ErrorActionPreference = 'Continue'
    & powershell.exe -NoProfile -File (Join-Path $Repo "tools\$test") *>&1 | ForEach-Object { "$_" } | Tee-Object -Variable lines | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $lines | Set-Content -LiteralPath $log -Encoding UTF8
    foreach ($line in @($lines | Where-Object { $_ -like 'FAIL *' -or $_ -match '^\d+ passed' -or $_ -like 'Deployment delta ZIP*' })) { Note $line }
    Note "exit code $code"
    if ($code -ne 0) { $failed += $test }
}

if ((Get-SourceTree) -ne $Tree) { Fail 'the source tree changed during the run' }
Note ''
if ($failed.Count -eq 0) { Note "VERDICT: PASS ($Tree)" } else { Note "VERDICT: FAILED ($($failed -join ', '))" }
Write-Host "Logs: $Out"
if ($failed.Count -gt 0) { exit 1 }
