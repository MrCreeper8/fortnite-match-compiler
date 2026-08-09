[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-AppShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ShortcutPath,

        [Parameter(Mandatory = $true)]
        [string] $TargetPath,

        [string] $Arguments = '',
        [string] $WorkingDirectory = '',
        [string] $Description = '',
        [string] $IconPath = ''
    )

    $shortcutDirectory = Split-Path -Parent $ShortcutPath
    New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $TargetPath
        $shortcut.Arguments = $Arguments
        $shortcut.WorkingDirectory = $WorkingDirectory
        $shortcut.Description = $Description
        if (-not [string]::IsNullOrWhiteSpace($IconPath)) {
            $shortcut.IconLocation = "$IconPath,0"
        }
        $shortcut.Save()
    }
    finally {
        if ($null -ne $shell) {
            [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }
}

try {
    $packageDirectory = Split-Path -Parent $PSCommandPath
    $sourceExecutable = Join-Path $packageDirectory 'Fortnite Match Compiler.exe'
    $sourceUninstaller = Join-Path $packageDirectory 'Uninstall.ps1'
    $sourceFfmpeg = Join-Path $packageDirectory 'ffmpeg.exe'
    $sourceFfprobe = Join-Path $packageDirectory 'ffprobe.exe'

    if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
        throw 'Fortnite Match Compiler.exe was not found beside the installer. Extract the complete release ZIP and try again.'
    }
    if (-not (Test-Path -LiteralPath $sourceUninstaller -PathType Leaf)) {
        throw 'Uninstall.ps1 was not found beside the installer. Extract the complete release ZIP and try again.'
    }
    $hasFfmpeg = Test-Path -LiteralPath $sourceFfmpeg -PathType Leaf
    $hasFfprobe = Test-Path -LiteralPath $sourceFfprobe -PathType Leaf
    if ($hasFfmpeg -ne $hasFfprobe) {
        throw 'If you place FFmpeg beside the installer, both ffmpeg.exe and ffprobe.exe are required.'
    }

    $localApplicationData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    $desktopDirectory = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::DesktopDirectory)
    $programsDirectory = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::Programs)

    if ([string]::IsNullOrWhiteSpace($localApplicationData) -or
        [string]::IsNullOrWhiteSpace($desktopDirectory) -or
        [string]::IsNullOrWhiteSpace($programsDirectory)) {
        throw 'Windows did not provide one or more required per-user folders.'
    }

    $installDirectory = Join-Path $localApplicationData 'Programs\Fortnite Match Compiler'
    $destinationExecutable = Join-Path $installDirectory 'Fortnite Match Compiler.exe'
    $destinationUninstaller = Join-Path $installDirectory 'Uninstall.ps1'
    $startMenuDirectory = Join-Path $programsDirectory 'Fortnite Match Compiler'

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    Copy-Item -LiteralPath $sourceExecutable -Destination $destinationExecutable -Force
    Copy-Item -LiteralPath $sourceUninstaller -Destination $destinationUninstaller -Force
    if ($hasFfmpeg) {
        Copy-Item -LiteralPath $sourceFfmpeg -Destination $installDirectory -Force
        Copy-Item -LiteralPath $sourceFfprobe -Destination $installDirectory -Force
    }

    foreach ($documentName in @('README.md', 'LICENSE')) {
        $sourceDocument = Join-Path $packageDirectory $documentName
        if (Test-Path -LiteralPath $sourceDocument -PathType Leaf) {
            Copy-Item -LiteralPath $sourceDocument -Destination $installDirectory -Force
        }
    }

    New-AppShortcut `
        -ShortcutPath (Join-Path $desktopDirectory 'Compile Latest Fortnite Match.lnk') `
        -TargetPath $destinationExecutable `
        -Arguments '--compile-latest' `
        -WorkingDirectory $installDirectory `
        -Description 'Compile the latest completed Fortnite match' `
        -IconPath $destinationExecutable

    New-AppShortcut `
        -ShortcutPath (Join-Path $startMenuDirectory 'Fortnite Match Compiler.lnk') `
        -TargetPath $destinationExecutable `
        -WorkingDirectory $installDirectory `
        -Description 'Open Fortnite Match Compiler' `
        -IconPath $destinationExecutable

    $powerShellExecutable = Join-Path $PSHOME 'powershell.exe'
    New-AppShortcut `
        -ShortcutPath (Join-Path $startMenuDirectory 'Uninstall Fortnite Match Compiler.lnk') `
        -TargetPath $powerShellExecutable `
        -Arguments "-NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$destinationUninstaller`"" `
        -WorkingDirectory $installDirectory `
        -Description 'Uninstall Fortnite Match Compiler' `
        -IconPath $destinationExecutable

    Write-Host ''
    Write-Host 'Fortnite Match Compiler is installed.' -ForegroundColor Green
    Write-Host "Application: $destinationExecutable"
    Write-Host "Desktop shortcut: $(Join-Path $desktopDirectory 'Compile Latest Fortnite Match.lnk')"
    Write-Host "Start Menu folder: $startMenuDirectory"
    exit 0
}
catch {
    Write-Host ''
    Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'If the app is already running, close it before installing an update.'
    exit 1
}
