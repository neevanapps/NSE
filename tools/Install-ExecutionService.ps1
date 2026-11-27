<#
.SYNOPSIS
    Owner-run, once, on the target machine in an elevated PowerShell: registers the NiftySignalExecution Windows service (paper only).

.DESCRIPTION
    Registers the service with MANUAL start and restart-on-failure actions, and does not start it. It changes no setting and no database.
    deploy.ps1 then copies the binaries into -InstallDir and starts the service; it idles until Execution:Enabled=true.
    Order of activation: docs/NSE_EXECUTION_SERVICE_RUNBOOK.md. Set the start type to Automatic only after activation is verified.

.EXAMPLE
    .\Install-ExecutionService.ps1 -InstallDir C:\Apps\NiftySignal.ExecutionService
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string] $InstallDir,
    # Account of the service. Omit for LocalSystem; use the same account as NiftySignalHost.
    [System.Management.Automation.PSCredential] $ServiceCredential
)
$ErrorActionPreference = 'Stop'
$name = 'NiftySignalExecution'
if (-not [IO.Path]::IsPathRooted($InstallDir)) { throw 'InstallDir must be a full path.' }
if (Get-Service -Name $name -ErrorAction SilentlyContinue) { throw "$name is already registered; nothing changed." }

if ($PSCmdlet.ShouldProcess($name, "register Windows service in $InstallDir (manual start, not started)")) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $definition = @{
        Name           = $name
        DisplayName    = 'NiftySignal Paper Execution'
        BinaryPathName = '"' + (Join-Path $InstallDir 'NiftySignal.ExecutionService.exe') + '"'
        StartupType    = 'Manual'
    }
    if ($ServiceCredential) { $definition['Credential'] = $ServiceCredential }
    New-Service @definition | Out-Null
    & sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Registered $name but setting its failure actions failed (sc.exe exit $LASTEXITCODE)." }
    # also apply the actions when the service stops itself with an error, not only when the process dies
    & sc.exe failureflag $name 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Registered $name but setting its failure flag failed (sc.exe exit $LASTEXITCODE)." }
    Write-Host "$name registered (manual start, not started)."
}
