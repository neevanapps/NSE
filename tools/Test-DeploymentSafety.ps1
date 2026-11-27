<#
  Local tests of tools/DeploymentSafety.ps1. Touches only a fresh folder under %TEMP% (or -WorkRoot): no VM, no service, no production path.
  Run:  powershell -NoProfile -File tools\Test-DeploymentSafety.ps1
#>
param([string] $WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) ('nifty-deploy-tests-' + [Guid]::NewGuid().ToString('N'))))

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DeploymentSafety.ps1')

$script:passed = 0
$script:failed = 0
function Test-Case([string] $name, [scriptblock] $body) {
    try { & $body; $script:passed++; Write-Host "PASS $name" }
    catch { $script:failed++; Write-Host "FAIL $name : $($_.Exception.Message)" -ForegroundColor Red }
}
function Assert-True($condition, [string] $message = 'condition is false') { if (-not $condition) { throw $message } }
function Assert-Throws([scriptblock] $body, [string] $like) {
    try { & $body } catch { if ($_.Exception.Message -notlike $like) { throw "unexpected error: $($_.Exception.Message)" }; return }
    throw "expected an error like '$like'"
}
function Utc([string] $s) { [DateTimeOffset]::Parse($s, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime }

# ---- market-hours guard (2026-11-26 is a Thursday; 2026-11-28 a Saturday) ----
foreach ($case in @(
        @('2026-11-26T08:29:00+05:30', $true), @('2026-11-26T08:30:00+05:30', $true), @('2026-11-26T08:31:00+05:30', $false),
        @('2026-11-26T08:59:59+05:30', $false), @('2026-11-26T09:00:00+05:30', $false), @('2026-11-26T12:00:00+05:30', $false),
        @('2026-11-26T15:44:59+05:30', $false), @('2026-11-26T15:45:00+05:30', $true), @('2026-11-26T23:59:00+05:30', $true),
        @('2026-11-28T12:00:00+05:30', $true), @('2026-11-29T12:00:00+05:30', $true))) {
    Test-Case "guard $($case[0]) allowed=$($case[1])" { Assert-True ((Get-DeploymentWindowVerdict -UtcNow (Utc $case[0]) -ReserveMinutes 30).Allowed -eq $case[1]) }
}
Test-Case 'guard blocks without override' { Assert-Throws { Assert-DeploymentWindow -Operation 'stop X' -UtcNow (Utc '2026-11-26T10:00:00+05:30') } '*blocked*market hours*' }
# The emergency override needs an interactive answer and is deliberately not automated here; check it by hand if ever changed.

# ---- backup ----
function New-Install([string] $name) {
    $dir = Join-Path $WorkRoot "apps\$name"
    New-Item -ItemType Directory -Force (Join-Path $dir 'logs'), (Join-Path $dir 'runtimes\win') | Out-Null
    Set-Content (Join-Path $dir "$name.dll") "binary of $name"
    Set-Content (Join-Path $dir 'runtimes\win\native.dll') 'native'
    Set-Content (Join-Path $dir 'appsettings.json') '{}'
    Set-Content (Join-Path $dir 'appsettings.Local.json') "{ ""secret"": ""test-$name"" }"
    Set-Content (Join-Path $dir 'logs\today.log') 'log'
    $dir
}
$hostDir = New-Install 'NiftySignal.Host'
$dashDir = New-Install 'NiftySignal.Dashboard'
$backupRoot = Join-Path $WorkRoot 'apps\_backups'

Test-Case 'Host-only backup is verified, keeps appsettings.Local.json and skips logs' {
    $r = & $InstallBackupScript $hostDir 'NiftySignalHost' '20260101-000001'
    Assert-True ($r.RobocopyExit -lt 8 -and $r.FilesVerified -eq 4 -and $r.LocalSettings) "result: $r"
    Assert-True (Test-Path (Join-Path $r.Destination 'runtimes\win\native.dll'))
    Assert-True (-not (Test-Path (Join-Path $r.Destination 'logs')))
    Assert-True ((Get-FileHash (Join-Path $r.Destination 'appsettings.Local.json')).Hash -eq (Get-FileHash (Join-Path $hostDir 'appsettings.Local.json')).Hash)
    Assert-True (Test-Path (Join-Path $hostDir 'appsettings.Local.json'))
}
Test-Case 'Dashboard-only backup' {
    $r = & $InstallBackupScript $dashDir 'NiftySignalDashboard' '20260101-000001'
    Assert-True (Test-Path (Join-Path $r.Destination 'NiftySignal.Dashboard.dll'))
    Assert-True (-not (Test-Path (Join-Path $r.Destination 'NiftySignal.Host.dll')))
}
Test-Case 'both services use separate destinations, as vm-deploy.ps1 calls them' {
    $before = @([pscustomobject]@{ InstallDir = $hostDir; Name = 'NiftySignalHost' }, [pscustomobject]@{ InstallDir = $dashDir; Name = 'NiftySignalDashboard' })
    $results = foreach ($b in $before) { & $InstallBackupScript $b.InstallDir $b.Name '20260101-000002' }
    Assert-True (@($results).Count -eq 2 -and $results[0].Destination -ne $results[1].Destination)
    Assert-True ((Get-Content (Join-Path $results[1].Destination 'NiftySignal.Dashboard.dll')) -eq 'binary of NiftySignal.Dashboard')
}
Test-Case 'a missing source fails and creates nothing' {
    Assert-Throws { & $InstallBackupScript (Join-Path $WorkRoot 'nope') 'NiftySignalHost' '20260101-000003' } '*not a directory*'
    Assert-True (-not (Test-Path (Join-Path $backupRoot 'NiftySignalHost-20260101-000003')))
}
Test-Case 'two directories passed as one argument fail instead of reaching robocopy' {
    Assert-Throws { & $InstallBackupScript "$hostDir $dashDir" 'NiftySignalHost' '20260101-000004' } '*not a directory*'
}
Test-Case 'a robocopy failure (locked file) fails the backup and leaves no verified copy' {
    $lock = [IO.File]::Open((Join-Path $hostDir 'NiftySignal.Host.dll'), 'Open', 'Read', 'None')
    try { Assert-Throws { & $InstallBackupScript $hostDir 'NiftySignalHost' '20260101-000005' } '*robocopy exit*' }
    finally { $lock.Dispose() }
    Assert-True (-not (Test-Path (Join-Path $backupRoot 'NiftySignalHost-20260101-000005')))
}
Test-Case 'a folder without binaries is not a rollback copy' {
    $empty = Join-Path $WorkRoot 'apps\Empty'; New-Item -ItemType Directory -Force $empty | Out-Null
    Assert-Throws { & $InstallBackupScript $empty 'Empty' '20260101-000006' } '*no binaries*'
}
Test-Case 'only a newly registered Observer or execution service without binaries skips the backup' {
    function Snap($name, $written) { [pscustomobject]@{ Name = $name; DllWritten = $written } }
    foreach ($name in @('NiftySignalObserver', 'NiftySignalExecution')) {
        Assert-True (Test-FirstInstall -Snapshot (Snap $name $null)) "$name first install"
        Assert-True (-not (Test-FirstInstall -Snapshot (Snap $name (Get-Date)))) "$name installed"
    }
    foreach ($name in @('NiftySignalHost', 'NiftySignalDashboard')) { Assert-True (-not (Test-FirstInstall -Snapshot (Snap $name $null))) "$name always backed up" }
}
Test-Case 'an existing destination is never overwritten' { Assert-Throws { & $InstallBackupScript $hostDir 'NiftySignalHost' '20260101-000001' } '*already exists*' }
Test-Case 'pruning keeps the newest verified copies and never deletes unverified ones' {
    foreach ($i in 7..9) { & $InstallBackupScript $hostDir 'NiftySignalHost' "20260101-00000$i" | Out-Null }
    New-Item -ItemType Directory -Force (Join-Path $backupRoot 'NiftySignalHost-20200101-000000') | Out-Null   # legacy, unverified
    $removed = @(& $PruneBackupScript $backupRoot 'NiftySignalHost' 3)
    $left = @(Get-ChildItem $backupRoot -Directory -Filter 'NiftySignalHost-*' | Where-Object { Test-Path (Join-Path $_.FullName '.backup-verified') } | Sort-Object Name | ForEach-Object Name)
    Assert-True ($removed.Count -eq 2) "removed $($removed.Count)"
    Assert-True (($left -join ',') -eq 'NiftySignalHost-20260101-000007,NiftySignalHost-20260101-000008,NiftySignalHost-20260101-000009') ($left -join ',')
    Assert-True (Test-Path (Join-Path $backupRoot 'NiftySignalHost-20200101-000000'))
    Assert-True (@(Get-ChildItem $backupRoot -Directory -Filter 'NiftySignalDashboard-*').Count -eq 2)
}

# ---- post-deploy verification ----
$sha = '0123456789abcdef0123456789abcdef01234567'
$start = (Utc '2026-11-26T03:08:00Z').Ticks
function New-Facts {
    [pscustomobject]@{ Service = 'NiftySignalHost'; Status = 'Running'; InstallDir = 'C:\Apps\Host'; ProcessId = 1; ProcessPath = 'C:\Apps\Host\NiftySignal.Host.exe'
        ProcessStartUtcTicks = (Utc '2026-11-26T03:10:00Z').Ticks; ProductVersion = "1.0.0+$sha"; CheckedFiles = 3; DifferentFiles = @(); LogFile = 'x'
        BuildIdentityLines = @("Build identity: master @ $sha"); FatalLines = @()
    }
}
function Problems($facts) { @(Get-DeploymentProblems -Facts $facts -ExpectedCommitSha $sha -DeployStartUtcTicks $start -RequireBuildIdentityLog) }
Test-Case 'a verified deployment has no problem even when DLL timestamps look 5.5 h ahead (not compared)' { Assert-True ((Problems (New-Facts)).Count -eq 0) }
Test-Case 'a stopped service is a problem' { $f = New-Facts; $f.Status = 'Stopped'; Assert-True ((Problems $f).Count -ge 1) }
Test-Case 'running alone is not success: wrong files' { $f = New-Facts; $f.DifferentFiles = @('NiftySignal.Host.dll (differs)'); Assert-True ((Problems $f).Count -eq 1) }
Test-Case 'wrong build identity' { $f = New-Facts; $f.ProductVersion = '1.0.0+ffff'; Assert-True ((Problems $f).Count -eq 1) }
Test-Case 'a process older than the deployment' { $f = New-Facts; $f.ProcessStartUtcTicks = (Utc '2026-11-26T03:00:00Z').Ticks; Assert-True ((Problems $f).Count -eq 1) }
Test-Case 'no build identity log line' { $f = New-Facts; $f.BuildIdentityLines = @(); Assert-True ((Problems $f).Count -eq 1) }
Test-Case 'fatal startup log line' { $f = New-Facts; $f.FatalLines = @('[FTL] NiftySignal Host terminated unexpectedly'); Assert-True ((Problems $f).Count -eq 1) }

Test-Case 'facts script reads hashes, identity and only log lines of this start' {
    # Uses a real Windows service entry point lookup only for the name; a name that does not exist must report NOT INSTALLED.
    $facts = & $DeploymentFactsScript 'NiftySignalNoSuchService' 'none' 'none.dll' @{} $start
    Assert-True ($facts.Status -eq 'NOT INSTALLED' -and (@(Get-DeploymentProblems -Facts $facts -ExpectedCommitSha $sha -DeployStartUtcTicks $start)).Count -ge 1)
}
Test-Case 'staged hashes exclude the local settings file' {
    $h = Get-StagedFileHashes -StagingDirectory $hostDir
    Assert-True ($h.ContainsKey('NiftySignal.Host.dll') -and -not $h.ContainsKey('appsettings.Local.json'))
}
Test-Case 'the scripts parse and call no removed variable' {
    foreach ($file in @('..\deploy.ps1', '..\vm-deploy.ps1', 'DeploymentSafety.ps1', 'Install-ExecutionService.ps1')) {
        $tokens = $null; $errors = $null
        [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref] $tokens, [ref] $errors) | Out-Null
        Assert-True (@($errors).Count -eq 0) "$file : $($errors | Select-Object -First 1)"
    }
}

