# Reproducing the benchmark

Every number in [docs/benchmarks.md](../docs/benchmarks.md) and in the chart on the front page comes
from the steps below. Run them on your own machine and compare: the stand builds the original stb C
code and Stb.Managed side by side and times the same 34 operations on the same files.

## 1. Install

| | Windows | Linux | macOS |
|---|---|---|---|
| .NET 10 SDK | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) | same | same |
| CMake 3.20+ | `winget install Kitware.CMake` | `sudo apt install cmake` | `brew install cmake` |
| C compiler | Visual Studio 2022 (C++ workload), or LLVM clang + Ninja | `sudo apt install gcc` (or clang) | `xcode-select --install` |
| Shell | Windows PowerShell (built in) | bash | bash |

With clang or gcc the native code is built with `-O3 -march=native`; with MSVC with `/O2 /arch:AVX2`.
The exact compiler and flags are recorded in the results and shown in the report.

## 2. Get the code and the inputs

```
git clone https://github.com/sensboston/Stb.Managed
cd Stb.Managed
dotnet run --project tools/Stb.Managed.Tools -c Release -- fetch-bench-corpus
```

The last command downloads the benchmark inputs (about 90 MB, derived from two CC0 photographs,
see [CORPUS.md](CORPUS.md)) and two fonts, and checks every file against its SHA-256.

## 3. Run

```
powershell -ExecutionPolicy Bypass -File bench/run.ps1   # Windows
bash bench/run.sh                                        # Linux, macOS
```

The script builds both sides, then runs, one after another: native stb, Stb.Managed on one thread,
on all cores, and in its default configuration (all cores plus a GPU, if a CUDA or OpenCL device is
found). `-SkipGpu` / `--skip-gpu` leaves the GPU out. A full run takes 15 to 40 minutes.

For stable numbers: plug in a laptop and select the high-performance power plan, close browsers
and other busy programs, and do not use the machine during the run. Each case is warmed up first and
then repeated until at least 10 runs and 1 second; the median is reported.

## 4. Read the results

The script prints a summary (speedup = native time / managed time, above 1.00 the managed code is
faster) and writes `bench/results/<HOST>/` plus a zip of it:

| File | Contents |
|---|---|
| `native.csv` | native stb timings, compiler and flags |
| `managed_1t.csv`, `managed_0t.csv`, `managed_default.csv` | Stb.Managed: 1 thread, all cores, default |
| `sysinfo.json` | CPU, instruction sets, OS, .NET, GPU |

Every row also carries an FNV-1a-64 checksum of the output, so you can see that both sides produced
the same result (see the notes in [docs/benchmarks.md](../docs/benchmarks.md) for the expected exceptions).

To build the report and the charts from one or more machines:

```
dotnet run --project tools/Stb.Managed.Tools -c Release -- report bench/results/<HOST> [more results...]
```

It rewrites `docs/benchmarks.md` and `docs/images/*.svg`.

## 5. Share

Results from other machines are welcome: open an issue and attach the zip from `bench/results/`.
