# Shared safety functions for deploy.ps1 and vm-deploy.ps1 (market-hours guard, install backup, post-deploy verification).
# Everything here is free of VM / production access when dot-sourced, so tools/Test-DeploymentSafety.ps1 can exercise it locally.
# Compatible with Windows PowerShell 5.1.

# Guard window: weekdays [09:00, 15:45) IST. Exchange holidays are not known to the scripts, so a weekday holiday is guarded too.
$script:GuardStartMinute = 9 * 60
$script:GuardEndMinute = 15 * 60 + 45
$script:EmergencyPhrase = 'EMERGENCY DEPLOY'
$script:EmergencyConfirmed = $false

function Get-DeploymentWindowVerdict {
    <# Pure decision: may a disruptive operation start at $UtcNow when it needs $ReserveMinutes for startup, validation and rollback? #>
    param(
        [Parameter(Mandatory = $true)][DateTime] $UtcNow,
        [int] $ReserveMinutes = 30
    )
    # IST has no daylight saving time, so a fixed offset is exact and needs no time zone database.
    $ist = [DateTime]::SpecifyKind($UtcNow.ToUniversalTime(), [DateTimeKind]::Unspecified).AddMinutes(330)
    $weekend = $ist.DayOfWeek -eq [DayOfWeek]::Saturday -or $ist.DayOfWeek -eq [DayOfWeek]::Sunday
    $minute = $ist.TimeOfDay.TotalMinutes
    $stamp = '{0:yyyy-MM-dd HH:mm} IST ({1})' -f $ist, $ist.DayOfWeek
    $reason = $null
    if (-not $weekend) {
        if ($minute -ge $script:GuardStartMinute -and $minute -lt $script:GuardEndMinute) {
            $reason = "market hours (weekdays 09:00-15:45 IST); now $stamp"
        }
        elseif ($minute -lt $script:GuardStartMinute -and ($minute + $ReserveMinutes) -gt $script:GuardStartMinute) {
            $reason = "less than $ReserveMinutes minutes remain before the 09:00 IST guard for startup, validation and rollback; now $stamp"
        }
    }
    [pscustomobject]@{ Allowed = ($null -eq $reason); Reason = $reason; Ist = $ist }
}

function Assert-DeploymentWindow {
    <#
      Throws unless a disruptive operation may start now. Call it IMMEDIATELY before the operation, not only when the script starts.
      -AllowMarketHours never bypasses the guard by itself: the owner must also type the emergency phrase (once per script run), after assessing open-position risk.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Operation,
        [switch] $AllowMarketHours,
        [int] $ReserveMinutes = 30,
        [Nullable[DateTime]] $UtcNow
    )
    $now = if ($null -ne $UtcNow) { [DateTime] $UtcNow } else { [DateTime]::UtcNow }
    $verdict = Get-DeploymentWindowVerdict -UtcNow $now -ReserveMinutes $ReserveMinutes
    if ($verdict.Allowed) { return }
    if (-not $AllowMarketHours) {
        throw "Deployment guard blocked '$Operation': $($verdict.Reason). Nothing further was changed."
    }
    if ($script:EmergencyConfirmed) { return }
    Write-Host ''
    Write-Host "EMERGENCY OVERRIDE requested for '$Operation': $($verdict.Reason)." -ForegroundColor Red
    Write-Host 'A restart stops paper position management and the observer until recovery completes. Check open positions and pending orders first.' -ForegroundColor Yellow
    # Only an interactive answer counts: there is deliberately no parameter that could supply it.
    $answer = Read-Host "Type '$($script:EmergencyPhrase)' to confirm you assessed the open-position risk"
    if ($answer -cne $script:EmergencyPhrase) {
        throw "Deployment guard blocked '$Operation': the emergency override was not confirmed. Nothing further was changed."
    }
    $script:EmergencyConfirmed = $true
}

