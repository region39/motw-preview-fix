#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the preview-handler pack and the Mark-of-the-Web watcher.

.PARAMETER Full
    Also revert the Attachment Manager / SmartScreen settings that install.ps1 changed.

.PARAMETER Purge
    Also delete the install directory (%LOCALAPPDATA%\PreviewMotw), including the log.

.EXAMPLE
    .\uninstall.ps1 -Full -Purge
#>
[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$Purge
)

$ErrorActionPreference = 'Continue'

$HandlerClsid = '{B5C1D3E7-4A62-4F0F-9C1E-7D2A5B8F3C64}'
$PreviewIid   = '{8895B1C6-B41F-4C1C-A562-0D564250836F}'
$TxtPreviewer = '{1531D583-8375-4D3F-B5FB-D23BBD169F22}'
$TaskName     = 'PreviewMotw-Unblock'
$installDir   = Join-Path $env:LOCALAPPDATA 'PreviewMotw'
$regasm       = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"

$ImageExts = 'png', 'jpg', 'jpeg', 'jpe', 'tif', 'tiff', 'bmp', 'gif', 'psd', 'psb', 'svg', 'svgz', 'ai', 'eps', 'ps'
$TextExts  = 'txt', 'json', 'md', 'markdown', 'sh', 'bash', 'log', 'yml', 'yaml', 'xml', 'css', 'js',
             'cs', 'py', 'ini', 'cfg', 'conf', 'ts', 'sql', 'ps1', 'bat', 'cmd'

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

Write-Step 'Watcher task'
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Write-Host '   task still present' -ForegroundColor Yellow }
else { Write-Host '   removed' -ForegroundColor Green }

Write-Step 'Preview handler'
$dll = Join-Path $installDir 'PreviewMotwHandlers.dll'
if (Test-Path -LiteralPath $dll) { & $regasm /nologo /unregister $dll 2>&1 | Out-Null }
Remove-Item -LiteralPath "HKLM:\Software\Classes\CLSID\$HandlerClsid" -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers' `
    -Name $HandlerClsid -ErrorAction SilentlyContinue
Write-Host '   COM registration removed' -ForegroundColor Green

Write-Step 'Associations'
foreach ($e in $ImageExts) {
    $k = "HKLM:\Software\Classes\SystemFileAssociations\.$e\ShellEx\$PreviewIid"
    $v = (Get-ItemProperty -LiteralPath $k -ErrorAction SilentlyContinue).'(default)'
    if ($v -eq $HandlerClsid) { Remove-ItemProperty -LiteralPath $k -Name '(default)' -ErrorAction SilentlyContinue }
}
foreach ($e in $TextExts) {
    $k = "HKLM:\Software\Classes\SystemFileAssociations\.$e\ShellEx\$PreviewIid"
    $v = (Get-ItemProperty -LiteralPath $k -ErrorAction SilentlyContinue).'(default)'
    if ($v -eq $TxtPreviewer) { Remove-ItemProperty -LiteralPath $k -Name '(default)' -ErrorAction SilentlyContinue }
}
Write-Host '   preview associations removed' -ForegroundColor Green

if ($Full) {
    Write-Step 'Attachment Manager / SmartScreen settings'
    $att = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Attachments'
    Remove-ItemProperty -Path $att -Name SaveZoneInformation      -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $att -Name HideZoneInfoOnProperties -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments' `
        -Name ScanWithAntiVirus -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\AppHost' `
        -Name EnableWebContentEvaluation -ErrorAction SilentlyContinue
    Write-Host '   reverted' -ForegroundColor Green
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PreviewMotwRefresh {
    [DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, int flags, IntPtr i1, IntPtr i2);
}
'@ -ErrorAction SilentlyContinue
[PreviewMotwRefresh]::SHChangeNotify(0x08000000, 0x1000, [IntPtr]::Zero, [IntPtr]::Zero)

if ($Purge) {
    Write-Step 'Install directory'
    Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "   $installDir removed" -ForegroundColor Green
}

Write-Host "`nUninstall finished. Restart Explorer (or sign out and back in) to drop cached handlers." -ForegroundColor Green
