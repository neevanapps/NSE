<#
.SYNOPSIS
    Publishes NiftySignal.Host / NiftySignal.Dashboard to the VM (or locally) and restarts
    the Windows Services around the copy.

.DESCRIPTION
    Replaces the manual "dotnet publish, stop the service by hand, copy, start it again"
    cycle. Everything a deploy has to get right is enforced here rather than remembered:

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
    Runs tests, publishes both services, deploys to the VM over Tailscale.

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

    [ValidateSet('Host', 'Dashboard', 'Both')]
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
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

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

$services = @()
if ($Service -eq 'Both' -or $Service -eq 'Host') {
    $services += [pscustomobject]@{ Name = 'NiftySignalHost'; Project = 'NiftySignal.Host' }
}
if ($Service -eq 'Both' -or $Service -eq 'Dashboard') {
    $services += [pscustomobject]@{ Name = 'NiftySignalDashboard'; Project = 'NiftySignal.Dashboard' }
}

function Write-Step($message) {
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

# --- Tests -----------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step "Running tests"
    & dotnet test $publishSourceRoot --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed -- nothing deployed." -ForegroundColor Red
        exit 1
    }
}

# --- Build/source identity ---------------------------------------------------------------
$commitSha = $verifiedSha
$buildUtc = [DateTimeOffset]::UtcNow.ToString('O')
Write-Host "    source: $sourceBranch @ $commitSha"
Write-Host "    build : $buildUtc UTC"

# --- Publish to staging ----------------------------------------------------------------

foreach ($svc in $services) {
    $staging = Join-Path $stagingRoot $svc.Project
    Write-Step "Publishing $($svc.Project)"

    if (Test-Path $staging) {
        Remove-Item $staging -Recurse -Force
    }

    & dotnet publish (Join-Path $publishSourceRoot $svc.Project) -c Release -o $staging --nologo "-p:SourceBranch=$sourceBranch" "-p:CommitSha=$commitSha" "-p:BuildUtc=$buildUtc"
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
if ($Target -eq 'Local') {
    foreach ($svc in $services) {
        $staging = Join-Path $stagingRoot $svc.Project
        $installed = (Get-CimInstance Win32_Service -Filter "Name='$($svc.Name)'").PathName
        if (-not $installed) {
            Write-Host "Service $($svc.Name) is not installed on this machine." -ForegroundColor Red
            exit 1
        }
        $targetDir = Split-Path ($installed.Trim('"')) -Parent

        Write-Step "$($svc.Name) -> $targetDir"
        Stop-Service -Name $svc.Name -Force -ErrorAction SilentlyContinue
        (Get-Service $svc.Name).WaitForStatus('Stopped', '00:00:30')

        Copy-Item -Path (Join-Path $staging '*') -Destination $targetDir -Recurse -Force
        Start-Service -Name $svc.Name
        (Get-Service $svc.Name).WaitForStatus('Running', '00:00:30')
        Write-Host "    $($svc.Name) is Running" -ForegroundColor Green
    }
}
else {
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
                        Where-Object { $_.FullName -notlike (Join-Path $dir 'logs\*') } |
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
                # Create every needed destination folder in one round trip, not one per file.
                $destDirs = $toCopy | ForEach-Object { Split-Path (Join-Path $targetDir $_) -Parent } | Sort-Object -Unique
                Invoke-Command -Session $session -ArgumentList (, $destDirs) -ScriptBlock {
                    param($dirs)
                    foreach ($d in $dirs) {
                        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
                    }
                }

                foreach ($rel in $toCopy) {
                    Copy-Item -Path (Join-Path $staging $rel) -Destination (Join-Path $targetDir $rel) -ToSession $session -Force
                }
                Write-Host "    files copied"
            }

            $state = Invoke-Command -Session $session -ArgumentList $svc.Name -ScriptBlock {
                param($name)
                Start-Service -Name $name
                (Get-Service $name).WaitForStatus('Running', '00:00:30')
                (Get-Service $name).Status.ToString()
            }
            Write-Host "    $($svc.Name) is $state" -ForegroundColor Green
        }
    }
    finally {
        Remove-PSSession $session
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