# Runs on the target machine (Invoke-Command -ScriptBlock) with SCALAR arguments, one service per call: passing arrays of directories through
# -ArgumentList is what handed both install folders to a single robocopy call (exit code 16, 26 Nov 2026).
$InstallBackupScript = {
    param(
        [string] $SourceDir,
        [string] $ServiceName,
        [string] $Stamp
    )
    $ErrorActionPreference = 'Stop'
    # Runtime output, not part of the installed application. appsettings.Local.json IS backed up (it never leaves the VM).
    $excluded = @('logs', 'screenshots', '.playwright-browsers', 'agent-feed')
    if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) { throw "Backup source '$SourceDir' is not a directory." }
    $source = (Get-Item -LiteralPath $SourceDir).FullName.TrimEnd('\')
    $root = Join-Path (Split-Path $source -Parent) '_backups'
    $final = Join-Path $root ("{0}-{1}" -f $ServiceName, $Stamp)
    $partial = "$final.partial"
    if ((Test-Path -LiteralPath $final) -or (Test-Path -LiteralPath $partial)) { throw "Backup destination '$final' already exists; nothing was overwritten." }
    New-Item -ItemType Directory -Path $partial -Force | Out-Null

    $excludedPaths = @($excluded | ForEach-Object { Join-Path $source $_ })
    $output = & robocopy $source $partial /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /XD $excludedPaths
    $code = $LASTEXITCODE
    # robocopy: 0-7 are success variants (copied / extra / mismatched bits), 8 and above mean at least one failure.
    if ($code -ge 8) { throw "Backup of $source failed (robocopy exit $code). $($output -join ' ')" }

    # Integrity: every binary and settings file of the source must exist in the backup with the same SHA-256.
    $checked = 0
    $files = Get-ChildItem -LiteralPath $source -Recurse -File -Force | Where-Object {
        $relative = $_.FullName.Substring($source.Length + 1)
        ($excluded -notcontains $relative.Split('\')[0]) -and ($_.Name -match '\.(dll|exe)$' -or $_.Name -like 'appsettings*.json' -or $_.Name -like '*.deps.json' -or $_.Name -like '*.runtimeconfig.json')
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($source.Length + 1)
        $copy = Join-Path $partial $relative
        if (-not (Test-Path -LiteralPath $copy)) { throw "Backup of $source is incomplete: '$relative' is missing." }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) {
            throw "Backup of $source is corrupt: '$relative' differs from the source."
        }
        $checked++
    }
    if (@($files | Where-Object { $_.Name -match '\.dll$' }).Count -eq 0) { throw "Backup of $source holds no binaries; refusing to treat it as a rollback copy." }

    Set-Content -LiteralPath (Join-Path $partial '.backup-verified') -Value ("source={0}`r`nfilesVerified={1}`r`nrobocopyExit={2}" -f $source, $checked, $code)
    Rename-Item -LiteralPath $partial -NewName (Split-Path $final -Leaf)
    [pscustomobject]@{ Service = $ServiceName; Source = $source; Destination = $final; FilesVerified = $checked; RobocopyExit = $code; LocalSettings = (Test-Path -LiteralPath (Join-Path $final 'appsettings.Local.json')) }
}

function Test-FirstInstall {
    <#
      True only for a service new in this release that is registered but has never received binaries (its main DLL is absent from the snapshot):
      there is nothing to roll back to, so no backup is taken. The Host and the Dashboard always need a verified backup.
    #>
    param([Parameter(Mandatory = $true)] $Snapshot)
    (@('NiftySignalObserver', 'NiftySignalExecution') -contains $Snapshot.Name) -and ($null -eq $Snapshot.DllWritten)
}

# Runs on the target machine, only after EVERY requested backup succeeded. Prunes only verified backups of one service and keeps the newest $Keep;
# unverified folders (older script versions, failed attempts) are never counted and never deleted.
$PruneBackupScript = {
    param(
        [string] $BackupRoot,
        [string] $ServiceName,
        [int] $Keep = 3
    )
    $ErrorActionPreference = 'Stop'
    if ($Keep -lt 1) { throw 'Keep must be at least 1.' }
    Get-ChildItem -LiteralPath $BackupRoot -Directory -Filter "$ServiceName-*" |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName '.backup-verified') } |
        Sort-Object Name -Descending | Select-Object -Skip $Keep |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force; $_.FullName }
}

