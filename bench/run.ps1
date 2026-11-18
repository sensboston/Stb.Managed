# Builds both harnesses, runs the benchmark passes and archives the results.
# Usage: bench/run.ps1 [-Corpus DIR] [-Cases FILE] [-Filter TEXT] [-MinTimeMs N] [-Framework TFM] [-SkipGpu]
param(
    [string]$Corpus = "bench/corpus",
    [string]$Cases = "bench/cases.tsv",
    [string]$Filter = "",
    [double]$MinTimeMs = 1000,
    [string]$Framework = "net10.0",
    [switch]$SkipGpu,
    [string]$NativeExe = "",
    [string]$ManagedExe = ""
)

$ErrorActionPreference = "Stop"
$BenchRoot = $PSScriptRoot
$RepoRoot = Split-Path -Parent $BenchRoot
$Invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Join-Under([string]$Base, [string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $Base $Path
}

function Require-Tool([string]$Name) {
    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' not found in PATH"
    }
}

function Read-ResultCsv([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    return @(Get-Content -LiteralPath $Path | Where-Object { $_ -notmatch '^#' } | ConvertFrom-Csv)
}

function To-Double([string]$Text) {
    $value = 0.0
    [void][double]::TryParse($Text, [System.Globalization.NumberStyles]::Float, $Invariant, [ref]$value)
    return $value
}

function Print-Summary([string]$Title, [string]$NativeCsv, [string]$ManagedCsv) {
    Write-Host ""
    Write-Host "=== $Title ==="
    if (-not (Test-Path -LiteralPath $NativeCsv) -or -not (Test-Path -LiteralPath $ManagedCsv)) {
        Write-Host "(missing CSV, skipped)"
        return
    }
    $native = @{}
    foreach ($row in (Read-ResultCsv $NativeCsv)) { $native[$row.case.Trim('"')] = $row }
    $managedRows = Read-ResultCsv $ManagedCsv
    if ($managedRows.Count -eq 0) { Write-Host "(no result rows)"; return }
    $format = "{0,-36} {1,12} {2,12} {3,9} {4,-8} {5}"
    Write-Host ($format -f "case", "native_ms", "managed_ms", "speedup", "checksum", "note")
    foreach ($m in $managedRows) {
        $name = $m.case.Trim('"')
        $n = $native[$name]
        $nStatus = if ($null -ne $n) { $n.status } else { "missing" }
        $mStatus = $m.status
        $nMedian = if ($null -ne $n) { To-Double $n.median_ms } else { 0.0 }
        $mMedian = To-Double $m.median_ms
        $speedup = "n/a"
        if ($nStatus -eq "ok" -and $mStatus -eq "ok" -and $nMedian -gt 0 -and $mMedian -gt 0) {
            $speedup = ($nMedian / $mMedian).ToString("F2", $Invariant) + "x"
        }
        $checksum = "n/a"
        if ($null -ne $n -and $n.checksum -and $m.checksum) {
            $checksum = if ($n.checksum -eq $m.checksum) { "match" } else { "DIFFER" }
        }
        $note = ""
        if ($nStatus -ne "ok" -or $mStatus -ne "ok") { $note = "native=$nStatus managed=$mStatus" }
        $nColumn = if ($null -ne $n) { $n.median_ms } else { "-" }
        Write-Host ($format -f $name, $nColumn, $m.median_ms, $speedup, $checksum, $note)
    }
}

$CorpusPath = Join-Under $RepoRoot $Corpus
$CasesPath = Join-Under $RepoRoot $Cases
$NativeDir = Join-Path $BenchRoot "native"
$NativeBuild = Join-Path $NativeDir "build"
$ManagedProject = Join-Path $BenchRoot "Stb.Managed.Bench"
$ResultsRoot = Join-Path $BenchRoot "results"

if (-not (Test-Path -LiteralPath $CorpusPath -PathType Container)) { throw "Corpus directory not found: $CorpusPath" }
if (-not (Test-Path -LiteralPath $CasesPath -PathType Leaf)) { throw "Cases file not found: $CasesPath" }
$Prebuilt = ($NativeExe -ne "") -and ($ManagedExe -ne "")
if (-not $Prebuilt) {
    Require-Tool "cmake"
    Require-Tool "dotnet"
}

$Machine = $env:COMPUTERNAME
if ([string]::IsNullOrWhiteSpace($Machine)) { $Machine = [System.Net.Dns]::GetHostName() }
$Machine = $Machine -replace '[^A-Za-z0-9._-]', '_'
$ResultsDir = Join-Path $ResultsRoot $Machine
if (Test-Path -LiteralPath $ResultsDir) { Remove-Item -LiteralPath $ResultsDir -Recurse -Force }
New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null

$NativeOut = Join-Path $ResultsDir "native.csv"
$Managed1T = Join-Path $ResultsDir "managed_1t.csv"
$Managed0T = Join-Path $ResultsDir "managed_0t.csv"
$ManagedDefault = Join-Path $ResultsDir "managed_default.csv"
$SysInfo = Join-Path $ResultsDir "sysinfo.json"

Write-Host "Corpus:    $CorpusPath"
Write-Host "Cases:     $CasesPath"
Write-Host "Framework: $Framework"
Write-Host "Results:   $ResultsDir"
Write-Host ""

if (-not $Prebuilt) {
    $CmakeArgs = @("-S", $NativeDir, "-B", $NativeBuild, "-DCMAKE_BUILD_TYPE=Release")
    if ((Get-Command clang -ErrorAction SilentlyContinue) -and (Get-Command ninja -ErrorAction SilentlyContinue)) {
        Write-Host "Using Ninja + clang"
        $CmakeArgs += @("-G", "Ninja", "-DCMAKE_C_COMPILER=clang")
    }

    Write-Host "=== native build ==="
    & cmake @CmakeArgs
    if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
    & cmake --build $NativeBuild --config Release --parallel
    if ($LASTEXITCODE -ne 0) { throw "native build failed" }

    $OutDir = Join-Path $NativeBuild "out"
    if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) { throw "Native build output directory not found: $OutDir" }
    foreach ($candidate in @(
            (Join-Path $OutDir "stb_bench_native.exe"),
            (Join-Path $OutDir "stb_bench_native"),
            (Join-Path (Join-Path $OutDir "Release") "stb_bench_native.exe"),
            (Join-Path (Join-Path $OutDir "Release") "stb_bench_native"))) {
        if (Test-Path -LiteralPath $candidate) { $NativeExe = $candidate; break }
    }
    if (-not $NativeExe) { throw "Native benchmark binary not found under $OutDir" }
    Write-Host "Native binary: $NativeExe"
    Write-Host ""

    Write-Host "=== managed build ==="
    & dotnet build $ManagedProject -c Release --framework $Framework
    if ($LASTEXITCODE -ne 0) { throw "managed build failed" }
    $ManagedExe = Join-Path (Join-Path (Join-Path (Join-Path $ManagedProject "bin") "Release") $Framework) "Stb.Managed.Bench.dll"
    if (-not (Test-Path -LiteralPath $ManagedExe)) { throw "Managed binary not found: $ManagedExe" }
    Write-Host ""

}

