# Native stb Oracle

Builds a shared library (`stb_oracle`) from the pinned upstream `nothings/stb` and
`nothings/stb_image_resize` commits. The C headers are fetched at build time
and verified by SHA-256 — they are never vendored into this repository.

## Build

```bash
cmake -S native -B native/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/build --config Release
```

The resulting shared library is written to `native/build/out/` (`libstb_oracle.so`,
`libstb_oracle.dylib`, or `stb_oracle.dll`). `Stb.Managed.Oracle` locates it automatically via
`STB_ORACLE_PATH` or `native/build/out/`.