function Get-DeploymentServiceCatalog {
    <#
      The services of one deployment, in deployment order: the Host (ingestion), the Observer (migrates the observer database and publishes bars), the
      paper execution service that reads them, then the Dashboard. 'Both' includes the Observer and the execution service as Optional: each is skipped
      where it is not installed (the Observer only where the Host runs it in-process: Get-ObserverInstallProblems).
    #>
    param([Parameter(Mandatory = $true)][ValidateSet('Host', 'Dashboard', 'Observer', 'Execution', 'Both')][string] $Service)
    $all = @(
        [pscustomobject]@{ Key = 'Host'; Name = 'NiftySignalHost'; Project = 'NiftySignal.Host'; Dll = 'NiftySignal.Host.dll'; Optional = $false; LogsBuildIdentity = $true }
        [pscustomobject]@{ Key = 'Observer'; Name = 'NiftySignalObserver'; Project = 'NiftySignal.Observer'; Dll = 'NiftySignal.Observer.dll'; Optional = ($Service -eq 'Both'); LogsBuildIdentity = $true }
        [pscustomobject]@{ Key = 'Execution'; Name = 'NiftySignalExecution'; Project = 'NiftySignal.ExecutionService'; Dll = 'NiftySignal.ExecutionService.dll'; Optional = ($Service -eq 'Both'); LogsBuildIdentity = $true }
        [pscustomobject]@{ Key = 'Dashboard'; Name = 'NiftySignalDashboard'; Project = 'NiftySignal.Dashboard'; Dll = 'NiftySignal.Dashboard.dll'; Optional = $false; LogsBuildIdentity = $false }
    )
    $all | Where-Object { $Service -eq 'Both' -or $_.Key -eq $Service }
}

# ---- settings files ----
# The applications read appsettings*.json with // and /* */ comments; ConvertFrom-Json in Windows PowerShell 5.1 rejects them.
function ConvertFrom-SettingsJson {
    <#
      Parses one settings file: comments are removed by reading the text character by character, so that // and /* inside a string (URLs, connection
      strings, escaped quotes) are kept. Everything else is left to ConvertFrom-Json. Throws on an unterminated string or comment, on an empty file and
      on anything that is not one JSON object. Callers must not repeat the exception text: it can quote file content.
    #>
    param([AllowNull()][AllowEmptyString()][string] $Text)
    $length = $Text.Length
    $clean = New-Object System.Text.StringBuilder
    $inString = $false
    $i = 0
    while ($i -lt $length) {
        $c = [string] $Text[$i]
        $next = if ($i + 1 -lt $length) { [string] $Text[$i + 1] } else { '' }
        if ($inString) {
            [void] $clean.Append($c)
            if ($c -ceq '\') { [void] $clean.Append($next); $i += 2; continue }
            if ($c -ceq '"') { $inString = $false }
            $i++
        }
        elseif ($c -ceq '/' -and $next -ceq '/') {
            while ($i -lt $length -and $Text[$i] -ne [char] 10 -and $Text[$i] -ne [char] 13) { $i++ }
            [void] $clean.Append(' ')
        }
        elseif ($c -ceq '/' -and $next -ceq '*') {
            $end = $Text.IndexOf('*/', $i + 2, [StringComparison]::Ordinal)
            if ($end -lt 0) { throw 'unterminated comment' }
            [void] $clean.Append(' ')      # a comment separates tokens; it never joins them
            $i = $end + 2
        }
        else {
            if ($c -ceq '"') { $inString = $true }
            [void] $clean.Append($c)
            $i++
        }
    }
    if ($inString) { throw 'unterminated string' }
    $json = $clean.ToString().Trim()
    if (-not ($json.StartsWith('{') -and $json.EndsWith('}'))) { throw 'not one JSON object' }
    ConvertFrom-Json -InputObject $json -ErrorAction Stop
}