# A .dll runs on the installed runtime; anything else is a self-contained executable.
function Invoke-Managed {
    if ($ManagedExe.EndsWith(".dll")) { & dotnet $ManagedExe @args } else { & $ManagedExe @args }
}

$MinTimeArg = $MinTimeMs.ToString($Invariant)
$FilterArgs = @()
if ($Filter -ne "") { $FilterArgs = @("--filter", $Filter) }
$Failures = 0

Write-Host "=== native run ==="
& $NativeExe --corpus $CorpusPath --cases $CasesPath --out $NativeOut --min-time-ms $MinTimeArg @FilterArgs
if ($LASTEXITCODE -ne 0) { $Failures++; Write-Warning "native run reported failures" }

Write-Host "=== managed run (threads=1) ==="
Invoke-Managed --corpus $CorpusPath --cases $CasesPath --out $Managed1T --min-time-ms $MinTimeArg @FilterArgs --threads 1
if ($LASTEXITCODE -ne 0) { $Failures++; Write-Warning "managed threads=1 run reported failures" }

Write-Host "=== managed run (threads=0) ==="
Invoke-Managed --corpus $CorpusPath --cases $CasesPath --out $Managed0T --min-time-ms $MinTimeArg @FilterArgs --threads 0
if ($LASTEXITCODE -ne 0) { $Failures++; Write-Warning "managed threads=0 run reported failures" }

Invoke-Managed --sysinfo $SysInfo
if ($LASTEXITCODE -ne 0) { $Failures++; Write-Warning "sysinfo failed" }
if ($SkipGpu) { $env:STB_GPU_MODE = "cpu"; Write-Host "GPU disabled by -SkipGpu" }
Write-Host "=== managed run (default: all cores, GPU auto) ==="
Invoke-Managed --corpus $CorpusPath --cases $CasesPath --out $ManagedDefault --min-time-ms $MinTimeArg @FilterArgs --default
if ($LASTEXITCODE -ne 0) { $Failures++; Write-Warning "managed default run reported failures" }
Print-Summary "summary: native vs managed threads=1" $NativeOut $Managed1T
Print-Summary "summary: native vs managed default" $NativeOut $ManagedDefault

$ZipPath = Join-Path $ResultsRoot ($Machine + ".zip")
if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
if ((Get-ChildItem -LiteralPath $ResultsDir -Recurse -File | Measure-Object).Count -gt 0) {
    Compress-Archive -Path $ResultsDir -DestinationPath $ZipPath
    Write-Host ""
    Write-Host "Results: $ResultsDir"
    Write-Host "Archive: $ZipPath"
} else {
    Write-Warning "No results produced; archive skipped"
}

if ($Failures -gt 0) { exit 1 }
exit 0
