<#
.SYNOPSIS
    Publishes NiftySignal.Host / .Observer / .ExecutionService / .Dashboard to the VM
    (or locally) and restarts the Windows Services around the copy.

.DESCRIPTION
    Replaces the manual "dotnet publish, stop the service by hand, copy, start it again"
    cycle. Everything a deploy has to get right is enforced here rather than remembered:

      - The paper execution service (NiftySignalExecution), where installed, is stopped before any
        other service and started only after every selected service deployed, and never while the
        Host is also configured to write paper state. A failed deployment leaves it stopped.
        This script never registers a service: see tools\Install-ExecutionService.ps1.
      - A Host that does not run the observer itself needs NiftySignalObserver registered (see
        tools\Install-ObserverService.ps1), checked before anything stops. The Observer is stopped
        before the Host changes and started after every selected service deployed, only when exactly
        one process is configured to write the observer database. A failed deployment leaves it stopped.
      - Tests must pass first (skip with -SkipTests only when you know why).
      - appsettings.Local.json is stripped from the staged output before it ever leaves this
        machine. That file holds the database connection string, the FlatTrade credentials
        and the dashboard password hash, and the VM's copy is NOT the same as this one --
        overwriting it points the VM at the desktop's database and breaks its own login.
      - The remote install directory is discovered from the service registration itself
        (Win32_Service PathName), so a deploy cannot land in the wrong folder.
      - Only one machine may hold a FlatTrade session at a time (one API key, two approved
        IPs). Deploying to the VM while the desktop services are still running would leave
        both fighting for the feed, which shows up as phantom disconnections. Guarded below.
      - VM deploys only copy files that actually changed (by hash, not timestamp -- publish
        re-stamps every file regardless of content). A framework-dependent publish is mostly
        unchanged runtime/dependency DLLs, and copying all of them over WinRM every time was
        the multi-minute cost; a small code change now only transfers the handful of project
        DLLs that actually differ.

.EXAMPLE
    .\deploy.ps1
    Runs tests, publishes the services, deploys to the VM over Tailscale. NiftySignalExecution is
    included only where that service is installed.

.EXAMPLE
    .\deploy.ps1 -Service Host -SkipTests
    Host only, no test run -- for a quick iteration when the suite already passed.

.EXAMPLE
    .\deploy.ps1 -Target Local
    Deploys to this desktop instead (the fallback when the VM has a problem).