function New-SettingsScript {
    <# A script block that runs on the target machine sees none of this file's functions: the reader's source is placed inside it. #>
    param([Parameter(Mandatory = $true)][scriptblock] $Template)
    $marker = '# <settings-reader>'
    $text = $Template.ToString()
    if (-not $text.Contains($marker)) { throw 'settings script without a reader marker' }
    [scriptblock]::Create($text.Replace($marker, "function ConvertFrom-SettingsJson {`n$(${function:ConvertFrom-SettingsJson})`n}"))
}

# Runs on the target machine. Reads ONLY the switches that decide who writes paper state; no other setting (and no secret) leaves the machine.
# appsettings.Local.json overrides appsettings.json, as in the applications. Environment-variable overrides are not visible here.
$PaperOwnershipScript = New-SettingsScript {
    param(
        [string] $HostDir,
        [string] $ExecutionDir,
        [string] $DashboardDir
    )
    # <settings-reader>
    $problems = @()
    function Read-Switch([string] $dir, [string] $section, [string] $key) {
        if (-not $dir) { return $null }
        $value = $false
        foreach ($name in @('appsettings.json', 'appsettings.Local.json')) {
            $path = Join-Path $dir $name
            if (-not (Test-Path -LiteralPath $path)) {
                if ($name -eq 'appsettings.json') { throw "'$path' is missing" }
                continue
            }
            try { $json = ConvertFrom-SettingsJson -Text (Get-Content -LiteralPath $path -Raw) }
            catch { throw "'$path' cannot be read as JSON" }      # the parser's own message can quote file content, so it is not repeated
            $found = $null
            if ($json.PSObject.Properties[$section]) { $found = $json.$section.PSObject.Properties[$key] }
            $flat = $json.PSObject.Properties["${section}:$key"]
            if ($flat) { $found = $flat }
            if ($found) {
                $parsed = $false
                if (-not [bool]::TryParse([string] $found.Value, [ref] $parsed)) { throw "${section}:$key in '$path' is not true or false" }
                $value = $parsed
            }
        }
        $value
    }
    $hostWriter = $null; $execution = $null; $required = $null
    try { $hostWriter = Read-Switch $HostDir 'PaperEngineV2' 'Enabled' } catch { $problems += "Host settings: $($_.Exception.Message)" }
    try { $execution = Read-Switch $ExecutionDir 'Execution' 'Enabled' } catch { $problems += "Execution settings: $($_.Exception.Message)" }
    try { $required = Read-Switch $DashboardDir 'ExecutionHealth' 'Required' } catch { $problems += "Dashboard settings: $($_.Exception.Message)" }
    [pscustomobject]@{ HostPaperEngineEnabled = $hostWriter; ExecutionEnabled = $execution; DashboardExecutionRequired = $required; Errors = @($problems) }
}

function Get-PaperOwnershipProblems {
    <# Pure verdict: a non-empty result means the execution service must not be started. Unknown is never treated as safe. #>
    param([Parameter(Mandatory = $true)] $Ownership)
    foreach ($e in @($Ownership.Errors)) { if ($e) { $e } }
    if ($Ownership.ExecutionEnabled -eq $true -and $Ownership.HostPaperEngineEnabled -ne $false) {
        'Execution:Enabled=true while the Host does not have PaperEngineV2:Enabled=false: two paper writers would be configured. Set the Host to false in its appsettings.Local.json.'
    }
    if ($Ownership.ExecutionEnabled -eq $true -and $Ownership.DashboardExecutionRequired -ne $true) {
        'Execution:Enabled=true while the Dashboard does not have ExecutionHealth:Required=true: a service that never reports would raise no alert. Set it in the Dashboard appsettings.Local.json.'
    }
}

