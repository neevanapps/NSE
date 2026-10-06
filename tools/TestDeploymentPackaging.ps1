$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DeploymentPackaging.ps1')
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('nifty-package-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $stage = Join-Path $root 'stage'
    $target = Join-Path $root 'installed'
    New-Item -ItemType Directory -Path (Join-Path $stage '.playwright/package'), $target -Force | Out-Null
    Set-Content (Join-Path $stage 'changed.dll') 'new-binary'
    Set-Content (Join-Path $stage '.playwright/package/driver.js') 'new-driver'
    Set-Content (Join-Path $stage 'unchanged.dll') 'unchanged'
    Set-Content (Join-Path $stage 'appsettings.Local.json') 'desktop-secret'
    Set-Content (Join-Path $target 'appsettings.Local.json') 'vm-secret'
    Set-Content (Join-Path $target 'changed.dll') 'old-binary'
    $zip = Join-Path $root 'delta.zip'
    New-ChangedDeploymentArchive -StagingDirectory $stage -RelativePaths @('changed.dll', '.playwright/package/driver.js') -ArchivePath $zip
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        if ($names.Count -ne 2 -or $names -notcontains '.playwright/package/driver.js') { throw 'Delta archive entries differ.' }
    } finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $zip -DestinationPath $target -Force
    if ((Get-Content (Join-Path $target 'appsettings.Local.json') -Raw).Trim() -ne 'vm-secret') { throw 'VM local config was replaced.' }
    if ((Get-Content (Join-Path $target 'changed.dll') -Raw).Trim() -ne 'new-binary') { throw 'Changed binary not updated.' }
    if (Test-Path (Join-Path $target 'unchanged.dll')) { throw 'Unchanged file unexpectedly transferred.' }
    if (!(Test-Path (Join-Path $target '.playwright/package/driver.js'))) { throw 'Nested driver file missing.' }
    foreach ($unsafe in @('appsettings.Local.json', '../escape.txt')) {
        $rejected = $false
        try { New-ChangedDeploymentArchive -StagingDirectory $stage -RelativePaths @($unsafe) -ArchivePath (Join-Path $root ([Guid]::NewGuid().ToString('N') + '.zip')) }
        catch { $rejected = $_.Exception.Message -like 'Deployment archive contains an unsafe*' }
        if (!$rejected) { throw "Unsafe archive path was not rejected: $unsafe" }
    }
    Write-Host 'Deployment delta ZIP PASS: nested driver, changed files only, VM secrets retained, traversal rejected.'
} finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
