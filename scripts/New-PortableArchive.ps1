param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$Prefix = '',
    [string[]]$ExcludeDirectory = @()
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Source = [IO.Path]::GetFullPath($Source).TrimEnd('\','/')
$zip = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = Get-ChildItem $Source | Where-Object { $_.Name -notin $ExcludeDirectory } | ForEach-Object {
        if ($_.PSIsContainer) { Get-ChildItem $_.FullName -File -Recurse } else { $_ }
    }
    $files | ForEach-Object {
        $relative = $_.FullName.Substring($Source.Length).TrimStart('\','/').Replace('\','/')
        $entryName = if ($Prefix) { $Prefix.TrimEnd('/') + '/' + $relative } else { $relative }
        $compression = if ($_.Extension -eq '.zip') { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$_.FullName,$entryName,$compression) | Out-Null
    }
} finally { $zip.Dispose() }