# Runs on the target machine. Reads the settings that decide which process writes the observer database, and whether the Observer is configured like
# the Host it replaces. Only the process mode, yes/no facts and the NAMES of differing settings leave the machine.
$ObserverSettingsScript = New-SettingsScript {
    param(
        [string] $HostDir,
        [string] $ObserverDir
    )
    # <settings-reader>
    $problems = @()
    function Read-Setting([string] $dir, [string[]] $path, [string[]] $files) {
        $value = $null
        foreach ($name in $files) {
            $file = Join-Path $dir $name
            if (-not (Test-Path -LiteralPath $file)) { continue }
            try { $json = ConvertFrom-SettingsJson -Text (Get-Content -LiteralPath $file -Raw) }
            catch { throw "'$file' cannot be read as JSON" }
            $node = $json
            foreach ($part in $path) {
                if ($null -eq $node) { break }
                $property = $node.PSObject.Properties[$part]
                $node = if ($property) { $property.Value } else { $null }
            }
            if ($null -ne $node) { $value = $node }
            $flat = $json.PSObject.Properties[($path -join ':')]
            if ($flat) { $value = $flat.Value }
        }
        $value
    }
    $both = @('appsettings.json', 'appsettings.Local.json')
    $mode = @('Adaptive', 'Observer', 'Process')
    $knows = $false; $hostMode = $null; $observerMode = $null; $connection = $false; $different = @(); $inProcessPaper = @()
    try {
        if ($HostDir) {
            # a Host build without this key in its shipped settings runs the observer itself and takes no single-writer lock
            $knows = $null -ne (Read-Setting $HostDir $mode @('appsettings.json'))
            $hostMode = Read-Setting $HostDir $mode $both
            foreach ($setting in @('PaperEngineV2:Enabled', 'Trading:PaperLive:Enabled')) {
                if ([string] (Read-Setting $HostDir $setting.Split(':') $both) -eq 'true') { $inProcessPaper += $setting }
            }
        }
        if ($ObserverDir -and (Test-Path -LiteralPath (Join-Path $ObserverDir 'appsettings.json'))) {
            $observerMode = Read-Setting $ObserverDir $mode $both
            $connection = -not [string]::IsNullOrWhiteSpace([string] (Read-Setting $ObserverDir @('ConnectionStrings', 'NiftySignalDb') $both))
            if ($HostDir) {
                foreach ($setting in @('Adaptive:Top5Microstructure:Enabled', 'Adaptive:Top5Microstructure:OptionsEnabled', 'Pricing:RiskFreeRate', 'DashboardPush:HubUrl')) {
                    $parts = $setting.Split(':')
                    if ([string] (Read-Setting $HostDir $parts $both) -ne [string] (Read-Setting $ObserverDir $parts $both)) { $different += $setting }
                }
            }
        }
    }
    catch { $problems += $_.Exception.Message }
    [pscustomobject]@{
        HostBuildSeparatesObserver = $knows
        HostMode                   = if ($hostMode) { [string] $hostMode } else { 'Separate' }
        ObserverMode               = if ($observerMode) { [string] $observerMode } else { 'Separate' }
        ObserverHasConnectionString = $connection
        DifferentSettings          = @($different)
        HostInProcessPaperSettings = @($inProcessPaper)
        Errors                     = @($problems)
    }
}

function Get-ObserverInstallProblems {
    <# Before anything is stopped: a Host that will not run the observer needs a registered Observer service, or no observer runs at all. #>
    param([Parameter(Mandatory = $true)] $Settings, [bool] $ObserverInstalled, [bool] $HostSelected)
    foreach ($e in @($Settings.Errors)) { if ($e) { $e } }
    if ($HostSelected -and -not $ObserverInstalled -and $Settings.HostMode -ne 'InHost') {
        'NiftySignalObserver is not registered while the Host build will not run the observer: register it (tools\Install-ObserverService.ps1) or set Adaptive:Observer:Process=InHost in the Host appsettings.Local.json.'
    }
    # the new Host refuses to start in this combination, which would stop market-data ingestion
    if ($HostSelected -and $Settings.HostMode -ne 'InHost' -and @($Settings.HostInProcessPaperSettings).Count -gt 0) {
        "The Host has $(@($Settings.HostInProcessPaperSettings) -join ', ') = true while the Observer is Separate: the new Host would not start. Set it to false in the Host appsettings.Local.json, or set Adaptive:Observer:Process=InHost."
    }
}

