[CmdletBinding()]
param(
    [switch] $Quiet,
    [switch] $RemoveSettings
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Dialog {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message,

        [Parameter(Mandatory = $true)]
        [string] $Title,

        [Parameter(Mandatory = $true)]
        [System.Windows.Forms.MessageBoxButtons] $Buttons,

        [Parameter(Mandatory = $true)]
        [System.Windows.Forms.MessageBoxIcon] $Icon
    )

    return [System.Windows.Forms.MessageBox]::Show($Message, $Title, $Buttons, $Icon)
}

try {
    Add-Type -AssemblyName System.Windows.Forms

    if (-not $Quiet) {
        $choice = Show-Dialog `
            -Message "Remove Fortnite Match Compiler and its shortcuts?`n`nFinished compilations and local settings will be kept." `
            -Title 'Uninstall Fortnite Match Compiler' `
            -Buttons ([System.Windows.Forms.MessageBoxButtons]::YesNo) `
            -Icon ([System.Windows.Forms.MessageBoxIcon]::Question)
        if ($choice -ne [System.Windows.Forms.DialogResult]::Yes) {
            exit 0
        }
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

    $programInstallRoot = [IO.Path]::GetFullPath(
        (Join-Path $localApplicationData 'Programs'))
    $installDirectory = [IO.Path]::GetFullPath(
        (Join-Path $programInstallRoot 'Fortnite Match Compiler'))
    if (-not [IO.Path]::GetDirectoryName($installDirectory).Equals(
            $programInstallRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The resolved installation directory did not pass the safety check.'
    }

    $desktopShortcut = Join-Path $desktopDirectory 'Compile Latest Fortnite Match.lnk'
    $startMenuDirectory = Join-Path $programsDirectory 'Fortnite Match Compiler'
    $settingsDirectory = [IO.Path]::GetFullPath(
        (Join-Path $localApplicationData 'FortniteMatchCompiler'))

    if (Test-Path -LiteralPath $desktopShortcut) {
        Remove-Item -LiteralPath $desktopShortcut -Force
    }
    if (Test-Path -LiteralPath $startMenuDirectory) {
        Remove-Item -LiteralPath $startMenuDirectory -Recurse -Force
    }

    Set-Location ([IO.Path]::GetTempPath())
    if (Test-Path -LiteralPath $installDirectory) {
        Remove-Item -LiteralPath $installDirectory -Recurse -Force
    }
    if ($RemoveSettings -and (Test-Path -LiteralPath $settingsDirectory)) {
        Remove-Item -LiteralPath $settingsDirectory -Recurse -Force
    }

    if (-not $Quiet) {
        $settingsMessage = if ($RemoveSettings) {
            'Local settings, history, and logs were also removed.'
        }
        else {
            'Finished compilations and local settings were kept.'
        }
        [void] (Show-Dialog `
            -Message "Fortnite Match Compiler was removed.`n`n$settingsMessage" `
            -Title 'Uninstall complete' `
            -Buttons ([System.Windows.Forms.MessageBoxButtons]::OK) `
            -Icon ([System.Windows.Forms.MessageBoxIcon]::Information))
    }
    exit 0
}
catch {
    if ($Quiet) {
        Write-Error "Uninstall failed: $($_.Exception.Message)"
    }
    else {
        try {
            [void] (Show-Dialog `
                -Message "Uninstall failed:`n`n$($_.Exception.Message)" `
                -Title 'Uninstall Fortnite Match Compiler' `
                -Buttons ([System.Windows.Forms.MessageBoxButtons]::OK) `
                -Icon ([System.Windows.Forms.MessageBoxIcon]::Error))
        }
        catch {
            Write-Error "Uninstall failed: $($_.Exception.Message)"
        }
    }
    exit 1
}
