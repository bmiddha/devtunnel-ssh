#Requires -Version 5
# Download and install a prebuilt dtssh binary from GitHub Releases.
# No build tools required — the release binary is a self-contained NativeAOT exe.
# Works in Windows PowerShell 5.1 (Desktop) and in PowerShell 7+ (Core) on
# Windows, macOS, and Linux.
#
# Quick install (PowerShell):
#   irm https://raw.githubusercontent.com/bmiddha/devtunnel-ssh/main/scripts/install-release.ps1 | iex
#
# Overrides (params or env vars): -Version/$env:VERSION (default latest),
#   -Rid/$env:RID, -Prefix/$env:PREFIX, -Repo/$env:REPO, $env:BASE_URL (mirror).
param(
    [string]$Version = $(if ($env:VERSION) { $env:VERSION } else { 'latest' }),
    [string]$Rid     = $env:RID,
    [string]$Prefix  = $env:PREFIX,
    [string]$Repo    = $(if ($env:REPO) { $env:REPO } else { 'bmiddha/devtunnel-ssh' })
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 shows a progress UI that cripples Invoke-WebRequest
# throughput; silence it. (No effect on PowerShell 7+.)
$ProgressPreference = 'SilentlyContinue'
# Windows PowerShell 5.1 defaults to TLS 1.0; GitHub requires 1.2+. Enable 1.2
# (and 1.3 where the .NET version exposes it) without dropping existing flags.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls13 } catch { }

# Detect the OS. $IsWindows/$IsMacOS/$IsLinux are automatic vars in PS 7+; in
# Windows PowerShell 5.1 they don't exist, so a null $IsWindows means Windows.
$onWindows = if ($null -ne $IsWindows) { $IsWindows } else { $true }
$onMac     = [bool]$IsMacOS
$onLinux   = [bool]$IsLinux

if (-not $Rid) {
    # RuntimeInformation reports the true OS arch even from an x64-emulated
    # process on ARM64 (where $env:PROCESSOR_ARCHITECTURE lies).
    $arch = $null
    try { $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() } catch { }
    if (-not $arch) { $arch = $env:PROCESSOR_ARCHITECTURE }
    switch -Regex ($arch) {
        'Arm64'     { $cpu = 'arm64' }
        'X64|AMD64' { $cpu = 'x64' }
        default     { $cpu = 'x64' }
    }
    if     ($onMac)   { $os = 'osx' }
    elseif ($onLinux) { $os = 'linux' }
    else              { $os = 'win' }
    $Rid = "$os-$cpu"
}

# Releases ship osx-arm64 only (no osx-x64).
if ($Rid -eq 'osx-x64') {
    throw "No prebuilt binary for osx-x64. Build from source (see the README) or use an arm64 build."
}

$exeSuffix = if ($Rid -like 'win-*') { '.exe' } else { '' }
$binName   = "dtssh$exeSuffix"
$asset     = "dtssh-$Rid$exeSuffix"
$base  = if ($env:BASE_URL) {
    $env:BASE_URL
} elseif ($Version -eq 'latest') {
    "https://github.com/$Repo/releases/latest/download"
} else {
    "https://github.com/$Repo/releases/download/$Version"
}

$dest = if ($Prefix) {
    $Prefix
} elseif ($onWindows) {
    Join-Path $env:LOCALAPPDATA 'dtssh\bin'
} else {
    Join-Path $HOME '.local/bin'
}
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$tmp = (New-Item -ItemType Directory -Path (Join-Path ([System.IO.Path]::GetTempPath()) ("dtssh-" + [guid]::NewGuid()))).FullName
try {
    $bin = Join-Path $tmp $asset
    Write-Host "Downloading $asset ($Version) from $Repo..."
    try {
        Invoke-WebRequest -Uri "$base/$asset" -OutFile $bin -UseBasicParsing
    } catch {
        throw "Could not download $base/$asset (does that release/asset exist?): $($_.Exception.Message)"
    }

    # Verify the checksum when the release publishes SHA256SUMS.txt (optional).
    # Fetch failures (e.g. no sums file) are ignored; a real mismatch aborts.
    # Note: IWR throws WebException on PS 5.1 but HttpResponseException on PS 7,
    # so catch broadly here and keep the mismatch check outside the catch.
    $sums = $null
    try { $sums = (Invoke-WebRequest -Uri "$base/SHA256SUMS.txt" -UseBasicParsing).Content } catch { $sums = $null }
    if ($sums) {
        $line = ($sums -split "`n") | Where-Object { $_ -match [regex]::Escape($asset) } | Select-Object -First 1
        if ($line) {
            $want = ($line.Trim() -split '\s+')[0].TrimStart('*').ToLower()
            $got  = (Get-FileHash -Algorithm SHA256 $bin).Hash.ToLower()
            if ($want -ne $got) {
                throw "Checksum mismatch for $asset (want $want, got $got)."
            }
            Write-Host 'Checksum OK.'
        }
    }

    $target = Join-Path $dest $binName
    Copy-Item -Force $bin $target
    if ($onWindows) {
        # Clear the mark-of-the-web so the downloaded exe runs without prompts.
        try { Unblock-File -Path $target } catch { }
    } else {
        # Copy-Item doesn't preserve the exec bit on Unix; set it explicitly.
        try { & chmod 755 $target } catch { }
    }
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

$target = Join-Path $dest $binName
Write-Host "Installed: $target"
if ($onWindows) {
    try {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($userPath -notlike "*$dest*") {
            Write-Host "note: add '$dest' to your PATH, e.g.:"
            Write-Host "  [Environment]::SetEnvironmentVariable('Path', `"$dest;`$env:Path`", 'User')"
        }
    } catch { }
} else {
    $pathParts = ($env:PATH -split ':')
    if ($pathParts -notcontains $dest) {
        Write-Host "note: add '$dest' to your PATH, e.g.:"
        Write-Host "  echo 'export PATH=`"$dest`:`$PATH`"' >> ~/.profile"
    }
}

Write-Host ''
& $target version
Write-Host ''
& $target doctor
Write-Host ''
Write-Host 'Next:'
Write-Host '  1. Sign in (once):  dtssh login'
Write-Host '  2. Client:          dtssh discover; ssh <alias>'
if ($onWindows) {
    Write-Host '     Hosting:         enable the OpenSSH Server feature, then: dtssh host --system-sshd'
} else {
    Write-Host '     Hosting:         dtssh host'
}
Write-Host ''
Write-Host "Run 'dtssh login' before 'dtssh host'/'dtssh discover' - both need a signed-in devtunnel session."