function Get-ObserverStartProblems {
    <# Pure verdict for starting a separate Observer after the copy: a non-empty result means it must stay stopped. #>
    param([Parameter(Mandatory = $true)] $Settings)
    foreach ($e in @($Settings.Errors)) { if ($e) { $e } }
    if (-not $Settings.HostBuildSeparatesObserver) { 'The installed Host build runs the observer itself and takes no single-writer lock: deploy the Host first.' }
    if ($Settings.HostMode -ne 'Separate') { "The Host is configured with Adaptive:Observer:Process=$($Settings.HostMode): only one process may write the observer database." }
    if ($Settings.ObserverMode -ne 'Separate') { "The Observer is configured with Adaptive:Observer:Process=$($Settings.ObserverMode)." }
    if (-not $Settings.ObserverHasConnectionString) { 'The Observer has no ConnectionStrings:NiftySignalDb (set it in its appsettings.Local.json).' }
    if (@($Settings.DifferentSettings).Count -gt 0) { "The Observer settings differ from the Host's: $(@($Settings.DifferentSettings) -join ', '). Copy those values to the Observer appsettings.Local.json." }
}

function Get-StagedFileHashes {
    <# SHA-256 of every published file (relative path -> hash), excluding the local settings file that is never shipped. #>
    param([Parameter(Mandatory = $true)][string] $StagingDirectory)
    $root = (Get-Item -LiteralPath $StagingDirectory).FullName.TrimEnd('\')
    $hashes = @{}
    Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object { $_.Name -ne 'appsettings.Local.json' } | ForEach-Object {
        $hashes[$_.FullName.Substring($root.Length + 1)] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $hashes
}

# Runs on the target machine. Returns raw facts only; the verdict is Get-DeploymentProblems. All instants are UTC ticks so that no
# time zone or serialization rule of either machine can change a comparison.
$DeploymentFactsScript = {
    param(
        [string] $ServiceName,
        [string] $ProcessName,
        [string] $MainDll,
        [hashtable] $ExpectedHashes,
        [long] $DeployStartUtcTicks
    )
    $info = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $dir = if ($info) { Split-Path ($info.PathName.Trim('"')) -Parent } else { $null }
    $different = @()
    $version = $null
    if ($dir) {
        foreach ($relative in $ExpectedHashes.Keys) {
            $path = Join-Path $dir $relative
            if (-not (Test-Path -LiteralPath $path)) { $different += "$relative (missing)" }
            elseif ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $ExpectedHashes[$relative]) { $different += "$relative (differs)" }
        }
        $dll = Get-Item -LiteralPath (Join-Path $dir $MainDll) -ErrorAction SilentlyContinue
        if ($dll) { $version = $dll.VersionInfo.ProductVersion }
    }
    $processId = if ($info) { [int] $info.ProcessId } else { 0 }
    $process = if ($processId -gt 0) { Get-Process -Id $processId -ErrorAction SilentlyContinue } else { $null }
    $startTicks = if ($process) { $process.StartTime.ToUniversalTime().Ticks } else { $null }

    # Log lines written by THIS process start: Serilog file lines begin with a local timestamp carrying its offset.
    $identity = @()
    $fatal = @()
    $logFile = $null
    if ($dir) {
        $latest = Get-ChildItem (Join-Path $dir 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($latest) {
            $logFile = $latest.FullName
            $current = $false
            foreach ($line in (Get-Content -LiteralPath $latest.FullName -Tail 4000)) {
                if ($line -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(\.\d+)? [+-]\d\d:\d\d)') {
                    $parsed = [DateTimeOffset]::MinValue
                    if ([DateTimeOffset]::TryParse($matches[1], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $parsed)) { $current = $parsed.UtcTicks -ge $DeployStartUtcTicks }
                }
                if (-not $current) { continue }
                if ($line -match 'Build identity') { $identity += $line }
                if ($line -match '\[FTL\]|terminated unexpectedly|pending model changes|An error occurred using the connection to database|42501: permission denied') { $fatal += $line }
            }
        }
    }

    [pscustomobject]@{
        Service             = $ServiceName
        Status              = if ($info) { (Get-Service $ServiceName).Status.ToString() } else { 'NOT INSTALLED' }
        InstallDir          = $dir
        ProcessId           = $processId
        ProcessPath         = if ($process) { $process.Path } else { $null }
        ProcessStartUtcTicks = $startTicks
        ProductVersion      = $version
        CheckedFiles        = $ExpectedHashes.Count
        DifferentFiles      = @($different)
        LogFile             = $logFile
        BuildIdentityLines  = @($identity)
        FatalLines          = @($fatal | Select-Object -Last 10)
    }
}

function Get-DeploymentProblems {
    <# Pure verdict over the facts of one service. An empty result means verified; service status alone never is. #>
    param(
        [Parameter(Mandatory = $true)] $Facts,
        [Parameter(Mandatory = $true)][string] $ExpectedCommitSha,
        [Parameter(Mandatory = $true)][long] $DeployStartUtcTicks,
        # The Host and the execution service log their build identity at startup; a start without that line for the expected commit is not verified.
        [switch] $RequireBuildIdentityLog,
        # A service the owner disabled must stay stopped: only its installed files and build identity are verified.
        [switch] $ExpectStopped
    )
    if ($ExpectStopped) { if ($Facts.Status -ne 'Stopped') { "$($Facts.Service): disabled service is $($Facts.Status)" } }
    elseif ($Facts.Status -ne 'Running') { "$($Facts.Service): service is $($Facts.Status)" }
    if ($Facts.CheckedFiles -lt 1) { "$($Facts.Service): no published file hashes were compared" }
    if (@($Facts.DifferentFiles).Count -gt 0) { "$($Facts.Service): $(@($Facts.DifferentFiles).Count) installed file(s) differ from the published build, e.g. $(@($Facts.DifferentFiles | Select-Object -First 3) -join ', ')" }
    if (-not $Facts.ProductVersion -or -not $Facts.ProductVersion.ToLowerInvariant().Contains($ExpectedCommitSha.ToLowerInvariant())) { "$($Facts.Service): installed build identity '$($Facts.ProductVersion)' is not commit $ExpectedCommitSha" }
    if ($ExpectStopped) { return }
    if (-not $Facts.ProcessStartUtcTicks) { "$($Facts.Service): no running process" }
    elseif ([long] $Facts.ProcessStartUtcTicks -lt $DeployStartUtcTicks) { "$($Facts.Service): the running process started before this deployment (it has not loaded the new build)" }
    if ($Facts.ProcessPath -and $Facts.InstallDir -and -not $Facts.ProcessPath.StartsWith($Facts.InstallDir, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $Facts.ProcessPath -Leaf) -ne 'dotnet.exe') { "$($Facts.Service): the process runs from '$($Facts.ProcessPath)', not from the install directory" }
    if ($RequireBuildIdentityLog -and -not (@($Facts.BuildIdentityLines) | Where-Object { $_.ToLowerInvariant().Contains($ExpectedCommitSha.ToLowerInvariant()) })) { "$($Facts.Service): no startup log line shows build identity $ExpectedCommitSha since this deployment" }
    if (@($Facts.FatalLines).Count -gt 0) { "$($Facts.Service): startup log holds fatal / migration errors, e.g. $(@($Facts.FatalLines)[-1])" }
}
