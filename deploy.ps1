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

    # Bypasses the "desktop services are still running" guard. Only correct when you have
    # deliberately decided both machines should be up (they normally must not be).
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

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

# --- Tests -----------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step "Running tests"
    & dotnet test $repoRoot --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed -- nothing deployed." -ForegroundColor Red
        exit 1
    }
}

# --- Publish to staging ----------------------------------------------------------------
$stagingRoot = Join-Path $repoRoot 'artifacts\deploy-staging'

foreach ($svc in $services) {
    $staging = Join-Path $stagingRoot $svc.Project
    Write-Step "Publishing $($svc.Project)"

    if (Test-Path $staging) {
        Remove-Item $staging -Recurse -Force
    }

    & dotnet publish (Join-Path $repoRoot $svc.Project) -c Release -o $staging --nologo
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

            Invoke-Command -Session $session -ArgumentList $svc.Name -ScriptBlock {
                param($name)
                Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
                (Get-Service $name).WaitForStatus('Stopped', '00:00:30')
            }
            Write-Host "    stopped"

            Copy-Item -Path (Join-Path $staging '*') -Destination $targetDir -ToSession $session -Recurse -Force
            Write-Host "    files copied"

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
