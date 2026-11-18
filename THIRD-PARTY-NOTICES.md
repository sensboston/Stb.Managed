# Third-party notices

## stb

Stb.Managed is a C# translation of the following stb libraries by Sean Barrett and contributors
(https://github.com/nothings/stb, https://github.com/nothings/stb_image_resize):
`stb_image.h`, `stb_image_write.h`, `stb_image_resize2.h`, `stb_truetype.h`, `stb_rect_pack.h`,
`stb_dxt.h`, `stb_perlin.h`. They are available under either the MIT license or the Unlicense
(public domain), at the user's choice; Stb.Managed uses them under the MIT license:

> Copyright (c) 2017 Sean Barrett
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute,
> sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or
> substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
> NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
> OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
> CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## ILGPU (optional `Stb.Managed.Gpu` package only)

`Stb.Managed.Gpu` depends on the ILGPU and ILGPU.Algorithms NuGet packages
(https://github.com/m4rs-mt/ILGPU), distributed under the University of Illinois/NCSA Open Source
License. They are referenced as package dependencies and not included in this repository.

## Benchmark corpus

The benchmark inputs are generated from two CC0 photographs on Wikimedia Commons and two fonts
(Roboto, Apache License 2.0; Source Code Pro, SIL Open Font License 1.1); see `bench/CORPUS.md`.
None of them is included in the packages.
