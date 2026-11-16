param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\dist\linux-x64'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
dotnet publish (Join-Path $PSScriptRoot 'SimpleModManager.Linux.csproj') -c Release -r linux-x64 --self-contained true -p:DebugType=none -o $output
if ($LASTEXITCODE -ne 0) { throw 'Linux publish failed.' }
Copy-Item (Join-Path $PSScriptRoot 'install.sh') $output
Copy-Item (Join-Path $PSScriptRoot 'README.md') (Join-Path $output 'README-Linux.md')
$archive = Join-Path (Split-Path $output -Parent) 'SimpleModManager-linux-x64.tar.gz'
$stream = [IO.File]::Create($archive)
try {
    $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::SmallestSize, $true)
    try {
        $tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse) {
                $relative = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\', '/')
                $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $relative)
                $entry.Mode = [IO.UnixFileMode]420
                if ($relative -eq 'SimpleModManager.Linux' -or $relative.EndsWith('.sh')) {
                    $entry.Mode = [IO.UnixFileMode]493
                }
                $input = [IO.File]::OpenRead($file.FullName)
                try {
                    $entry.DataStream = $input
                    $tar.WriteEntry($entry)
                } finally { $input.Dispose() }
            }
        } finally { $tar.Dispose() }
    } finally { $gzip.Dispose() }
} finally { $stream.Dispose() }
Get-FileHash -LiteralPath $archive -Algorithm SHA256
