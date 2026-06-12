# Computes the transitive PE import-table closure of the given roots, restricted to DLLs
# shipped in the same directory. Used to derive tools/qemu-win64-files.txt (T11.1).
param(
    [Parameter(Mandatory)] [string] $Dir,
    [Parameter(Mandatory)] [string[]] $Roots
)

function Get-PeImports([string] $path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $peOff = [BitConverter]::ToInt32($bytes, 0x3C)
    if ([BitConverter]::ToUInt32($bytes, $peOff) -ne 0x4550) { throw "not PE: $path" }
    $coff = $peOff + 4
    $numSections = [BitConverter]::ToUInt16($bytes, $coff + 2)
    $optSize = [BitConverter]::ToUInt16($bytes, $coff + 16)
    $opt = $coff + 20
    $magic = [BitConverter]::ToUInt16($bytes, $opt)
    $ddOff = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }  # PE32+ vs PE32
    $importRva = [BitConverter]::ToUInt32($bytes, $ddOff + 8)         # data directory [1]
    if ($importRva -eq 0) { return @() }

    $sections = @()
    $sec = $opt + $optSize
    for ($i = 0; $i -lt $numSections; $i++) {
        $s = $sec + $i * 40
        $sections += [pscustomobject]@{
            VA   = [BitConverter]::ToUInt32($bytes, $s + 12)
            VSz  = [BitConverter]::ToUInt32($bytes, $s + 8)
            Raw  = [BitConverter]::ToUInt32($bytes, $s + 20)
        }
    }
    function RvaToOff([uint32] $rva) {
        foreach ($s in $sections) {
            if ($rva -ge $s.VA -and $rva -lt ($s.VA + $s.VSz)) { return $s.Raw + ($rva - $s.VA) }
        }
        throw "rva $rva unmapped"
    }

    $imports = @()
    $desc = RvaToOff $importRva
    while ($true) {
        $nameRva = [BitConverter]::ToUInt32($bytes, $desc + 12)
        if ($nameRva -eq 0) { break }
        $p = RvaToOff $nameRva
        $end = $p; while ($bytes[$end] -ne 0) { $end++ }
        $imports += [System.Text.Encoding]::ASCII.GetString($bytes, $p, $end - $p)
        $desc += 20
    }
    $imports
}

$shipped = @{}
Get-ChildItem $Dir -File -Filter *.dll | ForEach-Object { $shipped[$_.Name.ToLowerInvariant()] = $_.Name }

$needed = [System.Collections.Generic.HashSet[string]]::new()
$queue = [System.Collections.Generic.Queue[string]]::new()
$Roots | ForEach-Object { $queue.Enqueue($_) }
while ($queue.Count -gt 0) {
    $f = $queue.Dequeue()
    foreach ($imp in (Get-PeImports (Join-Path $Dir $f))) {
        $key = $imp.ToLowerInvariant()
        if ($shipped.ContainsKey($key) -and $needed.Add($shipped[$key])) { $queue.Enqueue($shipped[$key]) }
    }
}
$needed | Sort-Object
