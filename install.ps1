#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the preview-handler pack and the Mark-of-the-Web auto-unblocker.

.DESCRIPTION
    1. Builds %LOCALAPPDATA%\PreviewMotw\PreviewMotwHandlers.dll with csc.exe
       (no SDK or Visual Studio required) and registers it as a COM preview handler.
    2. Associates the handler with image / PSD / SVG / EPS / AI extensions and
       re-associates plain text extensions with the built-in Windows TXT previewer.
    3. Relaxes the Attachment Manager settings that make Windows keep the
       Zone.Identifier stream on downloaded/attached files.
    4. Registers the scheduled task "PreviewMotw-Unblock" that keeps the
       Zone.Identifier stream off the given roots.

.PARAMETER Roots
    Drives or folders to keep MOTW-free. Default: the current user's Downloads.

.PARAMETER SweepMinutes
    Interval of the fallback full sweep in the watcher task. Default: 30.

.PARAMETER SkipPreviewHandler
    Do not build/register the preview handler (MOTW part only).

.PARAMETER SkipMotw
    Do not touch Attachment Manager settings and do not register the task
    (preview handler part only).

.EXAMPLE
    .\install.ps1 -Roots 'D:\','E:\Media', "$env:USERPROFILE\Downloads"
#>
[CmdletBinding()]
param(
    [string[]]$Roots = @("$env:USERPROFILE\Downloads"),
    [int]$SweepMinutes = 30,
    [switch]$SkipPreviewHandler,
    [switch]$SkipMotw
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- constants
$HandlerClsid   = '{B5C1D3E7-4A62-4F0F-9C1E-7D2A5B8F3C64}'
$PreviewIid     = '{8895B1C6-B41F-4C1C-A562-0D564250836F}'
$TxtPreviewer   = '{1531D583-8375-4D3F-B5FB-D23BBD169F22}'
$PrevhostAppId  = '{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}'
$TaskName       = 'PreviewMotw-Unblock'
$HandlerName    = 'PreviewMotw Image Preview Handler'

$ImageExts = 'png', 'jpg', 'jpeg', 'jpe', 'tif', 'tiff', 'bmp', 'gif', 'psd', 'psb', 'svg', 'svgz', 'ai', 'eps', 'ps'
$TextExts  = 'txt', 'json', 'md', 'markdown', 'sh', 'bash', 'log', 'yml', 'yaml', 'xml', 'css', 'js',
             'cs', 'py', 'ini', 'cfg', 'conf', 'ts', 'sql', 'ps1', 'bat', 'cmd'

$root      = Split-Path -Parent $PSCommandPath
$installDir = Join-Path $env:LOCALAPPDATA 'PreviewMotw'
$csc        = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$regasm     = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"
$powershell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "   OK  $Text" -ForegroundColor Green }
function Write-Warn2([string]$Text){ Write-Host "   !!  $Text" -ForegroundColor Yellow }

foreach ($tool in $csc, $regasm) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "Required tool not found: $tool (.NET Framework 4.x is required)" }
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null

# ---------------------------------------------------------------- MOTW policy
if (-not $SkipMotw) {
    Write-Step 'Attachment Manager settings'

    $att = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Attachments'
    New-Item -Path $att -Force | Out-Null
    Set-ItemProperty -Path $att -Name SaveZoneInformation     -Value 1 -Type DWord
    Set-ItemProperty -Path $att -Name HideZoneInfoOnProperties -Value 1 -Type DWord
    Write-Ok 'HKCU Policies\Attachments: SaveZoneInformation=1, HideZoneInfoOnProperties=1'

    $attLm = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments'
    if (-not (Test-Path $attLm)) { New-Item -Path $attLm -Force | Out-Null }
    Set-ItemProperty -Path $attLm -Name ScanWithAntiVirus -Value 1 -Type DWord
    Write-Ok 'HKLM Policies\Attachments: ScanWithAntiVirus=1'

    $appHost = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\AppHost'
    if (-not (Test-Path $appHost)) { New-Item -Path $appHost -Force | Out-Null }
    Set-ItemProperty -Path $appHost -Name EnableWebContentEvaluation -Value 0 -Type DWord
    Write-Ok 'HKCU AppHost: EnableWebContentEvaluation=0'
}

