# Third-Party Software Notices and Licenses

PDF Viewer opens, draws, searches, edits and saves PDF documents with its own engine
(PdfEngine.Vector, part of this product and under its MIT licence). It contains the third-party
software listed below and nothing else from outside this project.

## MIT License
The MIT-licensed components below are distributed under these terms, with the copyright notice
given in each component's section.

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Vortice.Windows (Vortice.Direct2D1, Vortice.Direct3D11, Vortice.DXGI, Vortice.DirectX, Vortice.Mathematics) and SharpGen.Runtime (SharpGen.Runtime, SharpGen.Runtime.COM)
Version: 3.8.3 (Vortice.Mathematics 2.1.0; SharpGen.Runtime 2.4.2-beta) · Source: https://github.com/amerkoleci/Vortice.Windows · License: MIT (Copyright (c) Amer Koleci and contributors; SharpGen.Runtime: Copyright (c) SharpGenTools contributors). The MIT license text is reproduced above.

## Adobe cmap-resources (predefined CJK CMaps)
Commit: f5cf3bca7fdfeaceb77aa82847e974f2306c20b4 · Source: https://github.com/adobe-type-tools/cmap-resources · License: BSD-3-Clause. Shipped as `PdfEngine.Vector` embedded resource `PredefinedCMaps.zip` (59 CMaps of ISO 32000-2 Table 116).

```text
Copyright 1990-2023 Adobe. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.

Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

Neither the name of Adobe nor the names of its contributors may be
used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

```

## CoreJ2K (JPEG 2000 decoder)
Version: 2.4.1.108 · Source: https://github.com/cinderblocks/CoreJ2K · License: BSD-3-Clause (includes the JJ2000 copyright notice below).

```text
Copyright (c) 2007-2016, CSJ2K contributors.
Copyright (c) 2024-2026, Sjofn LLC.

Redistribution and use in source and binary forms, with or without modification, 
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, 
   this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, 
   this list of conditions and the following disclaimer in the documentation 
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors 
   may be used to endorse or promote products derived from this software without 
   specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS “AS IS” AND 
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED 
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, 
INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, 
OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
POSSIBILITY OF SUCH DAMAGE.

JJ2000 COPYRIGHT:

This software module was originally developed by Raphaël Grosbois and
Diego Santa Cruz (Swiss Federal Institute of Technology-EPFL); Joel
Askelöf (Ericsson Radio Systems AB); and Bertrand Berthelot, David
Bouchard, Félix Henry, Gerard Mozelle and Patrice Onno (Canon Research
Centre France S.A) in the course of development of the JPEG2000
standard as specified by ISO/IEC 15444 (JPEG 2000 Standard). This
software module is an implementation of a part of the JPEG 2000
Standard. Swiss Federal Institute of Technology-EPFL, Ericsson Radio
Systems AB and Canon Research Centre France S.A (collectively JJ2000
Partners) agree not to assert against ISO/IEC and users of the JPEG
2000 Standard (Users) any of their rights under the copyright, not
including other intellectual property rights, for this software module
with respect to the usage by ISO/IEC and Users of this software module
or modifications thereof for use in hardware or software products
claiming conformance to the JPEG 2000 Standard. Those intending to use
this software module in hardware or software products are advised that
their use may infringe existing patents. The original developers of
this software module, JJ2000 Partners and ISO/IEC assume no liability
for use of this software module or modifications thereof. No license
or right to this software module is granted for non JPEG 2000 Standard
conforming products. JJ2000 Partners have full right to use this
software module for his/her own purpose, assign or donate this
software module to any third party and to inhibit third parties from
using this software module for non JPEG 2000 Standard conforming
products. This copyright notice must be included in all copies or
derivative works of this software module.

Copyright (c) 1999/2000 JJ2000 Partners.

```

## SkiaSharp and Skia (web demo and Android app only)
SkiaSharp, SkiaSharp.NativeAssets.WebAssembly and SkiaSharp.NativeAssets.Android · Version: 3.116.1 · Source: https://github.com/mono/SkiaSharp · License: MIT (Copyright (c) 2015-2016 Xamarin, Inc., Copyright (c) 2017-2018 Microsoft Corporation). The MIT license text is reproduced above.

