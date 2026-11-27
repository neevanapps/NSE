<#
.SYNOPSIS
    Guarded VM release: pre-flight checks, VM snapshot, binary backup, deploy (through the
    existing deploy.ps1) and post-deploy verification of NiftySignalHost / NiftySignalExecution /
    NiftySignalDashboard (the execution service only where it is installed).

.DESCRIPTION
    deploy.ps1 stays the single place that builds, tests, publishes and copies. This wrapper adds
    what a release of THIS kind needs around it, in the order it matters:

      1. Pre-flight (nothing on the VM is touched):
           - the checkout is master, clean, and its SHA is pinned (deploy.ps1 then refuses any other),
           - the desktop NiftySignalHost is stopped (only one machine may hold the FlatTrade session),
           - it is not market hours in IST (a Host restart resets the observer warm-up),
           - Tailscale shows the VM online.
      2. Read-only VM snapshot: service states, installed binary timestamps, free disk.
      3. Backup of the installed service folders on the VM (binaries AND appsettings.Local.json,
         excluding logs / screenshots / browsers). Rollback = redeploy the previous build, or copy a
         backup folder back over the install folder with the service stopped.
      4. deploy.ps1 -Target Vm -ExpectedCommitSha <sha>   (tests on, VM appsettings.Local.json untouched,
         Host is deployed and started before the Dashboard).
      5. Post-deploy verification: installed file hashes equal the published build, installed build identity is the
         expected commit, services Running with a process started after this deployment (VM clock, UTC), the Host
         and the execution service logged that build identity and no fatal / migration error. A disabled execution
         service is verified by its files only and must be stopped. Service status alone is never success.
         It does NOT certify observer recovery: check the Dashboard runtime state afterwards.

    This script never copies desktop secrets, never edits VM settings and never touches a database
    itself: the Host applies the adaptive EF migration when it starts (Database.Migrate()).

    It is not part of any release gate. deploy.ps1 requires a CLEAN worktree, so commit this file
    (or keep a copy outside the repository) before running it.

.EXAMPLE
    .\vm-deploy.ps1 -PreflightOnly
    Runs every check and the read-only VM snapshot, deploys nothing.

.EXAMPLE
    .\vm-deploy.ps1
    Full release of Host and Dashboard (prompts for confirmation and for the VM credentials).

.EXAMPLE
    .\vm-deploy.ps1 -Service Host -Yes
    Host only, no confirmation prompt.
#>
[CmdletBinding()]
param(
    [string] $VmAddress = '100.105.67.79',

    # 'Both' = Host and Dashboard, plus the Observer and the execution service where they are installed.
    [ValidateSet('Host', 'Dashboard', 'Observer', 'Execution', 'Both')]
    [string] $Service = 'Both',

    [System.Management.Automation.PSCredential] $Credential,

    # Run the pre-flight and the read-only VM snapshot, then stop.
    [switch] $PreflightOnly,

    # Pass-through to deploy.ps1. Keep tests ON for a release.
    [switch] $SkipTests,

    # Skip the binary/config backup on the VM.
    [switch] $NoBackup,

    # Emergency override of the market-hours guard (weekdays 09:00-15:45 IST); also needs a typed confirmation.
    [switch] $AllowMarketHours,

    # Minutes needed for startup, validation and rollback before the 09:00 IST guard.
    [ValidateRange(0, 600)]
    [int] $ReserveMinutes = 30,

    # No confirmation prompt.
    [switch] $Yes
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$tailscale = 'C:\Program Files\Tailscale\tailscale.exe'
. (Join-Path $repoRoot 'tools\DeploymentSafety.ps1')

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Fail($message) {
    Write-Host ""
    Write-Host $message -ForegroundColor Red
    exit 1
}

$serviceMap = @(Get-DeploymentServiceCatalog -Service $Service | ForEach-Object {
    [pscustomobject]@{ Name = $_.Name; Dll = $_.Dll; Proc = $_.Project; Optional = $_.Optional; LogsBuildIdentity = $_.LogsBuildIdentity }
})

# ---------------------------------------------------------------- 1. pre-flight (local only)
Write-Step 'Pre-flight'

$branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'master') { Fail "Checkout is '$branch'. A release is deployed from master only. Nothing deployed." }

$dirty = & git -C $repoRoot status --porcelain --untracked-files=normal
if ($dirty) {
    Write-Host ($dirty | Out-String)
    Fail 'The worktree is not clean (deploy.ps1 refuses that too). Commit or remove the changes above. Nothing deployed.'
}

