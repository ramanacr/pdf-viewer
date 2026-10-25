# Third-party notices for the PdfEngine.Vector package

PdfEngine.Vector is MIT-licensed (Copyright © 2026 Ramana Reddy Chamakura). The package
contains one piece of third-party data, listed below.

## Adobe cmap-resources (predefined CJK CMaps)

Commit: f5cf3bca7fdfeaceb77aa82847e974f2306c20b4 · Source: https://github.com/adobe-type-tools/cmap-resources ·
License: BSD-3-Clause. Shipped inside `PdfEngine.Vector.dll` as the embedded resource
`PdfEngine.Vector.PredefinedCMaps.zip` (59 CMaps of ISO 32000-2 Table 116).

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

## Package dependencies

These come as separate NuGet packages, each with its own licence. They are not inside this package.

- CoreJ2K 2.4.1.108 (JPEG 2000 decoder): BSD-3-Clause, with the JJ2000 notice. https://github.com/cinderblocks/CoreJ2K
- System.Security.Cryptography.Pkcs 9.0.21: MIT. https://github.com/dotnet/runtime
