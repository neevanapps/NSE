function New-ChangedDeploymentArchive {
    param([string] $StagingDirectory, [string[]] $RelativePaths, [string] $ArchivePath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($ArchivePath, 'Create')
    try {
        foreach ($relative in $RelativePaths) {
            $entry = $relative.Replace('\', '/')
            if ([System.IO.Path]::IsPathRooted($relative) -or $entry.Contains(':') -or $entry.StartsWith('/') -or
                $entry.Split('/') -contains '..' -or $entry.Split('/')[-1] -eq 'appsettings.Local.json') {
                throw 'Deployment archive contains an unsafe or local-configuration path.'
            }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, (Join-Path $StagingDirectory $relative), $entry,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
}