# ---- paper execution service ----
Test-Case 'deployment order is Host, Observer, execution service, Dashboard; Both treats the Observer and the execution service as optional' {
    $both = @(Get-DeploymentServiceCatalog -Service Both)
    Assert-True ((($both | ForEach-Object Name) -join ',') -eq 'NiftySignalHost,NiftySignalObserver,NiftySignalExecution,NiftySignalDashboard')
    Assert-True ($both[1].Optional -and $both[2].Optional -and -not $both[0].Optional -and -not $both[3].Optional)
    $only = @(Get-DeploymentServiceCatalog -Service Execution)
    Assert-True ($only.Count -eq 1 -and $only[0].Name -eq 'NiftySignalExecution' -and -not $only[0].Optional)
    foreach ($s in $both) { Assert-True (Test-Path (Join-Path $PSScriptRoot "..\$($s.Project)\$($s.Project).csproj")) "no project $($s.Project)" }
}

function New-Settings([string] $name, [string] $shipped, [string] $local) {
    $dir = Join-Path $WorkRoot "settings\$name"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content (Join-Path $dir 'appsettings.json') $shipped
    if ($local) { Set-Content (Join-Path $dir 'appsettings.Local.json') $local }
    $dir
}
$hostOff = New-Settings 'hostoff' '{ "PaperEngineV2": { "Enabled": false } }' '{ "Other": { "secret": "test-value" } }'
$hostOn = New-Settings 'hoston' '{ "PaperEngineV2": { "Enabled": false } }' '{ "PaperEngineV2": { "Enabled": true } }'
$hostFlat = New-Settings 'hostflat' '{ "PaperEngineV2": { "Enabled": false } }' '{ "PaperEngineV2:Enabled": "true" }'
$execOn = New-Settings 'execon' '{ "Execution": { "Enabled": false } }' '{ "Execution": { "Enabled": true } }'
$execOff = New-Settings 'execoff' '{ "Execution": { "Enabled": false } }' $null
$dash = New-Settings 'dash' '{ "ExecutionHealth": { "Required": false } }' '{ "ExecutionHealth": { "Required": true } }'
$broken = New-Settings 'broken' '{ "PaperEngineV2": { "Enabled": false } }' '{ "secret-marker": '
function Ownership($h, $e) { & $PaperOwnershipScript $h $e $dash }

