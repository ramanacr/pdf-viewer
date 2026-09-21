# PDF Core Implementation Plan

## Layer 1 — byte source

Provide bounded random access over:
- file;
- memory;
- future stream/range source.

Requirements:
- 64-bit offsets;
- cancellation where I/O can block;
- no unchecked cast from PDF integer to allocation size;
- maximum document size policy configurable separately from process address space.

## Layer 2 — lexer

Tokens:
- integers/reals;
- names;
- literal strings;
- hex strings;
- arrays;
- dictionaries;
- booleans/null;
- indirect refs;
- stream boundaries;
- keywords/operators.

Test malformed escapes, unterminated objects, extreme numeric values, deeply nested arrays/dictionaries, and whitespace/comment edge cases.

## Layer 3 — xref/object resolution

Order:
1. classic xref tables/trailers;
2. incremental update chains;
3. xref streams;
4. compressed object streams;
5. hybrid-reference files;
6. controlled recovery for damaged xrefs.

Protect against:
- cyclic `/Prev`;
- cyclic indirect references;
- huge `/Size`;
- duplicate/redefined objects;
- out-of-file offsets;
- decompression bombs.

## Layer 4 — stream filters

Implement incrementally:
- FlateDecode first;
- ASCIIHex/ASCII85;
- RunLength;
- LZW if compatibility requires;
- image-specific filters/decoders via bounded libraries/Windows codecs where appropriate.

Do not implement cryptography or image codecs from scratch merely for dependency purity. Security-critical primitives should use vetted platform/library implementations.

## Layer 5 — document/page model

Resolve:
- catalog;
- page tree;
- inherited page attributes;
- MediaBox/CropBox/Rotate;
- Resources;
- Contents arrays/streams;
- XObjects;
- font/image/color-space references.

Cycle detection and depth limits are mandatory.

## Layer 6 — content interpreter

Initial operator families:

### Graphics state
`q Q cm w J j M d ri i gs`

### Paths
`m l c v y h re`

### Painting/clipping
`S s f F f* B B* b b* n W W*`

### Text
`BT ET Tc Tw Tz TL Tf Tr Ts Td TD Tm T* Tj TJ ' "`

### XObjects
`Do`

### Color
Start with DeviceGray/RGB/CMYK and basic color operators; expand systematically.

### Marked content
Parse safely early even if semantic handling comes later so unsupported sequences do not destabilize the operator parser.

## Fonts roadmap

1. standard/simple fonts and embedded TrueType/OpenType path;
2. Type1/CFF;
3. Type0/CID fonts;
4. CMaps and ToUnicode;
5. vertical writing;
6. Type3;
7. substitution rules for missing fonts.

Font rendering fidelity is a release-critical area. Never substitute Unicode layout for original glyph positioning.

## Images roadmap

Support:
- image XObjects;
- inline images;
- image masks;
- decode arrays;
- interpolation;
- DeviceGray/RGB/CMYK;
- indexed;
- ICCBased later;
- JPX/JBIG2 according to decoder/security decision.

## Transparency roadmap

Introduce only after basic vector fidelity:
- ExtGState alpha;
- blend modes;
- transparency groups;
- soft masks;
- isolated/knockout groups.

These are prime candidates for localized intermediate surfaces.

## Encryption

Keep PDFium for encrypted-document handling until an explicit encryption milestone. When migrated, implement Standard Security Handler versions required by the supported PDF versions using platform cryptography. Password handling must avoid logging and minimize retention.

## Saving/editing

Do not couple the first reader/rendering core to a writer. Existing PDFium-backed save/edit services continue. Later create a distinct writer/incremental-update subsystem.
