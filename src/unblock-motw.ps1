<#
.SYNOPSIS
    Keeps Mark of the Web (Zone.Identifier) off files so that the Explorer
    preview pane keeps working.

.DESCRIPTION
    Keeps the NTFS alternate data stream "Zone.Identifier" (Mark of the Web, MOTW)
    off files, so that Windows Explorer preview pane / thumbnails / viewers stop
    refusing to show downloaded and ZIP-extracted files.

    How it works
      1. On start  - full sweep of every root (switching 'cmd /c dir /s /r' output,
                     the cheapest way to spot ADS on ~100k files).
      2. Afterwards - a FileSystemWatcher per root: a new file is unblocked within
                     1-3 seconds after it appears.
      3. Every SweepMinutes - one more sweep, because FileSystemWatcher does not
                     report writes to an alternate data stream only.

    Read-only files are handled as well: the attribute is cleared, Unblock-File is
    called, the attribute is restored.

.PARAMETER Roots
    Folders or drives to watch. Example: 'D:\', "$env:USERPROFILE\Downloads".

.PARAMETER SweepMinutes
    Interval between full sweeps. 30 by default.

.EXAMPLE
    .\unblock-motw.ps1 -Roots 'D:\', "$env:USERPROFILE\Downloads"
#>
param(
    [Parameter(Mandatory = $false)]
    [string[]]$Roots = @("$env:USERPROFILE\Downloads"),

    [int]$SweepMinutes = 30
)

$ErrorActionPreference = 'SilentlyContinue'

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$log  = Join-Path $here 'unblock.log'
$dirExe = "$env:SystemRoot\System32\cmd.exe"
$RO = [System.IO.FileAttributes]::ReadOnly

function Write-Log([string]$Message) {
    try {
        $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Message
        Add-Content -LiteralPath $log -Value $line -Encoding UTF8
        if ((Get-Item -LiteralPath $log).Length -gt 4MB) {
            Move-Item -LiteralPath $log -Destination "$log.1" -Force
        }
    } catch { }
}

# Removes Zone.Identifier from a single file.
# Unblock-File silently fails on read-only files, so the attribute is toggled.
function Unblock-FileHardened([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try {
        if (-not [System.IO.File]::Exists($Path)) { return $false }
        $fi = New-Object System.IO.FileInfo($Path)
        try { $null = Get-Item -LiteralPath $Path -Stream Zone.Identifier -ErrorAction Stop } catch { return $false }

        $wasReadOnly = [bool]($fi.Attributes -band $RO)
        if ($wasReadOnly) { $fi.Attributes = $fi.Attributes -bxor $RO }
        Unblock-File -LiteralPath $Path -ErrorAction SilentlyContinue
        if ($wasReadOnly) { $fi.Attributes = $fi.Attributes -bor $RO }

        $still = $false
        try { $null = Get-Item -LiteralPath $Path -Stream Zone.Identifier -ErrorAction Stop; $still = $true } catch { }
        if ($still) { Write-Log "ERR unblock $Path (still blocked)"; return $false }
        Write-Log "unblock $Path"
        return $true
    } catch {
        Write-Log ("ERR unblock $Path :: " + $_.Exception.Message)
        return $false
    }
}

# Full sweep.
#  1) cmd /c dir /s /r lists ":Zone.Identifier:$DATA" entries - fast enough for huge trees;
#  2) collect the directories that contain such files;
#  3) unblock every file in those directories (names are taken from the file system,
#     so localized dir output and Unicode normalization no longer matter).
function Invoke-Sweep {
    foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $started = Get-Date
        $text = & $dirExe /c "chcp 65001>nul & dir `"$root`" /s /r /a:-d" 2>$null
        $currentDir = $null
        $hitDirs = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($line in $text) {
            # header line: does not start with a digit and carries a path like C:\...
            if ($line -match '^\s+[^\d].*?([A-Za-z]:\\.*?)\s*$') {
                $candidate = $matches[1]
                if (Test-Path -LiteralPath $candidate -PathType Container) { $currentDir = $candidate; continue }
            }
            if ($line -like '*:Zone.Identifier:$DATA*' -and $currentDir) { [void]$hitDirs.Add($currentDir) }
        }
        $count = 0
        foreach ($dir in $hitDirs) {
            foreach ($f in (Get-ChildItem -LiteralPath $dir -File -Force -ErrorAction SilentlyContinue)) {
                if (-not (Get-Item -LiteralPath $f.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue)) { continue }
                if (Unblock-FileHardened $f.FullName) { $count++ }
            }
        }
        Write-Log ("sweep {0}: dirs={1} unblocked={2} ({3:n1} s)" -f $root, $hitDirs.Count, $count, ((Get-Date) - $started).TotalSeconds)
    }
}

# ---------- FileSystemWatcher ----------
$global:MotwQueue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
$watchers = New-Object System.Collections.ArrayList

foreach ($root in $Roots) {
    if (-not (Test-Path -LiteralPath $root)) { Write-Log "skip missing root $root"; continue }
    try {
        $w = New-Object System.IO.FileSystemWatcher
        $w.Path = $root
        $w.IncludeSubdirectories = $true
        $w.NotifyFilter = ([System.IO.NotifyFilters]::FileName -bor
                           [System.IO.NotifyFilters]::DirectoryName -bor
                           [System.IO.NotifyFilters]::CreationTime -bor
                           [System.IO.NotifyFilters]::LastWrite -bor
                           [System.IO.NotifyFilters]::Size)
        $w.InternalBufferSize = 65536

        $action = {
            try {
                $p = $EventArgs.FullPath
                if ($p -and -not $p.EndsWith('\')) { $global:MotwQueue.Enqueue($p) }
            } catch { }
        }
        foreach ($evt in 'Created', 'Changed', 'Renamed') {
            Register-ObjectEvent -InputObject $w -EventName $evt `
                -SourceIdentifier ("PreviewMotw-$evt-" + [guid]::NewGuid().ToString('N')) -Action $action | Out-Null
        }
        $w.EnableRaisingEvents = $true
        $watchers.Add($w) | Out-Null
        Write-Log "watching $root"
    } catch {
        Write-Log ("ERR watcher $root :: " + $_.Exception.Message)
    }
}

Write-Log ("START pid={0} roots={1}" -f $PID, ($Roots -join '; '))

Invoke-Sweep
$lastSweep = Get-Date

while ($true) {
    Start-Sleep -Seconds 2
    $item = $null
    while ($global:MotwQueue.TryDequeue([ref]$item)) { [void](Unblock-FileHardened $item) }
    if (((Get-Date) - $lastSweep).TotalMinutes -ge $SweepMinutes) {
        Invoke-Sweep
        $lastSweep = Get-Date
    }
}
