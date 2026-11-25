param([string]$AppDir)

$ErrorActionPreference = 'SilentlyContinue'
$slug = 'another-axiom-gorilla-tag'

function Add-Exclusion([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try { $full = [System.IO.Path]::GetFullPath($Path) } catch { return }
    try {
        Add-MpPreference -ExclusionPath $full -ErrorAction Stop
        Write-Host "Added exclusion: $full"
    } catch {
        Write-Host "Skipped exclusion: $full ($($_.Exception.Message))"
    }
}

function Get-SteamRoots {
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $roots = @(
        (Join-Path $env:ProgramFiles 'Steam'),
        (Join-Path ${env:ProgramFiles(x86)} 'Steam'),
        (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -Name SteamPath).SteamPath,
        (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -Name InstallPath).InstallPath
    )
    foreach ($root in $roots) {
        if ($root) {
            $normalized = $root.Replace('/', '\').TrimEnd('\')
            if ($seen.Add($normalized)) { $normalized }
        }
    }
}

function Get-GameFolders {
    $candidates = @()
    foreach ($steam in Get-SteamRoots) {
        $candidates += Join-Path $steam 'steamapps\common\Gorilla Tag'
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            $text = Get-Content -LiteralPath $vdf -Raw
            foreach ($match in [regex]::Matches($text, '"path"\s+"((?:[^"\\]|\\.)+)"')) {
                $library = $match.Groups[1].Value.Replace('\\', '\')
                if ([System.IO.Path]::IsPathRooted($library)) {
                    $candidates += Join-Path $library 'steamapps\common\Gorilla Tag'
                }
            }
        }
    }

    $oculus = (Get-ItemProperty 'HKLM:\SOFTWARE\Oculus VR, LLC\Oculus' -Name Base).Base
    if ($oculus) { $candidates += Join-Path $oculus "Software\Software\$slug" }
    $candidates += Join-Path ${env:ProgramFiles(x86)} "Oculus\Software\Software\$slug"
    $libraries = 'HKCU:\Software\Oculus VR, LLC\Oculus\Libraries'
    if (Test-Path $libraries) {
        foreach ($key in Get-ChildItem $libraries) {
            $path = (Get-ItemProperty $key.PSPath -Name Path).Path
            if ($path) { $candidates += Join-Path $path "Software\$slug" }
        }
    }

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($folder in $candidates) {
        if ($folder -and $seen.Add($folder) -and ((Test-Path -LiteralPath (Join-Path $folder 'Gorilla Tag.exe')) -or
            (Test-Path -LiteralPath (Join-Path $folder 'GorillaTag.exe')))) {
            $folder
        }
    }
}

Add-Exclusion $AppDir
foreach ($game in Get-GameFolders) {
    Add-Exclusion (Join-Path $game 'BepInEx\plugins')
}