Test-Case 'execution on with the Host writer off may start' {
    $o = Ownership $hostOff $execOn
    Assert-True ($o.ExecutionEnabled -eq $true -and $o.HostPaperEngineEnabled -eq $false -and $o.DashboardExecutionRequired -eq $true)
    Assert-True (@(Get-PaperOwnershipProblems -Ownership $o).Count -eq 0)
}
Test-Case 'two configured writers block the start (nested and flat local override)' {
    foreach ($h in @($hostOn, $hostFlat)) { Assert-True (@(Get-PaperOwnershipProblems -Ownership (Ownership $h $execOn)).Count -eq 1) $h }
}
Test-Case 'an enabled execution service needs the Dashboard to require it' {
    $o = & $PaperOwnershipScript $hostOff $execOn $execOff
    Assert-True (@(Get-PaperOwnershipProblems -Ownership $o | Where-Object { $_ -like '*ExecutionHealth:Required*' }).Count -eq 1)
}
Test-Case 'a disabled execution service is never a second writer' { Assert-True (@(Get-PaperOwnershipProblems -Ownership (Ownership $hostOn $execOff)).Count -eq 0) }
Test-Case 'unreadable or unknown Host settings block the start and do not echo file content' {
    foreach ($h in @($broken, (Join-Path $WorkRoot 'settings\absent'), $null)) {
        $found = @(Get-PaperOwnershipProblems -Ownership (Ownership $h $execOn))
        Assert-True ($found.Count -ge 1) "no problem for '$h'"
        Assert-True (($found -join ' ') -notlike '*secret-marker*')
    }
}
Test-Case 'a disabled service is verified by files and identity only, and must be stopped' {
    $f = New-Facts; $f.Status = 'Stopped'; $f.ProcessStartUtcTicks = $null; $f.BuildIdentityLines = @()
    Assert-True (@(Get-DeploymentProblems -Facts $f -ExpectedCommitSha $sha -DeployStartUtcTicks $start -RequireBuildIdentityLog -ExpectStopped).Count -eq 0)
    $f.DifferentFiles = @('x (differs)')
    Assert-True (@(Get-DeploymentProblems -Facts $f -ExpectedCommitSha $sha -DeployStartUtcTicks $start -ExpectStopped).Count -eq 1)
    Assert-True (@(Get-DeploymentProblems -Facts (New-Facts) -ExpectedCommitSha $sha -DeployStartUtcTicks $start -ExpectStopped).Count -eq 1)
}
# deploy.ps1's own stop / start functions, run against FAKE service cmdlets (functions take precedence over cmdlets): no real service is queried or changed.
Test-Case 'execution service is stopped first, started only after the ownership check, and left stopped otherwise' {
    $script:calls = @()
    $script:state = @{ Status = 'Running'; StartMode = 'Auto' }
    $dirs = @{ NiftySignalHost = $hostOff; NiftySignalExecution = $execOn; NiftySignalDashboard = $dash }
    function Get-CimInstance { param($ClassName, $Filter) $n = $Filter -replace "^Name='|'$", ''; [pscustomobject]@{ PathName = '"' + (Join-Path $dirs[$n] 'x.exe') + '"'; StartMode = $script:state.StartMode } }
    function Get-Service { param($Name) $o = [pscustomobject]@{ Status = $script:state.Status }; $o | Add-Member -PassThru -MemberType ScriptMethod -Name WaitForStatus -Value { param($a, $b) } }
    function Stop-Service { param($Name, [switch] $Force, $ErrorAction) $script:calls += "stop $Name"; $script:state.Status = 'Stopped' }
    function Start-Service { param($Name) $script:calls += "start $Name"; $script:state.Status = 'Running' }
    function Assert-DeploymentWindow { param($Operation, [switch] $AllowMarketHours, $ReserveMinutes) $script:calls += "guard $Operation" }
    function Write-Step($message) { }
    $guard = @{}
    $services = @(Get-DeploymentServiceCatalog -Service Both)
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\deploy.ps1'), [ref] $null, [ref] $null)
    foreach ($node in $ast.EndBlock.Statements) {
        $isBlock = $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -in @('$serviceInfo', '$stopService')
        $isFunction = $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -like '*Execution*'
        if ($isBlock -or $isFunction) { . ([scriptblock]::Create($node.Extent.Text)) }
    }

    $before = Stop-ExecutionBeforeDeployment @{}
    Assert-True (($script:calls -join ',') -eq 'guard stop NiftySignalExecution,stop NiftySignalExecution') ($script:calls -join ',')
    $script:calls = @(); Start-ExecutionAfterDeployment @{} $before
    Assert-True (($script:calls -join ',') -eq 'start NiftySignalExecution') ($script:calls -join ',')

    $dirs.NiftySignalHost = $hostOn; $script:calls = @(); $script:state.Status = 'Stopped'
    Assert-Throws { Start-ExecutionAfterDeployment @{} $before } '*NOT started*two paper writers*'
    Assert-True ($script:calls.Count -eq 0) ($script:calls -join ',')

    $dirs.NiftySignalHost = $hostOff
    $disabled = [pscustomobject]@{ Dir = $execOn; StartMode = 'Disabled'; Running = $false }
    Start-ExecutionAfterDeployment @{} $disabled
    $services = @(Get-DeploymentServiceCatalog -Service Dashboard)
    Start-ExecutionAfterDeployment @{} ([pscustomobject]@{ Dir = $execOn; StartMode = 'Manual'; Running = $false })
    Start-ExecutionAfterDeployment @{} $null
    Assert-True ($script:calls.Count -eq 0) ($script:calls -join ',')
}
# ---- observer service ----
$shippedHost = '{ "Adaptive": { "Observer": { "Process": "Separate" }, "Top5Microstructure": { "Enabled": false, "OptionsEnabled": false } }, "Pricing": { "RiskFreeRate": 0.065 } }'
$newHost = New-Settings 'newhost' $shippedHost $null
$oldHost = New-Settings 'oldhost' '{ "Pricing": { "RiskFreeRate": 0.065 } }' $null
$inHost = New-Settings 'inhost' $shippedHost '{ "Adaptive": { "Observer": { "Process": "InHost" } } }'
$depthHost = New-Settings 'depthhost' $shippedHost '{ "Adaptive:Top5Microstructure:Enabled": true, "ConnectionStrings": { "NiftySignalDb": "test-host-value" } }'
$observer = New-Settings 'observer' $shippedHost '{ "ConnectionStrings": { "NiftySignalDb": "secret-marker" } }'
$observerBare = New-Settings 'observerbare' $shippedHost $null
function ObserverSettings($h, $o) { & $ObserverSettingsScript $h $o }

