# Commercial Readiness Acceptance Criteria

## Release 1.0 — Professional Reader

### Rendering
- [ ] 100-page document scrolls without visible stalls.
- [ ] 500-page document remains usable.
- [ ] 1,000-page document opens without excessive memory growth.
- [ ] Zoom does not create unbounded render tasks.
- [ ] Closing a document cancels outstanding work.

### Text
- [ ] Search is cancellable.
- [ ] Search results navigate correctly.
- [ ] Selection coordinates are stable.
- [ ] Unicode text is handled correctly.
- [ ] RTL and non-Latin fixtures are included.

### Annotations
- [ ] Existing annotations load.
- [ ] New annotations save.
- [ ] Annotation coordinates survive reopen.
- [ ] XFDF import/export behavior is tested.
- [ ] Flattening is verified.

### Reliability
- [ ] Corrupt PDFs fail safely.
- [ ] Password-protected PDFs behave predictably.
- [ ] Native handles are disposed.
- [ ] No known reproducible native crash remains.

### Security
- [ ] Document JavaScript policy exists.
- [ ] External actions are controlled.
- [ ] Embedded content is controlled.
- [ ] Native dependency checksum is verified.
- [ ] Releases are signed.

## Release 2.0 — Pro

### Page operations
- [ ] Merge.
- [ ] Split.
- [ ] Insert.
- [ ] Delete.
- [ ] Reorder.
- [ ] Extract.
- [ ] Rotate and persist.

### Forms
- [ ] AcroForm fields enumerate.
- [ ] Fields can be filled.
- [ ] Values persist.
- [ ] Form appearance is correct.
- [ ] Import/export works.

### Signatures
- [ ] Signature creation works.
- [ ] Signature verification works.
- [ ] Certificate trust is configurable.
- [ ] Tampered documents are detected.

### Redaction
- [ ] Text redaction removes underlying text.
- [ ] Redacted content cannot be copied.
- [ ] Hidden content is removed.
- [ ] Metadata/attachments are handled.
- [ ] Verification scan exists.

### OCR
- [ ] Scanned PDF becomes searchable.
- [ ] OCR is cancellable.
- [ ] Language selection works.
- [ ] Output quality is measurable.

## Release 3.0 — Enterprise

- [ ] Silent installation.
- [ ] Enterprise deployment.
- [ ] Managed policy.
- [ ] Offline activation.
- [ ] Signed update.
- [ ] Rollback.
- [ ] Security disclosure process.
- [ ] Support process.
- [ ] LTS release process.
- [ ] SBOM.
- [ ] Reproducible release metadata.

## SDK readiness

- [ ] No WPF dependency in core.
- [ ] Stable public interfaces.
- [ ] Semantic versioning.
- [ ] API compatibility policy.
- [ ] NuGet packaging.
- [ ] Sample integration.
- [ ] Redistribution license.
- [ ] Headless processing tests.
- [ ] Threading model documented.
- [ ] Performance characteristics documented.

## AI readiness

AI must not ship until:

- [ ] page-aware extraction exists;
- [ ] document fingerprinting exists;
- [ ] security policy exists;
- [ ] privacy mode exists;
- [ ] citations can point to page/region;
- [ ] model provider is replaceable;
- [ ] prompts do not bypass authorization;
- [ ] enterprise can disable AI;
- [ ] no document upload occurs without explicit policy.

## Commercial launch checklist

- [ ] Product name/trademark reviewed.
- [ ] EULA ready.
- [ ] Privacy policy ready.
- [ ] Third-party notices ready.
- [ ] License inventory complete.
- [ ] Installer signed.
- [ ] Update mechanism signed.
- [ ] Support portal/process ready.
- [ ] Release notes ready.
- [ ] Crash/diagnostics policy ready.
- [ ] Pricing and entitlements implemented.
- [ ] Trial/activation path tested.
- [ ] Refund/cancellation processes defined.
