<#
.SYNOPSIS
    Times the search before vs after killer-move ordering.

.DESCRIPTION
    Compiles two standalone bench binaries from the current engine source — one with killer
    ordering disabled (the pre-killer baseline, via /DBENCH_DISABLE_KILLERS) and one with it
    on — then runs each benchmark position in its own process (cold transposition table) and
    reports wall-clock search time for both, plus the speedup. Timing is measured inside the
    engine around engine_best_move, so process startup isn't counted. Each position is run
    -Reps times and the fastest run is kept, to cut scheduling noise.

.PARAMETER Skill
    Search difficulty / max depth. Default 8.

.PARAMETER Positions
    Which position indices to run (0=kiwipete, 1=ruy, 2=sicilian). Default: all.

.PARAMETER Reps
    Runs per position per variant; the minimum time is reported. Default 3.

.PARAMETER ShowDepths
    Also print every per-depth line the engine emits.

.EXAMPLE
    .\bench.ps1
    .\bench.ps1 -Skill 9 -Reps 5 -Positions 0,2
#>
[CmdletBinding()]
param(
    [int]$Skill = 8,
    [int[]]$Positions,
    [int]$Reps = 3,
    [switch]$ShowDepths
)

$ErrorActionPreference = 'Stop'

$root  = $PSScriptRoot
$src   = Join-Path $root 'src'
$inc   = Join-Path $root 'include'
$build = Join-Path $root 'build'
if (-not (Test-Path $build)) { New-Item -ItemType Directory -Path $build | Out-Null }

# --- enter a VS dev shell so cl is on PATH ---
$devShell = "D:\Program Files\Visual Studio 2026\Common7\Tools\Launch-VsDevShell.ps1"
if (-not (Test-Path $devShell)) { throw "VS dev shell not found at $devShell" }
& $devShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null

$engineSources = 'chess_engine.cpp','bitboard.cpp','zobrist.cpp','position.cpp','movegen.cpp','uci.cpp' |
    ForEach-Object { Join-Path $src $_ }
$benchMain = Join-Path $root 'test\bench_main.cpp'

function Build-Variant([string]$exe, [string[]]$extraDefs) {
    $clArgs = @('/nologo','/std:c++17','/O2','/EHsc','/arch:AVX2','/DCHESS_ENGINE_BUILD','/DNDEBUG') +
              $extraDefs + @("/I$inc","/I$src", $benchMain) + $engineSources + @("/Fe:$exe", "/Fo:$build\")
    & cl @clArgs | Out-Null
    if (-not (Test-Path $exe)) { throw "compile failed: $exe" }
}

$exeBefore = Join-Path $build 'bench_before.exe'   # killers disabled (pre-killer baseline)
$exeAfter  = Join-Path $build 'bench_after.exe'    # killers enabled (current)

Write-Host "Compiling both variants..." -ForegroundColor Cyan
Build-Variant $exeBefore @('/DBENCH_DISABLE_KILLERS')
Build-Variant $exeAfter  @()

$nodePattern = '^depth (\d+) nodes (\d+) best (\S+) score (-?\d+)$'
$timePattern = 'time_ms=(\d+)'

function Run-One([string]$exe, [int]$pos) {
    $nodes = 0; $best = '?'; $bestMs = [long]::MaxValue
    for ($r = 0; $r -lt $Reps; $r++) {
        $err = [System.IO.Path]::GetTempFileName()
        $out = [System.IO.Path]::GetTempFileName()
        Start-Process -FilePath $exe -ArgumentList $pos,$Skill -NoNewWindow -Wait `
            -RedirectStandardError $err -RedirectStandardOutput $out | Out-Null
        $lines = Get-Content $err
        Remove-Item $err,$out -Force -ErrorAction SilentlyContinue
        if ($ShowDepths) { $lines | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray } }

        $deepest = $lines | Select-String $nodePattern | Select-Object -Last 1
        if ($deepest) { $nodes = [long]$deepest.Matches.Groups[2].Value; $best = $deepest.Matches.Groups[3].Value }
        $tm = $lines | Select-String $timePattern | Select-Object -Last 1
        if ($tm) { $ms = [long]$tm.Matches.Groups[1].Value; if ($ms -lt $bestMs) { $bestMs = $ms } }
    }
    return [pscustomobject]@{ Nodes = $nodes; Best = $best; Ms = $bestMs }
}

if (-not $Positions) { $Positions = 0,1,2 }
$names = @('kiwipete','ruy','sicilian')
$rows = @()

foreach ($i in $Positions) {
    $b = Run-One $exeBefore $i
    $a = Run-One $exeAfter  $i
    $speedup = if ($a.Ms -gt 0) { [math]::Round($b.Ms / $a.Ms, 2) } else { 0 }
    $rows += [pscustomobject]@{
        Position       = $names[$i]
        'ms (before)'  = $b.Ms
        'ms (after)'   = $a.Ms
        Speedup        = "${speedup}x"
        'nodes before' = $b.Nodes
        'nodes after'  = $a.Nodes
    }
}

Write-Host ""
Write-Host "Skill $Skill, best of $Reps runs   (before = no killers, after = killers)" -ForegroundColor Cyan
$rows | Format-Table -AutoSize

$tb = ($rows | Measure-Object -Property 'ms (before)' -Sum).Sum
$ta = ($rows | Measure-Object -Property 'ms (after)'  -Sum).Sum
$tot = if ($ta -gt 0) { [math]::Round($tb / $ta, 2) } else { 0 }
Write-Host ("TOTAL   {0} ms  ->  {1} ms   ({2}x faster)" -f $tb, $ta, $tot)