The native libraries contain Skia (https://skia.org), used under the BSD 3-Clause License:

```
Copyright (c) 2011 Google Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

  * Redistributions of source code must retain the above copyright notice, this list of
    conditions and the following disclaimer.
  * Redistributions in binary form must reproduce the above copyright notice, this list of
    conditions and the following disclaimer in the documentation and/or other materials provided
    with the distribution.
  * Neither the name of the copyright holder nor the names of its contributors may be used to
    endorse or promote products derived from this software without specific prior written
    permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

The Windows application does not include SkiaSharp or Skia.

## Liberation Fonts (web demo only)
Version: 2.1.5 · Source: https://github.com/liberationfonts/liberation-fonts/releases/tag/2.1.5 (`liberation-fonts-ttf-2.1.5.tar.gz`, SHA-256 `7191c669bf38899f73a2094ed00f7b800553364f90e2637010a69c0e268f25d0`) · License: SIL Open Font License 1.1. Shipped unmodified as static files of the web demo in `src/PdfViewer.Web/wwwroot/fonts`, with the licence text from the archive as `OFL.txt` next to them. The Windows application and the Android app do not include them.

Copyright lines, as in the name table (ID 0) of every file:

```text
Digitized data copyright (c) 2010 Google Corporation.
Copyright (c) 2012 Red Hat, Inc.
```

The archive's licence adds the reserved font names: Arimo, Tinos and Cousine (Google Corporation) and Liberation (Red Hat, Inc.).

| File | SHA-256 |
|---|---|
| LiberationSans-Regular.ttf | `76d04c18ea243f426b7de1f3ad208e927008f961dc5945e5aad352d0dfde8ee8` |
| LiberationSans-Bold.ttf | `788abee4c806d660e8aee46689dd8540cd4bb98da03dcc9d171ce3efd99a9173` |
| LiberationSans-Italic.ttf | `e5bae5c4cde31f22142753855f4f8fb86da6ff39955ed3c0a11248b0d16948b0` |
| LiberationSans-BoldItalic.ttf | `698da70fc191cc5f33ad4d6d3fe830fe4624b898ea2e3169955928b7c491f1ee` |
| LiberationSerif-Regular.ttf | `058ea80864aef09a23f45cbec2bb5400bc3dfbdea01c3f10538a21fcb497fb74` |
| LiberationSerif-Bold.ttf | `d754ba427cfe0bca54ae052384baa8f842da5bd6550ad4da024ac441e7a7d5ce` |
| LiberationSerif-Italic.ttf | `0e3dea9f8d613e006ccfa62201f33e265d19167bd0907725c3e145368b04fc2e` |
| LiberationSerif-BoldItalic.ttf | `f17db8af71e24d2066b587546021d4f0b296be389512b658dec3c09affeb11a7` |
| LiberationMono-Regular.ttf | `f2b83c763e8afd21709333370bed4774337fae82267937e2b5aea7e2fbd922c1` |
| LiberationMono-Bold.ttf | `bd62a0672d0b9b6710b01df434c80ad54fa5f0835207eb7b17b7a761463067bb` |
| LiberationMono-Italic.ttf | `605c01c711b44480a7508d349dfbf3264e81fa43d69e61cfa7d10b86e764c4d1` |
| LiberationMono-BoldItalic.ttf | `79451f3c09fe25116098853b7a2ca6e2436220ccc11af022979adbcf195be130` |
| OFL.txt (the archive's `LICENSE`) | `93fed46019c38bbe566b479d22148e2e8a1e85ada614accb0211c37b2c61c19b` |

## CommunityToolkit.Mvvm
Version: 8.4.0 · Source: https://github.com/CommunityToolkit/dotnet · License: MIT (Copyright (c) .NET Foundation and Contributors). The MIT license text is reproduced above.

## Microsoft .NET libraries (System.Security.Cryptography.Pkcs, System.Speech, System.Text.Json)
Version: 9.0.x · Source: https://github.com/dotnet/runtime · License: MIT (Copyright (c) .NET Foundation and Contributors). The MIT license text is reproduced above. System.Speech ships as an optional download (Read Aloud), not in the installer.