# ---------------------------------------------------------------- preview handler
if (-not $SkipPreviewHandler) {
    Write-Step 'Preview handler'

    Copy-Item -LiteralPath (Join-Path $root 'src\PreviewMotwHandlers.cs') -Destination $installDir -Force
    $dll = Join-Path $installDir 'PreviewMotwHandlers.dll'

    & $csc /nologo /target:library /platform:x64 /optimize+ "/out:$dll" `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
        (Join-Path $installDir 'PreviewMotwHandlers.cs')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll)) { throw 'csc.exe failed to build PreviewMotwHandlers.dll' }
    Write-Ok "built $dll"

    # regasm writes its "unsigned assembly" warning to stderr: keep that non-terminating
    $ErrorActionPreference = 'Continue'
    & $regasm /nologo /codebase $dll 2>&1 | Where-Object { $_ -notmatch 'RA0000' } | ForEach-Object { Write-Host "   $_" }
    $regasmExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($regasmExit -ne 0) { throw 'regasm.exe failed to register the handler' }

    $clsidKey = "HKLM:\Software\Classes\CLSID\$HandlerClsid"
    Set-ItemProperty -Path $clsidKey -Name '(default)' -Value $HandlerName
    Set-ItemProperty -Path $clsidKey -Name 'AppID'     -Value $PrevhostAppId
    New-ItemProperty -Path $clsidKey -Name 'DisableLowILProcessIsolation' -Value 1 -PropertyType DWord -Force | Out-Null
    New-Item -Path "$clsidKey\Implemented Categories\$PreviewIid" -Force | Out-Null
    New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers' `
        -Name $HandlerClsid -Value $HandlerName -PropertyType String -Force | Out-Null
    Write-Ok 'COM class + preview-handler category + prevhost AppID'

    foreach ($e in $ImageExts) {
        $k = "HKLM:\Software\Classes\SystemFileAssociations\.$e\ShellEx\$PreviewIid"
        New-Item -Path $k -Force | Out-Null
        Set-ItemProperty -Path $k -Name '(default)' -Value $HandlerClsid
    }
    Write-Ok ("associations: " + ($ImageExts -join ' '))

    foreach ($e in $TextExts) {
        $k = "HKLM:\Software\Classes\SystemFileAssociations\.$e\ShellEx\$PreviewIid"
        New-Item -Path $k -Force | Out-Null
        Set-ItemProperty -Path $k -Name '(default)' -Value $TxtPreviewer
    }
    Write-Ok ("text previewer associations: " + ($TextExts -join ' '))

    if (-not (Test-Path -LiteralPath 'C:\Program Files\gs')) {
        Write-Warn2 'Ghostscript not found - .eps/.ai/.ps will show "no preview" until it is installed'
    }
}

# ---------------------------------------------------------------- watcher task
if (-not $SkipMotw) {
    Write-Step 'Mark-of-the-Web watcher task'

    Copy-Item -LiteralPath (Join-Path $root 'src\unblock-motw.ps1') -Destination $installDir -Force
    $script = Join-Path $installDir 'unblock-motw.ps1'

    # roots are handed over through roots.json: quoting drive roots like "D:\" on a
    # command line is a minefield (\" is an escaped quote for powershell.exe)
    ConvertTo-Json -InputObject @($Roots) -Compress | Set-Content -LiteralPath (Join-Path $installDir 'roots.json') -Encoding UTF8

    $argLine = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -SweepMinutes {1}' -f $script, $SweepMinutes

    $action    = New-ScheduledTaskAction -Execute $powershell -Argument $argLine
    $trigger   = New-ScheduledTaskTrigger -AtLogOn
    try { $trigger.Delay = 'PT1M' } catch { }
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
                    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId ("{0}\{1}" -f $env:USERDOMAIN, $env:USERNAME) `
                    -LogonType Interactive -RunLevel Highest

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
        -Principal $principal -Description 'Removes Mark of the Web (Zone.Identifier) so the Explorer preview pane keeps working' -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Write-Ok ("task '{0}' registered and started" -f $TaskName)
    Write-Ok ("roots: " + ($Roots -join '; '))
}

# ---------------------------------------------------------------- refresh shell
if (-not ('PreviewMotwShell' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class PreviewMotwShell {
    [DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, int flags, IntPtr i1, IntPtr i2);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder outBuf, ref int outLen);
    public static string Handler(string ext) {
        int len = 512; var sb = new StringBuilder(len);
        int hr = AssocQueryString(0, 16, ext, "{8895B1C6-B41F-4C1C-A562-0D564250836F}", sb, ref len);
        return sb.ToString();
    }
}
'@
}
[PreviewMotwShell]::SHChangeNotify(0x08000000, 0x1000, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Step 'Verification'
foreach ($e in '.png', '.psd', '.svg', '.ai', '.eps', '.json', '.md', '.sh') {
    $h = [PreviewMotwShell]::Handler($e)
    if ([string]::IsNullOrEmpty($h)) { Write-Warn2 ("{0,-6} no preview handler" -f $e) }
    else { Write-Host ("   {0,-6} -> {1}" -f $e, $h) }
}
Write-Host ""
Write-Host "Done. Install dir: $installDir" -ForegroundColor Green
Write-Host "Log: $(Join-Path $installDir 'unblock.log')"
Write-Host "Uninstall: .\uninstall.ps1"
