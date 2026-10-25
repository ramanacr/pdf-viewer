# Current-State Gap Register

## Severity model

- Critical: blocks commercial claims or creates correctness/security risk.
- High: major product limitation.
- Medium: important polish or scale issue.
- Low: differentiation/polish.

| Area | Gap | Severity | Recommended action |
|---|---|---:|---|
| Core architecture | WPF types in PDF service | Critical | Extract UI-neutral rendering/document abstractions |
| Architecture | MainViewModel responsibility concentration | High | Split controllers/services |
| Engine | PDFium APIs exposed indirectly throughout application | High | Centralize all native calls behind engine adapter |
| Concurrency | Coarse document lock | High | Introduce session/queue model |
| Memory | Whole-file memory copy | High | Investigate streaming/file-backed provider |
| Forms | AcroForm editing/fill | Critical for Pro | Add form subsystem |
| Forms | XFA | High for enterprise compatibility | Evaluate PDFium capability and/or optional engine |
| Signatures | Digital signature creation | Critical for Pro | Add crypto/signature subsystem |
| Signatures | Signature validation/LTV/PAdES | Critical for enterprise | Add validation and trust subsystem |
| Redaction | Verified irreversible redaction | Critical for legal/enterprise | Implement separate redaction engine |
| Editing | Text/image/object editing | High | Add document object model |
| Pages | Merge/split/organize | High | Add page operation pipeline |
| OCR | Searchable scanned PDF | High | OCR provider abstraction |
| Compliance | PDF/A verification/conversion | High | Compliance service |
| Accessibility | Tagged PDF/tree inspection and remediation | High | Accessibility subsystem |
| Accessibility | Keyboard/screen-reader validation | High | Automated + manual accessibility test matrix |
| Security | PDF JavaScript policy model | High | Explicit allow/deny/sandbox strategy |
| Security | Malformed PDF fuzzing | Critical | Add native-engine fuzz/regression corpus |
| Security | Signed update packages | Critical | Code-signing and secure update verification |
| Enterprise | Centralized policy | High | Policy provider and configuration schema |
| Deployment | MSI/MSIX/Intune/enterprise deployment | High | Enterprise deployment pipeline |
| Localization | UI localization infrastructure | Medium | Resource-based localization |
| Reliability | Crash telemetry/diagnostics | High | Privacy-preserving opt-in diagnostics |
| Compatibility | Broad PDF corpus | Critical | Build corpus and golden-image tests |
| Performance | No formal performance budget | High | Define measurable budgets |
| Performance | Limited large-document stress testing | High | Add 1k/5k/10k-page and huge-file scenarios |
| UX | No workspace/tab/document-session architecture | Medium | Add multi-document workspace |
| Collaboration | No comments/review workflow | Medium | Annotation collaboration model |
| Commercial | No licensing/activation architecture | Critical for paid editions | Implement licensing boundary |
| Commercial | No subscription/perpetual product model | High | Decide pricing and entitlements |
| SDK | No stable public SDK API | Critical for SDK strategy | Create `PdfViewer.Core`/`PdfEngine.Abstractions` |
| Cross-platform | Windows x64 only | High | Keep UI Windows-first; make core portable |
| ARM | No Windows ARM64 strategy | Medium | Plan native PDFium ARM64 build |
| Web | No browser runtime | P3 | Only after core stabilizes |
| Mobile | No mobile architecture | P3 | Separate product decision |
| Docs | No formal architecture decision records | Medium | Add ADRs |
| CI/CD | Commercial release gates need strengthening | High | Add signing, SBOM verification, compatibility tests |
| OSS | Community process is minimal | Medium | CONTRIBUTING, issue templates, security policy |
| Support | No support/SLA model | High for enterprise | Define support tiers |
| Legal | Product EULA/privacy policy not represented | High | Add legal/compliance workstream |

## Highest-priority gaps

### P0

1. Secure native engine lifecycle.
2. Fuzzing and malformed-document regression corpus.
3. Signed binaries and update verification.
4. UI-neutral core API.
5. Large-document performance tests.
6. Compatibility corpus.

### P1

1. AcroForms.
2. Digital signatures.
3. Redaction.
4. Page operations.
5. Merge/split.
6. OCR.
7. Accessibility.
8. Enterprise deployment.
9. Licensing.
10. Localization.

### P2

1. Content editing.
2. Comparison.
3. PDF/A.
4. optimization.
5. stamps/watermarks.
6. attachments/layers.
7. collaboration/review.

### P3

1. AI assistant.
2. WebAssembly/browser.
3. macOS/Linux.
4. mobile.
5. cloud document services.

## Important warning

Do not add AI before the document model, security model and operation model are mature.

AI should consume capabilities from the core rather than bypassing them.