#>
[CmdletBinding()]
param(
    [ValidateSet('Vm', 'Local')]
    [string] $Target = 'Vm',

    # 'Both' (the default) = Host and Dashboard, plus the Observer and the execution service where they are installed.
    [ValidateSet('Host', 'Dashboard', 'Observer', 'Execution', 'Both')]
    [string] $Service = 'Both',

    # Tailscale address. The public static IP (8.234.96.76) also works, but Tailscale keeps
    # the traffic on the WireGuard tunnel rather than the open internet.
    [string] $VmAddress = '100.105.67.79',

    [System.Management.Automation.PSCredential] $Credential,

    [switch] $SkipTests,

    # Release gate: pass the reviewed master SHA to prove the checkout being published.
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommitSha,

    # Bypasses the "desktop services are still running" guard. Only correct when you have
    # deliberately decided both machines should be up (they normally must not be).
    [switch] $Force,

    # Emergency override of the market-hours guard (weekdays 09:00-15:45 IST). Never enough on its own: the script also
    # requires a typed confirmation that open-position risk was assessed.
    [switch] $AllowMarketHours,

    # Minutes needed for startup, validation and rollback; a deployment that would reach into the guard window is refused.
    [ValidateRange(0, 600)]
    [int] $ReserveMinutes = 30
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
. (Join-Path $repoRoot 'tools\DeploymentSafety.ps1')
$guard = @{ AllowMarketHours = [bool] $AllowMarketHours; ReserveMinutes = $ReserveMinutes }
# Fail early; checked again immediately before every service stop below.
Assert-DeploymentWindow -Operation 'deployment start' @guard

# Assembly metadata must identify the source actually published, including untracked source files.
$sourceStatus = & git -C $repoRoot status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify source worktree; nothing deployed.' }
if ($sourceStatus) { throw 'Deployment requires a clean source worktree; commit or remove changes first. Nothing deployed.' }
$verifiedSha = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source revision; nothing deployed.' }
if ($ExpectedCommitSha -and $verifiedSha -ne $ExpectedCommitSha) {
    throw "Expected source SHA $ExpectedCommitSha but checkout is $verifiedSha. Nothing deployed."
}

$sourceBranch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
if ($ExpectedCommitSha -and $sourceBranch -ne 'master') {
    throw 'Expected-SHA release deployment requires the master branch. Nothing deployed.'
}

$services = @(Get-DeploymentServiceCatalog -Service $Service)

function Write-Step($message) {
    if ($script:stepClock) {
        Write-Host "    elapsed: $([Math]::Round($script:stepClock.Elapsed.TotalSeconds, 1)) s"
    }
    $script:stepClock = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

# --- Guard: never leave two FlatTrade sessions competing -------------------------------
if ($Target -eq 'Vm' -and -not $Force) {
    $running = Get-Service -Name 'NiftySignalHost' -ErrorAction SilentlyContinue |
               Where-Object { $_.Status -eq 'Running' }
    if ($running) {
        Write-Host ""
        Write-Host "The desktop's NiftySignalHost is still running." -ForegroundColor Red
        Write-Host "Only one machine can hold a FlatTrade session -- running both makes the feed" -ForegroundColor Red
        Write-Host "drop on one or other of them, which looks exactly like the disconnection" -ForegroundColor Red
        Write-Host "problem we are trying to measure." -ForegroundColor Red
        Write-Host ""
        Write-Host "Stop it first:  Stop-Service NiftySignalHost" -ForegroundColor Yellow
        Write-Host "Or re-run with -Force if you genuinely want both up." -ForegroundColor Yellow
        exit 1
    }
}

# Publish a tracked immutable archive of the verified revision. Subsequent editor changes
# cannot change the binaries while leaving their assembly SHA unchanged.
$stagingRoot = Join-Path $repoRoot 'artifacts\deploy-staging'
$sourceTag = [Guid]::NewGuid().ToString('N')
$sourceArchive = Join-Path $stagingRoot "source-$sourceTag.zip"
$publishSourceRoot = Join-Path $stagingRoot "source-$sourceTag"
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
& git -C $repoRoot archive --format=zip --output $sourceArchive $verifiedSha
if ($LASTEXITCODE -ne 0) { throw 'Cannot archive the verified source revision; nothing deployed.' }
Expand-Archive -LiteralPath $sourceArchive -DestinationPath $publishSourceRoot

# Build Release once. Test and publish reuse exactly these binaries and source metadata.
$commitSha = $verifiedSha
$buildUtc = [DateTimeOffset]::UtcNow.ToString('O')
Write-Step "Building validated Release source $sourceBranch @ $commitSha"
& dotnet build (Join-Path $publishSourceRoot 'NiftySignal.slnx') -c Release --nologo "-p:SourceBranch=$sourceBranch" "-p:CommitSha=$commitSha" "-p:BuildUtc=$buildUtc"
if ($LASTEXITCODE -ne 0) { throw 'Release build failed; nothing deployed.' }

# --- Tests -----------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step "Running tests"
    & dotnet test (Join-Path $publishSourceRoot 'NiftySignal.Tests\NiftySignal.Tests.csproj') -c Release --no-build --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed -- nothing deployed." -ForegroundColor Red
        exit 1
    }
}

# --- Build/source identity ---------------------------------------------------------------
Write-Host "    source: $sourceBranch @ $commitSha"
Write-Host "    build : $buildUtc UTC"

# --- Publish to staging ----------------------------------------------------------------

foreach ($svc in $services) {
    $staging = Join-Path $stagingRoot $svc.Project
    Write-Step "Publishing $($svc.Project)"

    if (Test-Path $staging) {
        Remove-Item $staging -Recurse -Force
    }

    & dotnet publish (Join-Path $publishSourceRoot $svc.Project) -c Release --no-build --no-restore -o $staging --nologo "-p:SourceBranch=$sourceBranch" "-p:CommitSha=$commitSha" "-p:BuildUtc=$buildUtc"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Publish failed for $($svc.Project) -- nothing deployed." -ForegroundColor Red
        exit 1
    }

    # The single most important line in this script: the target machine keeps its own
    # secrets and connection string. Never ship this file.
    $localSettings = Join-Path $staging 'appsettings.Local.json'
    if (Test-Path $localSettings) {
        Remove-Item $localSettings -Force
        Write-Host "    stripped appsettings.Local.json from the staged output"
    }
}

