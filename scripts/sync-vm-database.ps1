<#
.SYNOPSIS
    Dumps the VM's live NiftySignal Postgres database and restores it into a local copy, so
    analysis (SQL queries, the residual-tracking research, anything ad hoc) can happen with the
    VM shut down -- no need to pay for VM uptime just to run a query against real data.

.DESCRIPTION
    This is a snapshot copy, not live replication (deliberately -- see the 2026-09-09
    conversation this was requested in): true Postgres streaming replication needs the primary
    continuously reachable to stay in sync, which defeats the point of shutting the VM down.
    A dump/restore run once a day (right before you shut the VM down, or whenever you want a
    fresh copy) gets the same practical outcome with none of that ongoing complexity.

    Restores into a SEPARATELY NAMED local database (niftysignal_vm_copy by default), not the
    desktop's own niftysignal database that local dev/testing already uses -- deliberately, so
    this can never clobber local-only data. The target database is dropped and recreated fresh
    every run (this is a snapshot copy, not something to accumulate manual changes in).

    Requires the PostgreSQL client tools (pg_dump/pg_restore/psql) -- present alongside any local
    Postgres server install (C:\Program Files\PostgreSQL\<version>\bin) even though that directory
    usually isn't on PATH by default; pass -PgBinPath if yours lives somewhere else.

    Both the VM and local connections will prompt for a password interactively if neither has
    one cached (a .pgpass file, or PGPASSWORD set in the calling shell) -- this script never
    hardcodes or asks for a password itself.

.EXAMPLE
    .\sync-vm-database.ps1
    Dumps niftysignal from the VM (100.105.67.79) and restores into local niftysignal_vm_copy.

.EXAMPLE
    .\sync-vm-database.ps1 -LocalDatabase niftysignal_2026_09_09 -KeepDumpFile
    Restores into a dated database name instead of overwriting the same one each time, and
    keeps the raw .dump file afterward instead of deleting it.
#>
[CmdletBinding()]
param(
    [string] $VmAddress = '100.105.67.79',
    [string] $VmDatabase = 'niftysignal',
    [string] $VmUser = 'postgres',

    [string] $LocalDatabase = 'niftysignal_vm_copy',
    [string] $LocalUser = 'postgres',

    [string] $PgBinPath = 'C:\Program Files\PostgreSQL\18\bin',

    # Parallel restore jobs -- pg_restore only parallelizes with the custom (-Fc) format this
    # script always dumps in, safe to raise on a machine with more cores/faster disk than the
    # default guess here.
    [int] $RestoreJobs = 4,

    # Keeps the .dump file in the scratch folder afterward instead of deleting it once the
    # restore succeeds -- e.g. to keep a same-day backup around, or to restore it again without
    # re-dumping from the VM.
    [switch] $KeepDumpFile
)

$ErrorActionPreference = 'Stop'

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

$pgDump = Join-Path $PgBinPath 'pg_dump.exe'
$pgRestore = Join-Path $PgBinPath 'pg_restore.exe'
$psql = Join-Path $PgBinPath 'psql.exe'
foreach ($tool in @($pgDump, $pgRestore, $psql)) {
    if (-not (Test-Path $tool)) {
        Write-Host "Not found: $tool" -ForegroundColor Red
        Write-Host "Pass -PgBinPath pointing at your PostgreSQL install's bin directory." -ForegroundColor Yellow
        exit 1
    }
}

$dumpPath = Join-Path $env:TEMP "niftysignal-vm-$(Get-Date -Format 'yyyyMMdd-HHmmss').dump"

# --- Dump from the VM ---------------------------------------------------------------------
Write-Step "Dumping '$VmDatabase' from the VM ($VmAddress)"
& $pgDump -h $VmAddress -p 5432 -U $VmUser -d $VmDatabase -Fc --no-owner --no-privileges -f $dumpPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "pg_dump failed -- nothing restored locally." -ForegroundColor Red
    exit 1
}

$dumpSizeMb = [Math]::Round((Get-Item $dumpPath).Length / 1MB, 1)
Write-Host "    dump complete: $dumpPath ($dumpSizeMb MB)" -ForegroundColor Green

# --- Recreate the local target database ----------------------------------------------------
Write-Step "Recreating local database '$LocalDatabase'"

# Terminate any existing connections first -- DROP DATABASE fails if anything (e.g. a leftover
# psql session, or pgAdmin) still has it open.
& $psql -h localhost -U $LocalUser -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$LocalDatabase' AND pid <> pg_backend_pid();" | Out-Null

& $psql -h localhost -U $LocalUser -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $LocalDatabase;"
if ($LASTEXITCODE -ne 0) { Write-Host "Could not drop the existing local database." -ForegroundColor Red; exit 1 }

& $psql -h localhost -U $LocalUser -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $LocalDatabase;"
if ($LASTEXITCODE -ne 0) { Write-Host "Could not create the local database." -ForegroundColor Red; exit 1 }

# --- Restore --------------------------------------------------------------------------------
Write-Step "Restoring into '$LocalDatabase' ($RestoreJobs parallel jobs)"
& $pgRestore -h localhost -U $LocalUser -d $LocalDatabase --jobs $RestoreJobs --no-owner --no-privileges $dumpPath
if ($LASTEXITCODE -ne 0) {
    # pg_restore's exit code can be nonzero even for a substantively-complete restore (e.g. a
    # handful of skippable ownership/privilege warnings) -- --no-owner/--no-privileges above
    # already avoid the most common source of those, so a nonzero code here is worth looking at
    # rather than assuming it's always fatal, but not silently swallowed either.
    Write-Host "pg_restore reported a nonzero exit code -- check the output above for real errors" -ForegroundColor Yellow
    Write-Host "(ownership/permission warnings are expected and harmless; missing tables/rows are not)." -ForegroundColor Yellow
}

if ($KeepDumpFile) {
    Write-Host ""
    Write-Host "Dump file kept at $dumpPath" -ForegroundColor Green
}
else {
    Remove-Item $dumpPath -Force
}

Write-Host ""
Write-Host "Done. Connect to '$LocalDatabase' on localhost to analyze -- e.g.:" -ForegroundColor Green
Write-Host "    & `"$psql`" -h localhost -U $LocalUser -d $LocalDatabase" -ForegroundColor Green