$sha = (& git -C $repoRoot rev-parse HEAD).Trim()
$subject = (& git -C $repoRoot log -1 --format=%s).Trim()
Write-Host "    release : master @ $sha"
Write-Host "    subject : $subject"
$ahead = (& git -C $repoRoot rev-list --count 'origin/master..HEAD' 2>$null)
if ($LASTEXITCODE -eq 0 -and $ahead -ne '0') {
    Write-Host "    note    : master is $ahead commit(s) ahead of origin/master (not pushed). deploy.ps1 publishes the local HEAD archive, so this still deploys exactly $sha." -ForegroundColor Yellow
}

if ($Service -eq 'Host' -or $Service -eq 'Both') {
    $desktopHost = Get-Service -Name 'NiftySignalHost' -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Running' }
    if ($desktopHost) {
        Fail "The desktop NiftySignalHost is running. Only one machine may hold the FlatTrade session.`nStop it first:  Stop-Service NiftySignalHost"
    }
}

$verdict = Get-DeploymentWindowVerdict -UtcNow ([DateTime]::UtcNow) -ReserveMinutes $ReserveMinutes
Write-Host ("    IST now : {0:yyyy-MM-dd HH:mm} ({1})" -f $verdict.Ist, $verdict.Ist.DayOfWeek)
if (-not $verdict.Allowed) {
    if (-not $AllowMarketHours -and -not $PreflightOnly) { Fail "Deployment guard: $($verdict.Reason). Re-run after the close. -AllowMarketHours is for an owner-approved emergency only. Nothing deployed." }
    Write-Host "    guard    : $($verdict.Reason)" -ForegroundColor Yellow
}

if (Test-Path $tailscale) {
    $line = & $tailscale status 2>$null | Select-String -SimpleMatch $VmAddress
    if (-not $line) { Fail "Tailscale does not list $VmAddress. Check that Tailscale is running on this machine." }
    if ($line.Line -match 'offline') { Fail "Tailscale reports the VM offline: $($line.Line.Trim())" }
    Write-Host "    tailscale: $($line.Line.Trim())"
}
else {
    Write-Host "    tailscale: client not found at $tailscale (skipped)" -ForegroundColor Yellow
}

if (-not $PreflightOnly -and -not $Yes) {
    $answer = Read-Host "Deploy master @ $($sha.Substring(0, 10)) to $VmAddress ($Service)? Type 'yes' to continue"
    if ($answer -ne 'yes') { Fail 'Cancelled. Nothing deployed.' }
}

# ---------------------------------------------------------------- 2. read-only VM snapshot
Write-Step "Connecting to $VmAddress"
if (-not $Credential) { $Credential = Get-Credential -Message "Windows credentials for $VmAddress" }
try {
    $session = New-PSSession -ComputerName $VmAddress -Credential $Credential -ErrorAction Stop
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Fail "Could not open a PowerShell session. On this machine (once):  Set-Item WSMan:\localhost\Client\TrustedHosts -Value '$VmAddress' -Force"
}

function Get-VmSnapshot($remote, $services) {
    Invoke-Command -Session $remote -ArgumentList (, $services) -ScriptBlock {
        param($svcs)
        foreach ($s in $svcs) {
            $info = Get-CimInstance Win32_Service -Filter "Name='$($s.Name)'"
            $dir = if ($info) { Split-Path ($info.PathName.Trim('"')) -Parent } else { $null }
            $dll = if ($dir) { Get-Item (Join-Path $dir $s.Dll) -ErrorAction SilentlyContinue } else { $null }
            $proc = Get-Process -Name $s.Proc -ErrorAction SilentlyContinue | Select-Object -First 1
            $drive = if ($dir) { Get-PSDrive -Name $dir.Substring(0, 1) -ErrorAction SilentlyContinue } else { $null }
            [pscustomobject]@{
                Name         = $s.Name
                Status       = if ($info) { (Get-Service $s.Name).Status.ToString() } else { 'NOT INSTALLED' }
                StartMode    = if ($info) { $info.StartMode } else { $null }
                InstallDir   = $dir
                DllWritten   = if ($dll) { $dll.LastWriteTime } else { $null }
                ProcStarted  = if ($proc) { $proc.StartTime } else { $null }
                FreeGB       = if ($drive) { [Math]::Round($drive.Free / 1GB, 1) } else { $null }
            }
        }
    }
}