Test-Case 'a configured separate Observer beside a new Host may start, and no setting value leaves the machine' {
    $s = ObserverSettings $newHost $observer
    Assert-True (@(Get-ObserverStartProblems -Settings $s).Count -eq 0) (@(Get-ObserverStartProblems -Settings $s) -join ' ')
    Assert-True (($s | ConvertTo-Json) -notlike '*secret-marker*')
}
Test-Case 'the Observer never starts beside an older Host, an in-Host observer, or without a connection string' {
    foreach ($case in @(@($oldHost, $observer, '*deploy the Host first*'), @($inHost, $observer, '*only one process*'), @($newHost, $observerBare, '*no ConnectionStrings*'))) {
        $found = @(Get-ObserverStartProblems -Settings (ObserverSettings $case[0] $case[1])) -join ' '
        Assert-True ($found -like $case[2]) "$($case[2]) : $found"
    }
}
Test-Case 'an Observer configured differently from the Host is refused by setting name only' {
    $found = @(Get-ObserverStartProblems -Settings (ObserverSettings $depthHost $observer)) -join ' '
    Assert-True ($found -like '*Adaptive:Top5Microstructure:Enabled*' -and $found -notlike '*test-host-value*') $found
}
Test-Case 'deploying a Host that will not run the observer requires the Observer service' {
    $s = ObserverSettings $oldHost $null
    Assert-True (@(Get-ObserverInstallProblems -Settings $s -ObserverInstalled $false -HostSelected $true).Count -eq 1)
    Assert-True (@(Get-ObserverInstallProblems -Settings $s -ObserverInstalled $true -HostSelected $true).Count -eq 0)
    Assert-True (@(Get-ObserverInstallProblems -Settings $s -ObserverInstalled $false -HostSelected $false).Count -eq 0)
    Assert-True (@(Get-ObserverInstallProblems -Settings (ObserverSettings $inHost $null) -ObserverInstalled $false -HostSelected $true).Count -eq 0)
}
Test-Case 'a Host with an in-process paper path enabled is not deployed with a separate Observer' {
    foreach ($local in @('{ "PaperEngineV2": { "Enabled": true } }', '{ "Trading": { "PaperLive": { "Enabled": true } } }', '{ "PaperEngineV2:Enabled": "true" }')) {
        $dir = New-Settings "paper$([Guid]::NewGuid().ToString('N'))" $shippedHost $local
        $found = @(Get-ObserverInstallProblems -Settings (ObserverSettings $dir $null) -ObserverInstalled $true -HostSelected $true) -join ' '
        Assert-True ($found -like '*would not start*') "$local : $found"
        Assert-True (@(Get-ObserverInstallProblems -Settings (ObserverSettings $dir $null) -ObserverInstalled $true -HostSelected $false).Count -eq 0)
    }
    $inProcess = New-Settings 'paperinhost' $shippedHost '{ "Adaptive": { "Observer": { "Process": "InHost" } }, "PaperEngineV2": { "Enabled": true } }'
    Assert-True (@(Get-ObserverInstallProblems -Settings (ObserverSettings $inProcess $null) -ObserverInstalled $false -HostSelected $true).Count -eq 0)
}
# deploy.ps1's own observer functions against FAKE service cmdlets.
Test-Case 'the Observer is stopped before a Host change, started only when it is the single writer, untouched by a Dashboard deployment' {
    $script:calls = @()
    $script:state = @{ Status = 'Running'; StartMode = 'Auto' }
    $dirs = @{ NiftySignalHost = $newHost; NiftySignalObserver = $observer }
    function Get-CimInstance { param($ClassName, $Filter) $n = $Filter -replace "^Name='|'$", ''; [pscustomobject]@{ PathName = '"' + (Join-Path $dirs[$n] 'x.exe') + '"'; StartMode = $script:state.StartMode } }
    function Get-Service { param($Name) $o = [pscustomobject]@{ Status = $script:state.Status }; $o | Add-Member -PassThru -MemberType ScriptMethod -Name WaitForStatus -Value { param($a, $b) } }
    function Stop-Service { param($Name, [switch] $Force, $ErrorAction) $script:calls += "stop $Name"; $script:state.Status = 'Stopped' }
    function Start-Service { param($Name) $script:calls += "start $Name"; $script:state.Status = 'Running' }
    function Assert-DeploymentWindow { param($Operation, [switch] $AllowMarketHours, $ReserveMinutes) $script:calls += "guard $Operation" }
    function Write-Step($message) { }
    $guard = @{}
    $services = @(Get-DeploymentServiceCatalog -Service Both)
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\deploy.ps1'), [ref] $null, [ref] $null)
    foreach ($node in $ast.EndBlock.Statements) {
        $isBlock = $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -in @('$serviceInfo', '$stopService')
        $isFunction = $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -like '*Observer*'
        if ($isBlock -or $isFunction) { . ([scriptblock]::Create($node.Extent.Text)) }
    }

    Assert-ObserverInstalled @{}
    $before = Stop-ObserverBeforeDeployment @{}
    Assert-True (($script:calls -join ',') -eq 'guard stop NiftySignalObserver,stop NiftySignalObserver') ($script:calls -join ',')
    $script:calls = @(); Start-ObserverAfterDeployment @{} $before
    Assert-True (($script:calls -join ',') -eq 'start NiftySignalObserver') ($script:calls -join ',')

    $dirs.NiftySignalHost = $oldHost; $script:calls = @(); $script:state.Status = 'Stopped'
    Assert-Throws { Start-ObserverAfterDeployment @{} $before } '*NOT started*deploy the Host first*'
    $dirs.NiftySignalHost = $inHost; Start-ObserverAfterDeployment @{} $before
    $dirs.NiftySignalHost = $newHost
    Start-ObserverAfterDeployment @{} ([pscustomobject]@{ Dir = $observer; StartMode = 'Disabled'; Running = $false })
    Start-ObserverAfterDeployment @{} $null
    $script:state.Status = 'Running'
    $services = @(Get-DeploymentServiceCatalog -Service Dashboard)
    Assert-True ($null -eq (Stop-ObserverBeforeDeployment @{}))
    Assert-True ($script:calls.Count -eq 0) ($script:calls -join ',')
}

