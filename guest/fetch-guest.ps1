# Fetch the pinned guest image release into guest/out/ (OS_PLAN.md T2.5).
# Windows twin of fetch-guest.sh — same pin (guest/GUEST_VERSION -> tag guest-v<N>),
# same no-op rule, same checksum verification.
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$out       = Join-Path $scriptDir 'out'
$repo      = if ($env:GATOS_GUEST_REPO) { $env:GATOS_GUEST_REPO } else { 'meow-sci/gatOS' }
$version   = (Get-Content (Join-Path $scriptDir 'GUEST_VERSION') -Raw).Trim()
$tag       = "guest-v$version"
$assets    = @('base.qcow2', 'vmlinuz-virt', 'initramfs-virt', 'manifest.toml',
               'id_ed25519', 'id_ed25519.pub', 'host_key_fingerprint.txt', 'sha256sums.txt')

$manifest = Join-Path $out 'manifest.toml'
if ((Test-Path $manifest) -and (Select-String -Path $manifest -Pattern "^guest_version = $version$" -Quiet)) {
    Write-Host "guest/out/ already at guest_version=$version — nothing to do"
    exit 0
}

$tmp = Join-Path $scriptDir (".fetch-tmp." + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host "==> fetching $tag from $repo"
    foreach ($a in $assets) {
        Invoke-WebRequest -Uri "https://github.com/$repo/releases/download/$tag/$a" `
                          -OutFile (Join-Path $tmp $a) -UseBasicParsing
    }

    Write-Host '==> verifying checksums'
    foreach ($line in Get-Content (Join-Path $tmp 'sha256sums.txt')) {
        if ($line -notmatch '^([0-9a-f]{64})\s+\*?(.+)$') { throw "bad sha256sums.txt line: $line" }
        $expected = $Matches[1]; $file = $Matches[2].Trim()
        $actual = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $tmp $file)).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "checksum mismatch for ${file}: expected $expected got $actual" }
    }

    New-Item -ItemType Directory -Force -Path $out | Out-Null
    foreach ($a in $assets) {
        Move-Item -Force (Join-Path $tmp $a) (Join-Path $out $a)
    }
    Write-Host "==> guest/out/ ready (guest_version=$version)"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
