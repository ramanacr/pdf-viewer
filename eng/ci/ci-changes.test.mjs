import { test } from 'node:test';
import assert from 'node:assert/strict';
import { decide, summary, FORCE_LABEL, RULES } from './ci-changes.mjs';

const pr = (paths, labels = []) => decide({ event: 'pull_request', labels, paths });
const runs = (paths, labels) => pr(paths, labels).outputs.build_test;

test('every event that is not a pull request runs everything', () => {
  for (const event of ['push', 'schedule', 'workflow_dispatch', '']) {
    assert.equal(decide({ event, labels: [], paths: ['README.md'] }).outputs.build_test, true, event);
  }
});

test('the ci:full label runs everything', () => {
  assert.equal(runs(['ROADMAP.md'], [FORCE_LABEL]), true);
});

test('an empty list of changed files runs everything', () => {
  assert.equal(runs([]), true);
  assert.equal(runs(['', '  ']), true);
});

test('documentation-only changes skip the Windows job', () => {
  const skip = [
    ['ROADMAP.md'],
    ['README.md', 'CONTRIBUTING.md', 'SECURITY.md', 'CODE_OF_CONDUCT.md', 'CODE_SIGNING_POLICY.md'],
    ['docs/features.md', 'docs/index.html', 'docs/sdk/examples/large-pdf.js'],
    ['.github/ISSUE_TEMPLATE/bug.yml', '.github/pull_request_template.md'],
    ['store/listing.md', 'store-screenshots/1.png', 'fastlane/metadata/android/en-US/title.txt'],
    ['packages/pdfviewer-embed/package.json', 'tests/sdk/embed-range.test.mjs'],
    ['.github/workflows/web-demo.yml', '.github/workflows/npm.yml', '.github/workflows/android.yml'],
    ['eng/ci/ci-changes.test.mjs'],
  ];
  for (const paths of skip) assert.equal(runs(paths), false, paths.join(', '));
});

test('code, packed files and CI changes run everything', () => {
  const full = [
    ['src/PdfViewer/MainWindow.xaml.cs'],
    ['src/PdfViewer.Android/MainActivity.cs'],
    ['tests/PdfViewer.Tests/SomeTests.cs'],
    ['tests/sdk/font-base-cases.json'],
    ['tests/sdk/sub/helper.mjs'],
    ['THIRD_PARTY_NOTICES.md'],
    ['LICENSE'],
    ['src/PdfEngine.Vector/PACKAGE_README.md'],
    ['src/PdfEngine.Vector/THIRD_PARTY_NOTICES.md'],
    ['eng/signing/README.md'],
    ['Directory.Build.props'],
    ['global.json'],
    ['PdfViewer.slnx'],
    ['.github/workflows/ci.yml'],
    ['.github/workflows/new-workflow.yml'],
    ['eng/ci/ci-changes.mjs'],
    ['eng/ci/ci-gate.mjs'],
    ['scripts/build_publish.ps1'],
    ['samples/SampleDocument.pdf'],
    ['docs.md'],
    ['a-new-kind-of-file.xyz'],
  ];
  for (const paths of full) assert.equal(runs(paths), true, paths.join(', '));
});

test('one code file among documents runs everything', () => {
  assert.equal(runs(['ROADMAP.md', 'docs/features.md', 'src/PdfViewer.Core/Foo.cs']), true);
});

test('rule matching uses the full path, not a part of it', () => {
  assert.equal(runs(['src/docs/Foo.cs']), true);
  assert.equal(runs(['src/README.md']), true);
  assert.equal(runs(['README.md.cs']), true);
});

test('the last rule matches every path', () => {
  const last = RULES[RULES.length - 1];
  assert.deepEqual(last.needs, ['build_test']);
  assert.ok(last.test.test('anything/at/all'));
});

test('the summary names the decision and every changed file', () => {
  const md = summary(pr(['ROADMAP.md', 'docs/a.md']));
  assert.match(md, /skip: windows-build-test/);
  assert.match(md, /`ROADMAP\.md` \| root documents/);
  assert.match(md, /`docs\/a\.md` \| documentation site/);
});
