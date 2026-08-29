# Engineering Backlog

## EPIC 1 — Core extraction

### P0-001 Create PdfEngine.Abstractions
- [ ] Create engine-neutral interfaces.
- [ ] Remove WPF references.
- [ ] Define document/page/render contracts.
- [ ] Define cancellation semantics.
- [ ] Define error taxonomy.
- [ ] Add XML documentation.

### P0-002 Create PdfEngine.Pdfium
- [ ] Move native bridge.
- [ ] Move SafeHandles.
- [ ] Move PDFium document adapter.
- [ ] Add native ABI version validation.
- [ ] Add deterministic initialization/shutdown.

### P0-003 Extract rendering
- [ ] Create `RenderedPage`.
- [ ] Create `RenderRequest`.
- [ ] Create `RenderPriority`.
- [ ] Move bitmap conversion to WPF adapter.
- [ ] Add memory ownership semantics.

## EPIC 2 — Document session

### P0-010 DocumentSession
- [ ] Introduce document identity/fingerprint.
- [ ] Introduce revision counter.
- [ ] Introduce dirty state.
- [ ] Introduce session lifecycle.
- [ ] Add close/cancel semantics.
- [ ] Add safe concurrent operation policy.

## EPIC 3 — Rendering performance

### P0-020 Render scheduler
- [ ] Priority queue.
- [ ] Cancellation.
- [ ] Deduplication.
- [ ] viewport prefetch.
- [ ] thumbnail priority.
- [ ] cache invalidation.
- [ ] performance counters.

### P0-021 Cache redesign
- [ ] metadata cache.
- [ ] text cache.
- [ ] thumbnail cache.
- [ ] rendered-page cache.
- [ ] configurable memory budget.
- [ ] cache telemetry.

### P0-022 Large document test corpus
- [ ] 100 pages.
- [ ] 500 pages.
- [ ] 1,000 pages.
- [ ] 5,000 pages.
- [ ] huge image pages.
- [ ] malformed PDFs.
- [ ] encrypted PDFs.

## EPIC 4 — Reliability/security

### P0-030 Native crash safety
- [ ] isolate risky operations.
- [ ] validate handles.
- [ ] ensure shutdown ordering.
- [ ] add crash diagnostics.

### P0-031 Fuzz regression
- [ ] malformed corpus.
- [ ] mutation corpus.
- [ ] crash reproduction archive.
- [ ] native API fuzz harness where practical.

### P0-032 Secure updates
- [ ] Authenticated metadata.
- [ ] signed package verification.
- [ ] HTTPS-only transport.
- [ ] rollback.
- [ ] update failure recovery.

## EPIC 5 — Forms

### P1-040 AcroForm
- [ ] enumerate fields.
- [ ] field types.
- [ ] field values.
- [ ] appearance regeneration.
- [ ] checkbox/radio.
- [ ] combo/list.
- [ ] multiline text.
- [ ] validation.
- [ ] save.
- [ ] import/export.

### P1-041 XFA assessment
- [ ] determine supported PDFium capability.
- [ ] identify gaps.
- [ ] decide native implementation vs optional commercial engine.
- [ ] document unsupported scenarios.

## EPIC 6 — Signatures

### P1-050 Signature model
- [ ] signature field model.
- [ ] certificate model.
- [ ] trust store abstraction.
- [ ] signing provider abstraction.
- [ ] validation result model.

### P1-051 Signing
- [ ] certificate selection.
- [ ] signing workflow.
- [ ] incremental save.
- [ ] timestamping strategy.
- [ ] validation.
- [ ] audit details.
- [ ] tamper detection.

## EPIC 7 — Redaction

### P1-060 Redaction
- [ ] text redaction.
- [ ] image redaction.
- [ ] vector redaction.
- [ ] metadata sanitization.
- [ ] attachment sanitization.
- [ ] irreversible flattening.
- [ ] verification scan.

## EPIC 8 — Page operations

### P1-070 Page organizer
- [ ] delete.
- [ ] insert.
- [ ] reorder.
- [ ] rotate.
- [ ] extract.
- [ ] replace.

### P1-071 Merge/split
- [ ] multi-document sessions.
- [ ] merge.
- [ ] split.
- [ ] output naming.
- [ ] failure recovery.

## EPIC 9 — Editing

### P2-080 Content model
- [ ] text objects.
- [ ] images.
- [ ] paths.
- [ ] transforms.
- [ ] object selection.
- [ ] edit operations.
- [ ] undo/redo.

## EPIC 10 — OCR

### P2-090 OCR abstraction
- [ ] local provider interface.
- [ ] optional cloud provider.
- [ ] language packs.
- [ ] searchable PDF output.
- [ ] confidence model.

## EPIC 11 — Accessibility

### P1-100 Accessibility
- [ ] tagged PDF inspection.
- [ ] reading order.
- [ ] keyboard navigation.
- [ ] screen reader semantics.
- [ ] high contrast.
- [ ] zoom/accessibility preferences.
- [ ] automated UI accessibility tests.

## EPIC 12 — Enterprise

### P1-110 Deployment
- [ ] MSI.
- [ ] MSIX evaluation.
- [ ] silent install.
- [ ] Intune deployment.
- [ ] uninstall.
- [ ] rollback.
- [ ] enterprise config.

### P1-111 Policy
- [ ] policy schema.
- [ ] managed settings.
- [ ] update policy.
- [ ] telemetry policy.
- [ ] security policy.
- [ ] licensing policy.

## EPIC 13 — Licensing

### P1-120 Licensing
- [ ] capability model.
- [ ] offline activation.
- [ ] license cache.
- [ ] clock rollback detection strategy.
- [ ] graceful expiration.
- [ ] enterprise entitlement.
- [ ] SDK license API.

## EPIC 14 — SDK

### P2-130 Public API
- [ ] stable namespaces.
- [ ] API versioning.
- [ ] semantic versioning.
- [ ] XML docs.
- [ ] samples.
- [ ] NuGet packaging.
- [ ] compatibility policy.

## EPIC 15 — AI

### P3-140 Document intelligence
- [ ] local extraction pipeline.
- [ ] chunking.
- [ ] page-aware citations.
- [ ] structured extraction.
- [ ] summarization.
- [ ] question answering.
- [ ] privacy policy.
- [ ] local model provider.

## Definition of Done

Every production feature must include:

- [ ] Core API.
- [ ] UI integration.
- [ ] Unit tests.
- [ ] PDF integration tests.
- [ ] malformed input tests.
- [ ] cancellation tests.
- [ ] performance benchmark where applicable.
- [ ] accessibility review.
- [ ] localization resources.
- [ ] documentation.
- [ ] release notes.
- [ ] security review.
