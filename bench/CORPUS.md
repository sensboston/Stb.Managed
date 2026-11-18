# Benchmark corpus

The inputs named in `bench/cases.tsv` are published as `bench-corpus.zip` on the
[bench-corpus-1](https://github.com/sensboston/Stb.Managed/releases/tag/bench-corpus-1) release page and
fetched with `dotnet run --project tools/Stb.Managed.Tools -- fetch-bench-corpus`, which checks the archive
and every file against SHA-256. `photo_24mp.jpg` is one of the two CC0 photographs below, unchanged; the
other images were derived from them once (scaled, re-encoded with libjpeg/zlib through Pillow, converted
to the other formats). The two fonts come from the test corpus: Roboto Medium (Apache License 2.0) and
Source Code Pro (SIL Open Font License 1.1).

| Source | Author | License | SHA-1 |
|---|---|---|---|
| [San Carlos de la Barra Fort, Isla de San Carlos, Estado Zulia, Venezuela.jpg](https://commons.wikimedia.org/wiki/File:San_Carlos_de_la_Barra_Fort,_Isla_de_San_Carlos,_Estado_Zulia,_Venezuela.jpg) | Wilfredor | CC0 | `b56a1fb42565adc726b9670c5bf7c1ccb87b5702` |
| [Teatro de Santa Isabel, Recife, Pernambuco, Brasil.jpg](https://commons.wikimedia.org/wiki/File:Teatro_de_Santa_Isabel,_Recife,_Pernambuco,_Brasil.jpg) | Wilfredor | CC0 | `b902cbd16911bd2a95f1aa570ed85af609781b93` |

SHA-256 of the corpus files (also in `manifest.json` inside the corpus):

| File | SHA-256 |
|---|---|
| `anim.gif` | `bb77e42a7f39ca853351abfcbba477abe5258b839e2c14b47c5f21c1b9291416` |
| `gray.jpg` | `6704562714ff96968695f2ce7f0707ef30617eaa058e48878b6629d6ff360f42` |
| `image.hdr` | `d576e08844f834ec77ace4c3dac9abb64e1bffe71eb5026215709f4b96b4a36b` |
| `image.psd` | `61d36ad0338abffc3c5c0120ca62a00cbe3b2bdc0f3b96d7b06eadc18b5a82dd` |
| `palette.png` | `b39c2aa204e0156365ab94ad6a23be4f2f24f7e8d48302078759e98d4e5fbaaa` |
| `photo.bmp` | `ffcd6723f442ad6252d20c8356281baae844e87a26f4a95d2e5074060f3268dd` |
| `photo_12mp.jpg` | `3bad231ef8b52b447113d044bc54d79e7d007340f5734ed2c23b2f05978d5001` |
| `photo_16.png` | `bc4e06c20243b054cc8c5f2e8f5bcf71cf862a55aea854df921b3f6648bfd50a` |
| `photo_24mp.jpg` | `36426693ca01856fee4987fed217fa035b96a9732c6c745a67216fd8db5c1878` |
| `photo_444.jpg` | `eb97148a7b2fa7771defdc7a044a6c95b4860acdd808f6fe9a95d0c0473f35e2` |
| `photo_prog.jpg` | `c504a8612518ab977d0f9e590895fd0d9915b10d5b508eac94717d2263ec8cc8` |
| `photo_rgb.png` | `5b83f5ee204e3babb08c12e3da1e4f5877f7f1111cc7d4a52c051cf10de02dfa` |
| `photo_rle.tga` | `4c765e2d8e10d1042184377ee990ba4ef6948029d5987104bea30f7aa5e320c5` |
| `Roboto-Medium.ttf` | `8559132c89ad51d8a2ba5b171887a44a7ba93776e205f553573de228e64b45f8` |
| `screenshot.png` | `390395eee40beaed64a7c61308b95d45d452e701ab58c8506f9eb470daef6ce5` |
| `SourceCodePro-Regular.otf` | `9f9664e2edf6f045c11e774f9bd0be6993971f2544a39061a5ce478b96b051f8` |
| `web_1080p.jpg` | `d9b0cc2b2568178686b3e8d239443b945219041e92ec87227888584da58fc58f` |
