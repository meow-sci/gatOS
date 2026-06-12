# tools/fetch-qemu.ps1 - fetch + verify + trim the pinned QEMU win-x64 bundle (OS_PLAN.md T11.1).
#
# Downloads the pinned Stefan Weil qemu-w64 installer (pin lives in tools/qemu-win64-files.txt,
# the same file that lists the bundle contents), verifies its sha512, extracts it with 7-Zip
# (bootstrapping a portable 7-Zip via an administrative MSI extract when none is installed -
# the installer is NSIS, which only full 7z.exe can unpack), and copies exactly the listed
# files into vendor/qemu/win-x64/. Re-running is a no-op when the bundle is already current.
#
# Usage:  tools/fetch-qemu.ps1 [-Force]
#   -Force   refetch + re-extract + repopulate even when the bundle looks current.

[CmdletBinding()]
param([switch] $Force)

$ErrorActionPreference = 'Stop'

# 7-Zip bootstrap pin. sha256 cross-checked against the winget community manifest
# (microsoft/winget-pkgs manifests/7/7zip/7zip/25.01).
$sevenZipMsiUrl = 'https://www.7-zip.org/a/7z2501-x64.msi'
$sevenZipMsiSha256 = 'e7eb0b7ed5efa4e087b7b17f191797f7af5b7f442d1290c66f3a21777005ef57'

$repoRoot = Split-Path $PSScriptRoot -Parent
$listFile = Join-Path $PSScriptRoot 'qemu-win64-files.txt'
$qemuDir = Join-Path $repoRoot 'vendor\qemu'
$bundleDir = Join-Path $qemuDir 'win-x64'
$cacheDir = Join-Path $qemuDir '.cache'
$stampFile = Join-Path $qemuDir '.stamp'

# ---- pins + file list (single source of truth: tools/qemu-win64-files.txt) ----
$lines = Get-Content $listFile
$pins = @{}
foreach ($l in ($lines | Where-Object { $_ -match '^#pin:\s*(\w+)=(\S+)' })) {
    if ($l -match '^#pin:\s*(\w+)=(\S+)') { $pins[$Matches[1]] = $Matches[2] }
}
$files = $lines | Where-Object { $_ -and $_ -notmatch '^\s*#' }
if (-not $pins.url -or -not $pins.sha512 -or $files.Count -eq 0) {
    throw "Malformed $listFile : need #pin: url=, #pin: sha512= and at least one file entry."
}
$installerName = ($pins.url -split '/')[-1]

# ---- no-op when current ----
$current = (Test-Path $stampFile) -and ((Get-Content $stampFile -TotalCount 1) -eq $installerName)
if ($current) {
    foreach ($f in $files) {
        if (-not (Test-Path (Join-Path $bundleDir $f))) { $current = $false; break }
    }
}
if ($current -and -not $Force) {
    Write-Host "vendor/qemu/win-x64 is already current ($installerName); nothing to do."
    exit 0
}

New-Item -ItemType Directory -Force $cacheDir | Out-Null

# ---- download + verify ----
$installer = Join-Path $cacheDir $installerName
$haveInstaller = (Test-Path $installer) -and -not $Force
if ($haveInstaller) {
    $haveInstaller = (Get-FileHash $installer -Algorithm SHA512).Hash.ToLowerInvariant() -eq $pins.sha512
}
if (-not $haveInstaller) {
    Write-Host "Downloading $($pins.url) ..."
    & curl.exe -sSL --fail --retry 3 -o $installer $pins.url
    if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit $LASTEXITCODE): $($pins.url)" }
}
$actual = (Get-FileHash $installer -Algorithm SHA512).Hash.ToLowerInvariant()
if ($actual -ne $pins.sha512) {
    Remove-Item $installer
    throw "sha512 mismatch for $installerName : expected $($pins.sha512), got $actual. Deleted the download."
}
Write-Host "Verified $installerName (sha512 ok)."

# ---- locate or bootstrap 7-Zip ----
$sevenZip = $null
$cmd = Get-Command 7z -ErrorAction SilentlyContinue
if ($cmd) { $sevenZip = $cmd.Source }
if (-not $sevenZip) {
    foreach ($p in @("$env:ProgramFiles\7-Zip\7z.exe", "$cacheDir\7zip\Files\7-Zip\7z.exe")) {
        if (Test-Path $p) { $sevenZip = $p; break }
    }
}
if (-not $sevenZip) {
    Write-Host 'No 7-Zip found - bootstrapping a portable copy (administrative MSI extract, no install)...'
    $msi = Join-Path $cacheDir ($sevenZipMsiUrl -split '/')[-1]
    if (-not (Test-Path $msi)) {
        & curl.exe -sSL --fail --retry 3 -o $msi $sevenZipMsiUrl
        if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit $LASTEXITCODE): $sevenZipMsiUrl" }
    }
    $msiHash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($msiHash -ne $sevenZipMsiSha256) {
        Remove-Item $msi
        throw "sha256 mismatch for 7-Zip MSI: expected $sevenZipMsiSha256, got $msiHash. Deleted the download."
    }
    $proc = Start-Process msiexec -ArgumentList "/a `"$msi`" /qn TARGETDIR=`"$cacheDir\7zip`"" -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "msiexec administrative extract failed (exit $($proc.ExitCode))." }
    $sevenZip = "$cacheDir\7zip\Files\7-Zip\7z.exe"
    if (-not (Test-Path $sevenZip)) { throw "7-Zip bootstrap did not produce $sevenZip." }
}

# ---- extract ----
$extractDir = Join-Path $cacheDir 'extract'
$extractStamp = Join-Path $cacheDir '.extract-stamp'
$haveExtract = (Test-Path $extractStamp) -and ((Get-Content $extractStamp -TotalCount 1) -eq $installerName) -and -not $Force
if (-not $haveExtract) {
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force -Confirm:$false }
    Write-Host "Extracting $installerName (this takes a minute)..."
    & $sevenZip x $installer "-o$extractDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7z extraction failed (exit $LASTEXITCODE)." }
    Set-Content $extractStamp $installerName
}

# ---- populate vendor/qemu/win-x64 from the list ----
if (Test-Path $bundleDir) { Remove-Item $bundleDir -Recurse -Force -Confirm:$false }
foreach ($f in $files) {
    $src = Join-Path $extractDir ($f -replace '/', '\')
    if (-not (Test-Path $src)) { throw "Listed file missing from the extracted installer: $f (stale tools/qemu-win64-files.txt?)" }
    $dst = Join-Path $bundleDir ($f -replace '/', '\')
    New-Item -ItemType Directory -Force (Split-Path $dst -Parent) | Out-Null
    Copy-Item $src $dst
}
Set-Content $stampFile $installerName

$size = (Get-ChildItem $bundleDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Populated vendor/qemu/win-x64: {0} files, {1:N0} MB ({2})." -f $files.Count, $size, $installerName)