Remove-Item -LiteralPath $sourceArchive -Force
Remove-Item -LiteralPath $publishSourceRoot -Recurse -Force

# --- Deploy ----------------------------------------------------------------------------
# Runs on the target (local or remote): install folder, start mode and state of one registered service, or nothing.
$serviceInfo = {
    param($name)
    $info = Get-CimInstance Win32_Service -Filter "Name='$name'"
    if (-not $info) { return $null }
    [pscustomobject]@{ Dir = (Split-Path ($info.PathName.Trim('"')) -Parent); StartMode = $info.StartMode; Running = ((Get-Service $name).Status.ToString() -ne 'Stopped') }
}
$stopService = {
    param($name)
    Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
    (Get-Service $name).WaitForStatus('Stopped', '00:00:30')
}

# Stops the paper execution service (if installed) before anything else changes, so no paper writer runs against a half-deployed
# system and a rollback never runs beside it. It is started again only by Start-ExecutionAfterDeployment.
function Stop-ExecutionBeforeDeployment([hashtable] $on) {
    $info = Invoke-Command @on -ScriptBlock $serviceInfo -ArgumentList 'NiftySignalExecution'
    if ($info -and $info.Running) {
        Write-Step 'Stopping NiftySignalExecution (restarted after all services are deployed)'
        Assert-DeploymentWindow -Operation 'stop NiftySignalExecution' @guard
        Invoke-Command @on -ScriptBlock $stopService -ArgumentList 'NiftySignalExecution'
    }
    $info
}

function Start-ExecutionAfterDeployment([hashtable] $on, $before) {
    if (-not $before) { return }
    # Restore what ran before; a first deployment of the service starts it too (it idles while Execution:Enabled=false).
    $selected = @($services | Where-Object { $_.Key -eq 'Execution' }).Count -gt 0
    if (-not $before.Running -and -not $selected) { return }
    if ($before.StartMode -eq 'Disabled') { Write-Host '    NiftySignalExecution is disabled: left stopped.' -ForegroundColor Yellow; return }
    Write-Step 'Starting NiftySignalExecution'
    $hostInfo = Invoke-Command @on -ScriptBlock $serviceInfo -ArgumentList 'NiftySignalHost'
    $dashInfo = Invoke-Command @on -ScriptBlock $serviceInfo -ArgumentList 'NiftySignalDashboard'
    $ownership = Invoke-Command @on -ScriptBlock $PaperOwnershipScript -ArgumentList $hostInfo.Dir, $before.Dir, $dashInfo.Dir
    $problems = @(Get-PaperOwnershipProblems -Ownership $ownership)
    if ($problems.Count -gt 0) { throw "NiftySignalExecution was NOT started and stays stopped. $($problems -join ' ')" }
    $state = Invoke-Command @on -ArgumentList 'NiftySignalExecution' -ScriptBlock {
        param($name)
        Start-Service -Name $name
        (Get-Service $name).WaitForStatus('Running', '00:00:30')
        (Get-Service $name).Status.ToString()
    }
    Write-Host "    NiftySignalExecution is $state (Execution:Enabled=$($ownership.ExecutionEnabled))" -ForegroundColor Green
}

# Checked before anything stops: deploying a Host must never leave the system without an observer.
function Assert-ObserverInstalled([hashtable] $on) {
    $hostInfo = Invoke-Command @on -ScriptBlock $serviceInfo -ArgumentList 'NiftySignalHost'
    $observer = Invoke-Command @on -ScriptBlock $serviceInfo -ArgumentList 'NiftySignalObserver'
    $settings = Invoke-Command @on -ScriptBlock $ObserverSettingsScript -ArgumentList $hostInfo.Dir, $observer.Dir
    $hostSelected = @($services | Where-Object { $_.Key -eq 'Host' }).Count -gt 0
    $problems = @(Get-ObserverInstallProblems -Settings $settings -ObserverInstalled ([bool] $observer) -HostSelected $hostSelected)
    if ($problems.Count -gt 0) { throw "Nothing deployed. $($problems -join ' ')" }
}

