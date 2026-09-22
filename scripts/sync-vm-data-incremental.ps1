<#
.SYNOPSIS
    Daily incremental top-up of niftysignal_vm_copy (the local replica sync-vm-database.ps1
    creates) with just the Instruments/Ticks rows that are new on the VM since the last run.

.DESCRIPTION
    sync-vm-database.ps1 (in this same folder) is a full pg_dump/restore -- the right tool for a
    fresh, complete copy, but not something to run as a daily habit against a DB that keeps
    growing (ticks alone: 3.86M+ rows and climbing). This script instead pulls only what's new,
    by Id watermark, for exactly the two raw tables NiftySignal.BacktestData/CadencePopulator.cs
    actually reads (Instruments, Ticks) to build the backtest data NiftySignal.MetricTrials runs
    against -- everything else in niftysignal_vm_copy is left exactly as the last full sync left
    it. See NiftySignal.DataSync/Program.cs for the underlying export/import tool.

    REQUIRES niftysignal_vm_copy to already exist locally with the right schema -- run
    sync-vm-database.ps1 at least once first. This script only ever INSERTs into it; it never
    creates or migrates a database.

    2026-09-15 correction: an earlier version of this script connected straight to the VM's
    Postgres from the desktop, on the assumption that sync-vm-database.ps1's own pg_dump -h usage
    proved that path was open. A live run showed otherwise -- `Test-NetConnection 100.105.67.79
    -Port 5432` succeeds on ping but fails on the TCP connect, meaning Postgres itself isn't
    reachable from off-VM right now. So "export" runs ON the VM instead, over the same
    WinRM/PSRemoting trust deploy.ps1 already established, reading the VM's own local
    NiftySignalDb through its own already-configured NiftySignal.Host\appsettings.Local.json --
    only the resulting file crosses the wire (gzip'd -- see NiftySignal.DataSync/Program.cs, a
    multi-million-row catch-up as plain NDJSON made Copy-Item -FromSession painfully slow).

    2026-09-16 correction, more fundamental: earlier versions tracked the watermark in a separate
    file ($env:LOCALAPPDATA\NiftySignal\sync-state.json) and had the import tool discard the
    source row's Id, reassigning a fresh local one. That combination broke the first time
    niftysignal_vm_copy's contents and that file disagreed about what was already synced -- which
    happened immediately, because sync-vm-database.ps1's own pg_restore ALREADY preserves the VM's
    original Ids verbatim, so a fresh full restore can silently already contain rows this script's
    own bootkeeping didn't know about, and duplicate-imported them. Fixed at the root: the import
    tool now preserves Id too (matching pg_restore's own convention -- Npgsql's default identity
    column allows an explicit value on insert), and the watermark is no longer tracked separately
    at all -- this script asks niftysignal_vm_copy itself, via max-id, what its own current max Id
    is, every run. There is now exactly one source of truth for "what's already synced": the
    database itself. A side benefit: since Id is each table's own primary key, accidentally
    re-pulling something already present now fails loudly (a primary-key violation) instead of
    Ticks (no separate unique constraint of its own) silently doubling every row.

    NiftySignal.DataSync must already be deployed to the VM for this to work -- run this script
    with -DeployTool first (and again whenever NiftySignal.DataSync itself changes).

.EXAMPLE
    .\sync-vm-data-incremental.ps1 -SaveCredential
    One-time setup: stores Windows credentials for the VM (DPAPI-encrypted, this user+machine
    only) so the daily Scheduled Task run doesn't need an interactive prompt.

.EXAMPLE
    .\sync-vm-data-incremental.ps1 -DeployTool
    Publishes NiftySignal.DataSync and copies it onto the VM, next to NiftySignal.Host's own
    install directory. Run this once, and again whenever NiftySignal.DataSync changes -- NOT
    part of the daily run.

.EXAMPLE
    .\sync-vm-data-incremental.ps1
    The daily run: pulls whatever's new since niftysignal_vm_copy's own current max Id, for both
    tables, and imports it. This is what the 15:35 IST Scheduled Task should call -- there's
    nothing date-specific about it; it naturally catches up any gap (a day the task didn't run,
    a day it partially failed) since it always starts from what's actually there, not a
    remembered date or count.