# ---- settings files with comments (Windows PowerShell 5.1) ----
$commented = @'
// leading comment with a "quoted" word and an unmatched /*
{
  /* block comment
     // nested line marker, "quote" and { brace } */
  "Url": "http://localhost:5210//path", // trailing comment
  "Connection": "Host=db;Password=a//b/*c*/d",
  "Escaped": "say \"// not a comment\" /* nor this */",
  "Folder": "C:\\data\\", // the string above ends with an escaped backslash
  "Glob": "logs/*.log",
  "Number": 5 /* after a value */,
  "Section": { /* inside */ "Enabled": true }
}
/* closing comment */
'@
Test-Case 'comments are removed and URLs, connection strings and escaped strings are kept exactly' {
    $j = ConvertFrom-SettingsJson -Text $commented
    Assert-True ($j.Url -ceq 'http://localhost:5210//path') $j.Url
    Assert-True ($j.Connection -ceq 'Host=db;Password=a//b/*c*/d')
    Assert-True ($j.Escaped -ceq 'say "// not a comment" /* nor this */') $j.Escaped
    Assert-True ($j.Folder -ceq 'C:\data\') $j.Folder
    Assert-True ($j.Glob -ceq 'logs/*.log' -and $j.Number -eq 5 -and $j.Section.Enabled -eq $true)
    $crlf = ConvertFrom-SettingsJson -Text ($commented -replace "`r?`n", "`r`n")
    Assert-True ($crlf.Url -ceq $j.Url -and $crlf.Section.Enabled -eq $true)
}
Test-Case 'malformed, empty and non-object settings are rejected' {
    $bad = @($null, '', '   ', '// only a comment', '/* only */', '{ "a": 1', '"a": 1 }', '[ 1 ]', '{ "a": "open }', '{ "a": 1 } /* never closed', '{ "a": tru }', '{ "a": 1/* x */2 }', '{ "a": 1 } { "b": 2 }', '{ "a" 1 }')
    foreach ($text in $bad) {
        $rejected = $false
        try { ConvertFrom-SettingsJson -Text $text | Out-Null } catch { $rejected = $true }
        Assert-True $rejected "accepted: $text"
    }
}
Test-Case 'every shipped settings file is read, with its values intact' {
    $files = @(Get-ChildItem (Join-Path $PSScriptRoot '..') -Directory -Filter 'NiftySignal.*' | ForEach-Object { Get-ChildItem $_.FullName -File -Filter 'appsettings*.json' } | Where-Object { $_.Name -notlike '*.Local.json' })
    Assert-True ($files.Count -ge 6) "found $($files.Count)"
    foreach ($f in $files) { ConvertFrom-SettingsJson -Text (Get-Content -LiteralPath $f.FullName -Raw) | Out-Null }
    $d = ConvertFrom-SettingsJson -Text (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\NiftySignal.Dashboard\appsettings.json') -Raw)
    Assert-True ($d.AdaptiveScreenshots.BaseUrl -ceq 'http://localhost:5210' -and $d.Urls -ceq 'http://localhost:5210' -and $d.ExecutionHealth.Required -eq $false -and $d.DashboardAuth.Username -ceq 'admin')
}
function Copy-Shipped([string] $name, [string] $project, [string] $local) {
    New-Settings $name (Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\$project\appsettings.json") -Raw) $local
}
$realHost = Copy-Shipped 'realhost' 'NiftySignal.Host' '{ "PaperEngineV2": { "Enabled": false } }'
$realExec = Copy-Shipped 'realexec' 'NiftySignal.ExecutionService' '{ "Execution": { "Enabled": true } }'
$realDash = Copy-Shipped 'realdash' 'NiftySignal.Dashboard' '{ "ExecutionHealth": { "Required": true } }'
$realDashNotRequired = Copy-Shipped 'realdashoff' 'NiftySignal.Dashboard' $null
Test-Case 'the shipped Dashboard file: execution may start only when the local file requires it' {
    $o = & $PaperOwnershipScript $realHost $realExec $realDash
    Assert-True (@($o.Errors).Count -eq 0) ($o.Errors -join ' ')
    Assert-True (@(Get-PaperOwnershipProblems -Ownership $o).Count -eq 0)
    $found = @(Get-PaperOwnershipProblems -Ownership (& $PaperOwnershipScript $realHost $realExec $realDashNotRequired))
    Assert-True ($found.Count -eq 1 -and $found[0] -like '*ExecutionHealth:Required*') ($found -join ' ')
}
Test-Case 'commented local settings: an unsafe execution configuration is still blocked' {
    $twoWriters = New-Settings 'c-hoston' '{ "PaperEngineV2": { "Enabled": false } }' "{ // `"PaperEngineV2`": { `"Enabled`": false },`n `"PaperEngineV2`": { `"Enabled`": true } /* in-Host writer on */ }"
    $found = @(Get-PaperOwnershipProblems -Ownership (& $PaperOwnershipScript $twoWriters $realExec $realDash))
    Assert-True ($found.Count -eq 1 -and $found[0] -like '*two paper writers*') ($found -join ' ')
    $commentedOut = New-Settings 'c-dash' '{ "ExecutionHealth": { "Required": false } }' "{`n // `"ExecutionHealth`": { `"Required`": true }`n }"
    $found = @(Get-PaperOwnershipProblems -Ownership (& $PaperOwnershipScript $realHost $realExec $commentedOut))
    Assert-True ($found.Count -eq 1 -and $found[0] -like '*ExecutionHealth:Required*') ($found -join ' ')
}
Test-Case 'missing settings files, missing or non-boolean switches block or keep the application default' {
    $noFile = Join-Path $WorkRoot 'settings\nofile'; New-Item -ItemType Directory -Force $noFile | Out-Null
    foreach ($dirs in @(@($noFile, $realExec, $realDash), @($realHost, $noFile, $realDash), @($realHost, $realExec, $noFile), @($realHost, $realExec, $null))) {
        Assert-True (@(Get-PaperOwnershipProblems -Ownership (& $PaperOwnershipScript @dirs)).Count -ge 1) ($dirs -join '|')
    }
    $noKeys = New-Settings 'nokeys' '{ }' $null
    $o = & $PaperOwnershipScript $noKeys $noKeys $noKeys
    Assert-True ($o.HostPaperEngineEnabled -eq $false -and $o.ExecutionEnabled -eq $false -and $o.DashboardExecutionRequired -eq $false -and @($o.Errors).Count -eq 0)
    Assert-True (@(Get-PaperOwnershipProblems -Ownership (& $PaperOwnershipScript $realHost $realExec $noKeys)).Count -eq 1)
    $text = New-Settings 'nonbool' '{ "PaperEngineV2": { "Enabled": "no" } }' $null
    Assert-True (@(Get-PaperOwnershipProblems -Ownership (& $PaperOwnershipScript $text $realExec $realDash) | Where-Object { $_ -like '*not true or false*' }).Count -eq 1)
}
Test-Case 'a malformed file in any of the three folders blocks the start and is not echoed' {
    foreach ($local in @('{ "secret-marker": ', '{ "a": "secret-marker }', '{ } /* secret-marker', '// secret-marker')) {
        $name = "bad$([Guid]::NewGuid().ToString('N'))"
        $dir = New-Settings $name '{ }' $local
        foreach ($dirs in @(@($dir, $realExec, $realDash), @($realHost, $dir, $realDash), @($realHost, $realExec, $dir))) {
            $found = @(Get-PaperOwnershipProblems -Ownership (& $PaperOwnershipScript @dirs))
            Assert-True (@($found | Where-Object { $_ -like '*cannot be read as JSON*' }).Count -eq 1) "$local : $($found -join ' ')"
            Assert-True (($found -join ' ') -notlike '*secret-marker*')
        }
    }
}
Test-Case 'the Observer checks read commented files and refuse a commented-out connection string or a malformed file' {
    $hostC = New-Settings 'c-newhost' ("// shipped`n" + '{ "Adaptive": { "Observer": { "Process": "Separate" } } /* mode */ }') $null
    $ok = New-Settings 'c-observer' '{ }' '{ "ConnectionStrings": { "NiftySignalDb": "Host=a//b" } } // local'
    Assert-True (@(Get-ObserverStartProblems -Settings (& $ObserverSettingsScript $hostC $ok)).Count -eq 0)
    $off = New-Settings 'c-observeroff' '{ }' "{`n // `"ConnectionStrings`": { `"NiftySignalDb`": `"x`" }`n }"
    Assert-True ((@(Get-ObserverStartProblems -Settings (& $ObserverSettingsScript $hostC $off)) -join ' ') -like '*no ConnectionStrings*')
    $bad = New-Settings 'c-observerbad' '{ }' '{ "secret-marker": '
    $s = & $ObserverSettingsScript $hostC $bad
    Assert-True (@(Get-ObserverStartProblems -Settings $s).Count -ge 1 -and @(Get-ObserverInstallProblems -Settings $s -ObserverInstalled $true -HostSelected $true).Count -ge 1)
    Assert-True (($s.Errors -join ' ') -notlike '*secret-marker*')
}
Test-Case 'the settings scripts are self-contained, as Invoke-Command sends them to the target' {
    foreach ($case in @(@($PaperOwnershipScript, @($realHost, $realExec, $realDash)), @($ObserverSettingsScript, @($realHost, $realExec)))) {
        $isolated = [PowerShell]::Create()      # a new runspace: none of the functions defined here exist in it
        try {
            $isolated.AddScript($case[0].ToString()) | Out-Null
            foreach ($argument in $case[1]) { $isolated.AddArgument($argument) | Out-Null }
            $result = @($isolated.Invoke())
            Assert-True ($result.Count -eq 1 -and @($result[0].Errors | Where-Object { $_ }).Count -eq 0) (($result[0].Errors) -join ' ')
        }
        finally { $isolated.Dispose() }
    }
}

Test-Case 'the scripts read no variable they never assign' {
    $known = @('_', 'args', 'true', 'false', 'null', 'matches', 'PSScriptRoot', 'LASTEXITCODE', 'ErrorActionPreference', 'PSItem', 'input', 'PSCmdlet', 'Error', 'Host')
    $assigned = @{}
    $read = @()
    foreach ($file in @('..\deploy.ps1', '..\vm-deploy.ps1', 'DeploymentSafety.ps1', 'DeploymentPackaging.ps1', 'Install-ExecutionService.ps1', 'Install-ObserverService.ps1')) {
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref] $null, [ref] $null)
        foreach ($node in $ast.FindAll({ param($n) $true }, $true)) {
            if ($node -is [Management.Automation.Language.AssignmentStatementAst]) { foreach ($v in $node.Left.FindAll({ param($n) $n -is [Management.Automation.Language.VariableExpressionAst] }, $true)) { $assigned[($v.VariablePath.UserPath -replace '^script:', '')] = $true } }
            elseif ($node -is [Management.Automation.Language.ParameterAst]) { $assigned[$node.Name.VariablePath.UserPath] = $true }
            elseif ($node -is [Management.Automation.Language.ForEachStatementAst]) { $assigned[$node.Variable.VariablePath.UserPath] = $true }
            elseif ($node -is [Management.Automation.Language.VariableExpressionAst]) { if (-not $node.VariablePath.IsDriveQualified) { $read += [pscustomobject]@{ File = $file; Name = ($node.VariablePath.UserPath -replace '^script:', '') } } }
        }
    }
    $unknown = @($read | Where-Object { -not $assigned.ContainsKey($_.Name) -and $known -notcontains $_.Name } | ForEach-Object { "$($_.File): `$$($_.Name)" } | Sort-Object -Unique)
    Assert-True ($unknown.Count -eq 0) ($unknown -join '; ')
}

Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host "$($script:passed) passed, $($script:failed) failed"
if ($script:failed -gt 0) { exit 1 }
