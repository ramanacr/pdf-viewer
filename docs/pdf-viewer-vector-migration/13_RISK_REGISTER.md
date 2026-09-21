# Risk Register

| Risk | Impact | Mitigation |
|---|---|---|
| PDF format long tail underestimated | High | Hybrid fallback, corpus-driven priorities, staged gates |
| Font fidelity | High | Preserve glyph IDs/positions; differential geometry tests; dedicated font workstream |
| Transparency semantics | High | Local intermediate surfaces; defer until foundation stable |
| Parser vulnerabilities | Critical | Bounds, fuzzing, checked arithmetic, hostile-input design |
| Malformed PDFs differ from PDFium recovery | Medium/High | Explicit recovery policy; corpus; do not emulate unsafe quirks |
| WPF integration blocks true vector path | Medium | Isolate Windows backend; introduce vector-capable host incrementally |
| COM/device-loss complexity | Medium | device-independent/display-list separation; backend lifecycle tests |
| Full-page fallback hides lack of progress | High | fallback-area telemetry and reason enum |
| PDFium becomes permanent | High | exit gates, dependency direction, no Vector→Pdfium reference |
| Reimplementing codecs/crypto wastes effort | High | vetted platform/library dependencies allowed |
| Package size grows during transition | Medium | measure publish artifacts each milestone; temporary growth accepted only with budget |
| Regression in existing editing features | High | leave existing services on PDFium until separately migrated |
| Pixel-diff false positives | Medium | perceptual/structural diff; glyph/path checks |
| Scope expands to UI rewrite | High | explicit out-of-scope rule |
| Performance worse despite vector architecture | High | benchmark every hot-path milestone; cache/culling budgets |
| Legal issues with corpus PDFs | Medium | provenance manifest; no redistribution without rights |

## Stop conditions

Pause feature expansion and fix foundation when:
- parser crash/security defect appears;
- memory limit can be bypassed;
- fallback is unclassified;
- display-list semantics require repeated breaking changes;
- vector mode produces silent wrong output;
- benchmark regression exceeds agreed budget for two milestones without a mitigation plan.