#>
[CmdletBinding()]
param(
    [string] $VmAddress = '100.105.67.79',

    [System.Management.Automation.PSCredential] $Credential,

    [switch] $SaveCredential,

    [switch] $DeployTool,

    [string] $LocalDatabase = 'niftysignal_vm_copy'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

$stateDir = Join-Path $env:LOCALAPPDATA 'NiftySignal'
$credFile = Join-Path $stateDir 'vm-credential.xml'
if (-not (Test-Path $stateDir)) {
    New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
}

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

# --- One-time credential setup, for the unattended Scheduled Task run ------------------
if ($SaveCredential) {
    $cred = Get-Credential -Message "Windows credentials for $VmAddress (stored for unattended runs)"
    $cred | Export-Clixml -Path $credFile
    Write-Host "Saved to $credFile (DPAPI-encrypted -- only this Windows user, on this machine, can decrypt it)." -ForegroundColor Green
    exit 0
}

function Get-VmCredential {
    if ($Credential) { return $Credential }
    if (Test-Path $credFile) { return Import-Clixml -Path $credFile }
    Write-Host "No saved credential -- run '.\sync-vm-data-incremental.ps1 -SaveCredential' once for unattended (Scheduled Task) runs." -ForegroundColor Yellow
    return Get-Credential -Message "Windows credentials for $VmAddress"
}

$vmCredential = Get-VmCredential
$session = New-PSSession -ComputerName $VmAddress -Credential $vmCredential

try {
    # Host's own install dir, discovered the same way deploy.ps1 does -- never assumed. The VM's
    # DataSync copy is a sibling folder of this, and this is also where the VM's own DB
    # connection string lives (its appsettings.Local.json).
    $vmHostConfigDir = Invoke-Command -Session $session -ScriptBlock {
        $svcInfo = Get-CimInstance Win32_Service -Filter "Name='NiftySignalHost'"
        if (-not $svcInfo) { return $null }
        Split-Path ($svcInfo.PathName.Trim('"')) -Parent
    }
    if (-not $vmHostConfigDir) {
        Write-Host "NiftySignalHost is not installed on $VmAddress -- can't locate its config." -ForegroundColor Red
        exit 1
    }
    $vmToolDir = Join-Path (Split-Path $vmHostConfigDir -Parent) 'NiftySignal.DataSync'

    # --- Deploy/update the tool on the VM ------------------------------------------------
    if ($DeployTool) {
        Write-Step "Publishing NiftySignal.DataSync"
        $staging = Join-Path $repoRoot 'artifacts\deploy-staging\NiftySignal.DataSync'
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        & dotnet publish (Join-Path $repoRoot 'NiftySignal.DataSync') -c Release -o $staging --nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Publish failed -- nothing deployed." -ForegroundColor Red
            exit 1
        }

        Write-Step "Copying to ${VmAddress}:$vmToolDir"
        Invoke-Command -Session $session -ArgumentList $vmToolDir -ScriptBlock {
            param($dir)
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        }
        Copy-Item -Path (Join-Path $staging '*') -Destination $vmToolDir -ToSession $session -Recurse -Force
        Write-Host "    done" -ForegroundColor Green
        exit 0
    }

    $toolExists = Invoke-Command -Session $session -ArgumentList $vmToolDir -ScriptBlock {
        param($dir)
        Test-Path (Join-Path $dir 'NiftySignal.DataSync.dll')
    }
    if (-not $toolExists) {
        Write-Host "NiftySignal.DataSync isn't deployed on the VM yet. Run '.\sync-vm-data-incremental.ps1 -DeployTool' first." -ForegroundColor Red
        exit 1
    }

    # --- Local connection string: reuse NiftySignal.Host's own known-good one, database swapped --
    $localAppSettingsPath = Join-Path $repoRoot 'NiftySignal.Host\appsettings.Local.json'
    if (-not (Test-Path $localAppSettingsPath)) {
        Write-Host "Not found: $localAppSettingsPath -- can't read the local Postgres connection string." -ForegroundColor Red
        exit 1
    }
    $localSettings = Get-Content $localAppSettingsPath -Raw | ConvertFrom-Json
    $baseLocalConnectionString = $localSettings.ConnectionStrings.NiftySignalDb
    if ([string]::IsNullOrWhiteSpace($baseLocalConnectionString)) {
        Write-Host "ConnectionStrings:NiftySignalDb is empty in $localAppSettingsPath." -ForegroundColor Red
        exit 1
    }
    # Swap only the Database= segment -- same technique NiftySignal.BacktestData/Program.cs
    # already uses (NpgsqlConnectionStringBuilder), done here with a plain regex since this is a
    # PowerShell script with no Npgsql reference of its own.
    $localConnectionString = $baseLocalConnectionString -replace '(?i)Database=[^;]*', "Database=$LocalDatabase"

    # Extracts the last "SYNC_RESULT ..." line and requires it to match, throwing with the FULL
    # raw output on failure -- deliberately using -match (not -notmatch) so $Matches is always
    # freshly populated in the success branch, never stale from an earlier iteration.
    function Get-SyncResultMatch($output, $pattern, $context) {
        $line = $output | Where-Object { $_ -match '^SYNC_RESULT' } | Select-Object -Last 1
        if ($null -ne $line -and $line -match $pattern) {
            return $Matches
        }
        throw "$context -- no parseable SYNC_RESULT line. Raw output:`n$($output -join [Environment]::NewLine)"
    }

    $tables = @('Instruments', 'Ticks')
    $hadFailure = $false

    foreach ($table in $tables) {
        Write-Step "$table"
        try {
            # The watermark, freshly asked of niftysignal_vm_copy itself every run -- not a
            # remembered value. Runs locally (no Invoke-Command), so its own $LASTEXITCODE check
            # is reliable.
            $localMaxIdOutput = & dotnet run --project (Join-Path $repoRoot 'NiftySignal.DataSync') -c Release -- max-id --table=$table --connection="$localConnectionString"
            if ($LASTEXITCODE -ne 0) {
                throw "Local max-id failed for $table. Raw output:`n$($localMaxIdOutput -join [Environment]::NewLine)"
            }
            $m = Get-SyncResultMatch $localMaxIdOutput 'maxId=(\d+)' "Could not parse local max-id output for $table"
            $since = [long]$m[1]
            Write-Host "    niftysignal_vm_copy already has up to Id $since."

            # .gz -- NiftySignal.DataSync compresses on write/decompresses on read itself
            # (--gzip), because Copy-Item -FromSession (WinRM/PSRemoting) is painfully slow on a
            # large file otherwise (live-caught 2026-09-16 on a multi-million-row catch-up).
            #
            # $env:TEMP is deliberately NOT used for this remote path -- it evaluates in whichever
            # scope the expression runs in, so a string built with $env:TEMP in THIS (local) scope
            # is the desktop's temp path, not the VM's, even when later passed to a remote command
            # (live-caught the same day: Copy-Item -FromSession failed exactly this way). Building
            # this path once from $vmToolDir (a plain string, not scope-dependent) and reusing the
            # same value everywhere below avoids the ambiguity entirely.
            $vmTempFile = Join-Path $vmToolDir "sync-temp\$table.ndjson.gz"

            $exportOutput = Invoke-Command -Session $session -ArgumentList $vmToolDir, $table, $since, $vmHostConfigDir, $vmTempFile -ScriptBlock {
                param($toolDir, $tbl, $sinceId, $hostConfigDir, $outPath)
                $settings = Get-Content (Join-Path $hostConfigDir 'appsettings.Local.json') -Raw | ConvertFrom-Json
                $conn = $settings.ConnectionStrings.NiftySignalDb
                $outDir = Split-Path $outPath -Parent
                if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
                if (Test-Path $outPath) { Remove-Item $outPath -Force }
                & dotnet (Join-Path $toolDir 'NiftySignal.DataSync.dll') export --table=$tbl --since=$sinceId --out=$outPath --gzip --connection="$conn"
            }
            # No $LASTEXITCODE check here -- Invoke-Command does not propagate the REMOTE native
            # process's exit code into this (local) $LASTEXITCODE at all; it stays whatever it was
            # from the last LOCAL native command (live-caught 2026-09-16: false-failed on an
            # export that had actually succeeded). Get-SyncResultMatch below is the real signal --
            # a genuine remote crash shows up as "no parseable SYNC_RESULT line", with the full
            # raw output (including any exception) in the thrown message.
            $m = Get-SyncResultMatch $exportOutput 'count=(\d+)\s+maxId=(\d+)' "Could not parse export output for $table"
            $exportedCount = [int]$m[1]

            if ($exportedCount -eq 0) {
                Write-Host "    no new rows since Id $since." -ForegroundColor Green
                continue
            }

            Write-Host "    $exportedCount new row(s) on the VM. Copying (gzip'd)..."
            $localTemp = Join-Path $env:TEMP "niftysignal-sync-$table.ndjson.gz"
            if (Test-Path $localTemp) { Remove-Item $localTemp -Force }
            Copy-Item -Path $vmTempFile -Destination $localTemp -FromSession $session -Force

            Write-Host "    importing locally into $LocalDatabase..."
            $importOutput = & dotnet run --project (Join-Path $repoRoot 'NiftySignal.DataSync') -c Release -- import --table=$table --in=$localTemp --gzip --connection="$localConnectionString"
            if ($LASTEXITCODE -ne 0) {
                throw "Local import failed for $table. Raw output:`n$($importOutput -join [Environment]::NewLine)"
            }
            $importLine = $importOutput | Where-Object { $_ -match '^SYNC_RESULT' } | Select-Object -Last 1
            if ($null -ne $importLine -and $importLine -match 'count=(\d+)' -and [int]$Matches[1] -ne $exportedCount) {
                Write-Host "    WARNING: imported $($Matches[1]) rows but exported $exportedCount -- investigate before trusting today's local backtest data for $table." -ForegroundColor Yellow
            }
            else {
                Write-Host "    imported $exportedCount row(s)." -ForegroundColor Green
            }

            Invoke-Command -Session $session -ArgumentList $vmTempFile -ScriptBlock {
                param($path)
                Remove-Item $path -Force -ErrorAction SilentlyContinue
            }
            Remove-Item $localTemp -Force -ErrorAction SilentlyContinue
        }
        catch {
            Write-Host "    FAILED: $($_.Exception.Message)" -ForegroundColor Red
            $hadFailure = $true
        }
    }

    if ($hadFailure) {
        Write-Host ""
        Write-Host "Completed with at least one failure -- see above. Safe to re-run: the next run" -ForegroundColor Red
        Write-Host "re-checks niftysignal_vm_copy's own state rather than trusting anything remembered" -ForegroundColor Red
        Write-Host "from this attempt." -ForegroundColor Red
        exit 1
    }

    Write-Host ""
    Write-Host "Done." -ForegroundColor Green
}
finally {
    Remove-PSSession $session
}