try {
    Write-Step 'VM snapshot (read-only)'
    $before = Get-VmSnapshot $session $serviceMap
    $before | Format-List Name, Status, StartMode, InstallDir, DllWritten, ProcStarted, FreeGB | Out-String | Write-Host
    $skipped = @($serviceMap | Where-Object { $_.Optional } | ForEach-Object { $_.Name } | Where-Object { $name = $_; @($before | Where-Object { $_.Name -eq $name -and $_.Status -eq 'NOT INSTALLED' }).Count -gt 0 })
    if ($skipped.Count -gt 0) {
        Write-Host "    not installed, skipped: $($skipped -join ', ') (register with tools\Install-ObserverService.ps1 / Install-ExecutionService.ps1)" -ForegroundColor Yellow
        $serviceMap = @($serviceMap | Where-Object { $skipped -notcontains $_.Name })
        $before = @($before | Where-Object { $skipped -notcontains $_.Name })
    }
    $missing = $before | Where-Object { $_.Status -eq 'NOT INSTALLED' }
    if ($missing) { Fail "Not installed on the VM: $($missing.Name -join ', '). deploy.ps1 needs the services registered. Nothing deployed." }
    $low = $before | Where-Object { $_.FreeGB -ne $null -and $_.FreeGB -lt 2 }
    if ($low) { Fail "Less than 2 GB free on the VM install drive. Nothing deployed." }

    if ($PreflightOnly) {
        Remove-PSSession $session -ErrorAction SilentlyContinue
        Write-Host ""
        Write-Host 'Pre-flight finished. Nothing was deployed.' -ForegroundColor Green
        return
    }

    # ------------------------------------------------------------ 3. backup on the VM
    if ($NoBackup) {
        Write-Host '    WARNING : -NoBackup: no rollback copy is taken.' -ForegroundColor Yellow
    }
    else {
        Assert-DeploymentWindow -Operation 'VM backup' -AllowMarketHours:$AllowMarketHours -ReserveMinutes $ReserveMinutes
        Write-Step 'Backing up installed binaries and settings on the VM'
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        # One call per service with scalar arguments. Any failure throws and stops here: nothing is deployed without a verified backup.
        $firstInstall = @($before | Where-Object { Test-FirstInstall -Snapshot $_ })
        $toBackUp = @($before | Where-Object { -not (Test-FirstInstall -Snapshot $_) })
        $firstInstall | ForEach-Object { Write-Host "    $($_.Name): registered, no binaries installed yet; nothing to back up (rollback = leave it stopped and disabled)" -ForegroundColor Yellow }
        $backups = @(foreach ($b in $toBackUp) {
            Invoke-Command -Session $session -ScriptBlock $InstallBackupScript -ArgumentList $b.InstallDir, $b.Name, $stamp -ErrorAction Stop
        })
        if ($backups.Count -ne $toBackUp.Count) { Fail 'Not every service returned a verified backup. Nothing deployed.' }
        $backups | ForEach-Object { Write-Host "    backup: $($_.Destination) ($($_.FilesVerified) files verified, appsettings.Local.json: $($_.LocalSettings))" }
        # Older copies are pruned only now, after every backup of this run is verified.
        foreach ($b in $backups) {
            Invoke-Command -Session $session -ScriptBlock $PruneBackupScript -ArgumentList (Split-Path $b.Destination -Parent), $b.Service, 3 -ErrorAction Stop |
                ForEach-Object { Write-Host "    pruned: $_" }
        }
    }
}
catch {
    Remove-PSSession $session -ErrorAction SilentlyContinue
    throw
}

# ---------------------------------------------------------------- 4. deploy (the existing script)
Write-Step 'Deploying with deploy.ps1'
# The verification compares instants on ONE clock (the VM's, in UTC); desktop and VM clocks or time zones are never mixed.
$deployStartTicks = Invoke-Command -Session $session -ScriptBlock { [DateTime]::UtcNow.Ticks }
$deployArgs = @{
    Target            = 'Vm'
    Service           = $Service
    VmAddress         = $VmAddress
    Credential        = $Credential
    ExpectedCommitSha = $sha
    AllowMarketHours  = [bool] $AllowMarketHours
    ReserveMinutes    = $ReserveMinutes
}
if ($SkipTests) { $deployArgs['SkipTests'] = $true }
try {
    & (Join-Path $repoRoot 'deploy.ps1') @deployArgs
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        Remove-PSSession $session -ErrorAction SilentlyContinue
        Fail "deploy.ps1 exited with $LASTEXITCODE. Check its output above; the VM backup (if taken) is untouched."
    }
}
catch {
    Remove-PSSession $session -ErrorAction SilentlyContinue
    throw
}