# The Observer is stopped before a Host or Observer change, so an older Host build can never run beside it. A Dashboard or execution deployment leaves it running.
function Stop-ObserverBeforeDeployment([hashtable] $on) {
    if (@($services | Where-Object { $_.Key -eq 'Host' -or $_.Key -eq 'Observer' }).Count -eq 0) { return $null }
    $info = Invoke-Command @on -ScriptBlock $serviceInfo -ArgumentList 'NiftySignalObserver'
    if ($info -and $info.Running) {
        Write-Step 'Stopping NiftySignalObserver (restarted after all services are deployed)'
        Assert-DeploymentWindow -Operation 'stop NiftySignalObserver' @guard
        Invoke-Command @on -ScriptBlock $stopService -ArgumentList 'NiftySignalObserver'
    }
    $info
}

function Start-ObserverAfterDeployment([hashtable] $on, $before) {
    if (-not $before) { return }
    if ($before.StartMode -eq 'Disabled') { Write-Host '    NiftySignalObserver is disabled: left stopped.' -ForegroundColor Yellow; return }
    $hostInfo = Invoke-Command @on -ScriptBlock $serviceInfo -ArgumentList 'NiftySignalHost'
    $settings = Invoke-Command @on -ScriptBlock $ObserverSettingsScript -ArgumentList $hostInfo.Dir, $before.Dir
    if ($settings.HostBuildSeparatesObserver -and $settings.HostMode -eq 'InHost') {
        Write-Host '    The Host runs the observer (InHost): NiftySignalObserver is left stopped. Disable that service.' -ForegroundColor Yellow
        return
    }
    $problems = @(Get-ObserverStartProblems -Settings $settings)
    if ($problems.Count -gt 0) { throw "NiftySignalObserver was NOT started and stays stopped: no observer is running. $($problems -join ' ')" }
    Write-Step 'Starting NiftySignalObserver'
    $state = Invoke-Command @on -ArgumentList 'NiftySignalObserver' -ScriptBlock {
        param($name)
        Start-Service -Name $name
        (Get-Service $name).WaitForStatus('Running', '00:00:30')
        (Get-Service $name).Status.ToString()
    }
    Write-Host "    NiftySignalObserver is $state" -ForegroundColor Green
}