# ---------------------------------------------------------------- 5. post-deploy verification
Write-Step 'Post-deploy verification'
$problems = 0
try {
    Start-Sleep -Seconds 20     # let the Host finish Migrate() and its first recovery pass
    $hostDir = $null
    foreach ($s in $serviceMap) {
        $staging = Join-Path $repoRoot "artifacts\deploy-staging\$($s.Proc)"
        $expected = Get-StagedFileHashes -StagingDirectory $staging
        $facts = Invoke-Command -Session $session -ScriptBlock $DeploymentFactsScript -ArgumentList $s.Name, $s.Proc, $s.Dll, $expected, $deployStartTicks
        $disabled = @($before | Where-Object { $_.Name -eq $s.Name -and $_.StartMode -eq 'Disabled' }).Count -gt 0
        if ($disabled) { Write-Host "    $($s.Name) is disabled: files and build identity are verified, it is expected to be stopped." -ForegroundColor Yellow }
        $found = @(Get-DeploymentProblems -Facts $facts -ExpectedCommitSha $sha -DeployStartUtcTicks $deployStartTicks -RequireBuildIdentityLog:$s.LogsBuildIdentity -ExpectStopped:$disabled)
        if ($s.Name -eq 'NiftySignalHost') { $hostDir = $facts.InstallDir }
        $color = if ($found.Count -eq 0) { 'Green' } else { 'Red' }
        $started = if ($facts.ProcessStartUtcTicks) { ([DateTime]::new([long] $facts.ProcessStartUtcTicks, [DateTimeKind]::Utc)).ToString('yyyy-MM-dd HH:mm:ss') + ' UTC' } else { 'n/a' }
        Write-Host ("    {0,-22} {1,-8} files checked {2}, different {3}; process started {4}" -f $facts.Service, $facts.Status, $facts.CheckedFiles, @($facts.DifferentFiles).Count, $started) -ForegroundColor $color
        Write-Host "      build : $($facts.ProductVersion)"
        $found | ForEach-Object { Write-Host "      PROBLEM: $_" -ForegroundColor Red }
        $problems += $found.Count
    }
    Write-Host '    migrations: checked only through startup log errors (the history table is not queried; that needs database credentials).'

    if ($hostDir) {
        $log = Invoke-Command -Session $session -ArgumentList $hostDir -ScriptBlock {
            param($dir)
            $latest = Get-ChildItem (Join-Path $dir 'logs') -Filter 'niftysignal-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $latest) { return $null }
            $lines = Get-Content -LiteralPath $latest.FullName -Tail 400
            [pscustomobject]@{
                File   = $latest.FullName
                Errors = @($lines | Select-String -Pattern 'ERR|FTL|FATAL|Exception|parity mismatch|deterministic recovery|Unsupported ATM rule|permission denied|migration' -CaseSensitive:$false | ForEach-Object { $_.Line })
                Tail   = @($lines | Select-Object -Last 15)
            }
        }
        if (-not $log) {
            Write-Host '    no Host log found yet' -ForegroundColor Yellow
        }
        else {
            Write-Host "    log: $($log.File)"
            if ($log.Errors.Count -gt 0) {
                Write-Host "    lines worth reading ($($log.Errors.Count)):" -ForegroundColor Yellow
                $log.Errors | Select-Object -Last 15 | ForEach-Object { Write-Host "      $_" }
            }
            Write-Host '    last log lines:'
            $log.Tail | ForEach-Object { Write-Host "      $_" }
        }
    }
}
finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}

Write-Host ""
if ($problems -gt 0) {
    Write-Host "Deployed, but $problems verification problem(s) were found above. Rollback: .\deploy.ps1 of the previous master SHA, or copy the newest <install>\..\_backups\<service>-* folder back with the service stopped. Before rolling back to a build without NiftySignalExecution or NiftySignalObserver, stop AND disable those services (they must never run beside an older Host, which writes paper and observer state itself)." -ForegroundColor Red
    exit 1
}

Write-Host "Done: master @ $sha" -ForegroundColor Green
Write-Host @"

Next (by hand):
  - Before 09:15 IST: open the Dashboard -> Observer caption should read '... | ATM spot-atm-v1 | ...' once today's session exists (after the first poll past 09:15).
  - Observer log (Host log when InHost) after 09:15: 'spot instrument frozen: <NIFTY index token>'. A warning 'has no frozen spot instrument' means NO bands all day - check the NIFTY Index instrument and its ticks.
  - Dashboard Grid 2 (OPTION CONTEXT): dDelta / dVega columns fill from the first completed bar with a band.
  - NPT1 stays OFF (no configuration was changed).
"@