if ($Target -eq 'Local') {
    Assert-ObserverInstalled @{}
    $executionBefore = Stop-ExecutionBeforeDeployment @{}
    $observerBefore = Stop-ObserverBeforeDeployment @{}
    foreach ($svc in $services) {
        if ($svc.Optional -and -not (Get-CimInstance Win32_Service -Filter "Name='$($svc.Name)'")) {
            Write-Host "    $($svc.Name) is not installed: skipped." -ForegroundColor Yellow
            continue
        }
        $staging = Join-Path $stagingRoot $svc.Project
        $installed = (Get-CimInstance Win32_Service -Filter "Name='$($svc.Name)'").PathName
        if (-not $installed) {
            Write-Host "Service $($svc.Name) is not installed on this machine." -ForegroundColor Red
            exit 1
        }
        $targetDir = Split-Path ($installed.Trim('"')) -Parent

        Write-Step "$($svc.Name) -> $targetDir"
        Assert-DeploymentWindow -Operation "stop $($svc.Name)" @guard
        Stop-Service -Name $svc.Name -Force -ErrorAction SilentlyContinue
        (Get-Service $svc.Name).WaitForStatus('Stopped', '00:00:30')

        Copy-Item -Path (Join-Path $staging '*') -Destination $targetDir -Recurse -Force
        if ($svc.Name -eq 'NiftySignalDashboard') {
            & dotnet (Join-Path $targetDir 'NiftySignal.Dashboard.dll') --install-screenshot-browser
            if ($LASTEXITCODE -ne 0) { throw 'Screenshot Chromium installation failed; Dashboard remains stopped.' }
        }
        if ($svc.Key -eq 'Execution' -or $svc.Key -eq 'Observer') { continue }   # started last, after the ownership checks
        Start-Service -Name $svc.Name
        (Get-Service $svc.Name).WaitForStatus('Running', '00:00:30')
        Write-Host "    $($svc.Name) is Running" -ForegroundColor Green
    }
    Start-ObserverAfterDeployment @{} $observerBefore
    Start-ExecutionAfterDeployment @{} $executionBefore
}
else {
    . (Join-Path $repoRoot 'tools\DeploymentPackaging.ps1')
    Write-Step "Connecting to $VmAddress"
    if (-not $Credential) {
        $Credential = Get-Credential -Message "Windows credentials for $VmAddress"
    }

    try {
        $session = New-PSSession -ComputerName $VmAddress -Credential $Credential -ErrorAction Stop
    }
    catch {
        Write-Host ""
        Write-Host "Could not open a PowerShell session to $VmAddress." -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        Write-Host ""
        Write-Host "On the VM (as administrator), enable remoting once:" -ForegroundColor Yellow
        Write-Host "    Enable-PSRemoting -Force" -ForegroundColor Yellow
        Write-Host "On this machine, trust the VM once (both are outside a domain):" -ForegroundColor Yellow
        Write-Host "    Set-Item WSMan:\localhost\Client\TrustedHosts -Value '$VmAddress' -Force" -ForegroundColor Yellow
        exit 1
    }

    try {
        $remote = @{ Session = $session }
        # Every required service must be registered before anything is stopped.
        foreach ($svc in $services) {
            if (-not $svc.Optional -and -not (Invoke-Command @remote -ScriptBlock $serviceInfo -ArgumentList $svc.Name)) {
                Write-Host "Service $($svc.Name) is not installed on $VmAddress. Nothing deployed." -ForegroundColor Red
                exit 1
            }
        }
        Assert-ObserverInstalled $remote
        $executionBefore = Stop-ExecutionBeforeDeployment $remote
        $observerBefore = Stop-ObserverBeforeDeployment $remote

        foreach ($svc in $services) {
            $staging = Join-Path $stagingRoot $svc.Project

            # Discovered, not assumed -- the service itself knows where it was installed.
            $targetDir = Invoke-Command -Session $session -ArgumentList $svc.Name -ScriptBlock {
                param($name)
                $svcInfo = Get-CimInstance Win32_Service -Filter "Name='$name'"
                if (-not $svcInfo) { return $null }
                Split-Path ($svcInfo.PathName.Trim('"')) -Parent
            }

            if (-not $targetDir) {
                if ($svc.Optional) { Write-Host "    $($svc.Name) is not installed on $VmAddress`: skipped." -ForegroundColor Yellow; continue }
                Write-Host "Service $($svc.Name) is not installed on $VmAddress." -ForegroundColor Red
                exit 1
            }

            Write-Step "$($svc.Name) -> ${VmAddress}:$targetDir"

            # --- Work out which files actually changed, before stopping the service -------
            # Hashing, not size/timestamp: `dotnet publish` re-copies every output file on
            # every run, so even byte-identical framework DLLs get a fresh timestamp -- a
            # timestamp/size compare would think everything changed and defeat the point.
            # Hashing costs a few seconds of CPU; the payoff is skipping the WinRM transfer
            # for the ~90% of a framework-dependent publish that's unchanged runtime/
            # dependency DLLs, which is the actual multi-minute cost here (copying, not
            # compiling or testing).
            $localFiles = Get-ChildItem -Path $staging -Recurse -File
            $localHashes = @{}
            foreach ($f in $localFiles) {
                $rel = $f.FullName.Substring($staging.Length + 1)
                $localHashes[$rel] = (Get-FileHash -Path $f.FullName -Algorithm MD5).Hash
            }

            $remoteHashes = Invoke-Command -Session $session -ArgumentList $targetDir -ScriptBlock {
                param($dir)
                $result = @{}
                if (Test-Path $dir) {
                    # logs\ is runtime output, not part of the deployed app, and Serilog holds
                    # today's file open for writes while the service is running -- Get-FileHash
                    # on a locked file throws, and with $ErrorActionPreference = 'Stop' upstream
                    # that aborts the whole deploy (live-caught 2026-09-08). Skip the whole
                    # folder rather than just catching the error, since it should never have
                    # been part of a "did the deployed app change" comparison anyway.
                    Get-ChildItem -Path $dir -Recurse -File |
                        Where-Object { $_.FullName -notlike (Join-Path $dir 'logs\*') -and
                            $_.FullName -notlike (Join-Path $dir 'screenshots\*') -and
                            $_.FullName -notlike (Join-Path $dir '.playwright-browsers\*') } |
                        ForEach-Object {
                            $file = $_
                            $rel = $file.FullName.Substring($dir.Length + 1)
                            try {
                                $result[$rel] = (Get-FileHash -Path $file.FullName -Algorithm MD5 -ErrorAction Stop).Hash
                            }
                            catch {
                                # Any other unexpectedly-locked file: treat as unknown rather than
                                # aborting the deploy -- $toCopy below copies it since there's no
                                # hash to compare against, which is the safe direction to be wrong in.
                                Write-Warning "Could not hash $($file.FullName) on the remote side -- will copy the local version unconditionally: $($_.Exception.Message)"
                            }
                        }
                }
                $result
            }

            $toCopy = $localHashes.Keys | Where-Object {
                -not $remoteHashes.ContainsKey($_) -or $remoteHashes[$_] -ne $localHashes[$_]
            }
            Write-Host "    $($toCopy.Count) of $($localHashes.Count) files changed"

            # Transfer one delta ZIP before stopping the service, instead of hundreds of WinRM
            # file-copy round trips. The archive contains only changed published files.
            $localPackage = Join-Path $stagingRoot ("delta-" + [Guid]::NewGuid().ToString('N') + '.zip')
            $remotePackage = $null
            if ($toCopy.Count -gt 0) {
                New-ChangedDeploymentArchive -StagingDirectory $staging -RelativePaths @($toCopy) -ArchivePath $localPackage
                try {
                    $remotePackage = Invoke-Command -Session $session -ScriptBlock {
                        Join-Path ([System.IO.Path]::GetTempPath()) ("nifty-deploy-" + [Guid]::NewGuid().ToString('N') + '.zip')
                    }
                    Copy-Item -LiteralPath $localPackage -Destination $remotePackage -ToSession $session -Force
                } catch {
                    if ($remotePackage) {
                        Invoke-Command -Session $session -ArgumentList $remotePackage -ScriptBlock {
                            param($zip) Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
                        }
                    }
                    throw
                } finally { Remove-Item -LiteralPath $localPackage -Force -ErrorAction SilentlyContinue }
            }

            # The build, the tests and the transfer take minutes: the guard is evaluated again here, at the disruptive step.
            # A refusal leaves this service running; services already deployed in this run stay deployed (see the output above).
            Assert-DeploymentWindow -Operation "stop $($svc.Name)" @guard
            Invoke-Command -Session $session -ArgumentList $svc.Name -ScriptBlock {
                param($name)
                Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
                (Get-Service $name).WaitForStatus('Stopped', '00:00:30')
            }
            Write-Host "    stopped"

            if ($toCopy.Count -eq 0) {
                Write-Host "    nothing to copy"
            }
            else {
                Invoke-Command -Session $session -ArgumentList $remotePackage, $targetDir -ScriptBlock {
                    param($zip, $dir)
                    try { Expand-Archive -LiteralPath $zip -DestinationPath $dir -Force }
                    finally { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
                }
                Write-Host "    files copied"
            }

            if ($svc.Key -eq 'Execution' -or $svc.Key -eq 'Observer') { continue }   # started last, after the ownership checks

            $state = Invoke-Command -Session $session -ArgumentList $svc.Name, $targetDir -ScriptBlock {
                param($name, $dir)
                if ($name -eq 'NiftySignalDashboard') {
                    & dotnet (Join-Path $dir 'NiftySignal.Dashboard.dll') --install-screenshot-browser
                    if ($LASTEXITCODE -ne 0) { throw 'Screenshot Chromium installation failed; Dashboard remains stopped.' }
                }
                Start-Service -Name $name
                (Get-Service $name).WaitForStatus('Running', '00:00:30')
                (Get-Service $name).Status.ToString()
            }
            Write-Host "    $($svc.Name) is $state" -ForegroundColor Green
        }
        Start-ObserverAfterDeployment $remote $observerBefore
        Start-ExecutionAfterDeployment $remote $executionBefore
    }
    finally {
        Remove-PSSession $session
    }
}

Write-Host ""
if ($script:stepClock) { Write-Host "    elapsed: $([Math]::Round($script:stepClock.Elapsed.TotalSeconds, 1)) s" }
Write-Host "Done." -ForegroundColor Green
