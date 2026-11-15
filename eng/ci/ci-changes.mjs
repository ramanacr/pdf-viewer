// Change detection for CI: does a pull request need the Windows build and test job?
//
//   git diff -z --name-only --no-renames <base>...<head> | node eng/ci/ci-changes.mjs \
//     --event pull_request --labels '["bug"]' [--output-file FILE] [--summary-file FILE]
//
// The paths come on standard input, separated by NUL (from `git diff -z`) or by new lines.
// The tool prints `name=true|false` lines (the format of $GITHUB_OUTPUT) and a Markdown summary.
// With --output-file and --summary-file it appends them to those files (CI passes
// $GITHUB_OUTPUT and $GITHUB_STEP_SUMMARY). The rules are plain data in RULES below. The tests
// are in ci-changes.test.mjs (`node --test eng/ci/*.test.mjs`). ci-gate.mjs uses the same job table.
//
// Safety rules, in this order (a full run needs every job):
//   1. The event is not `pull_request` (push to main, the nightly run, a manual run): FULL.
//   2. The pull request has the label `ci:full`: FULL.
//   3. The list of changed files is empty: FULL. A pull request always changes a file, so an
//      empty list means that the diff failed or that the checkout is too shallow.
//   4. Otherwise every changed path is matched against RULES, and the needs of all paths are
//      added up. A path that no rule matches needs EVERYTHING (the last rule), so a new kind of
//      file can never skip a job by accident.
//
// Adapted from ramanacr/star-ng (tools/ci-changes.mjs).
import { appendFileSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

/** The label that forces a full run. Documented in CONTRIBUTING.md. */
export const FORCE_LABEL = 'ci:full';

/**
 * The jobs of ci.yml that can be skipped, and the output name of `changes` that decides each.
 * ci-gate.mjs checks every job in this table.
 */
export const JOBS = [
  {
    job: 'windows-build-test',
    output: 'build_test',
    title: 'windows-build-test (build, all .NET test suites, packaging and release gates)',
  },
];

const NONE = [];
const ALL = ['build_test'];

/**
 * The rules. The FIRST rule that matches a path decides it, so a narrow rule must stand before
 * a wide one. Only files that the Windows job never reads may have `needs: NONE`. Files that are
 * packed or published (THIRD_PARTY_NOTICES.md, LICENSE, each project's PACKAGE_README.md) are not
 * listed, so they fall through to the last rule and run everything.
 * @type {{ name: string, test: RegExp, needs: string[], why: string }[]}
 */
export const RULES = [
  // --- CI itself ---------------------------------------------------------------------------
  {
    name: 'ci.yml',
    test: /^\.github\/workflows\/ci\.yml$/,
    needs: ALL,
    why: 'the CI definition: a change must be proven by a full run',
  },
  {
    name: 'ci tool tests',
    test: /^eng\/ci\/[^/]+\.test\.mjs$/,
    needs: NONE,
    why: 'the tool tests run in the changes job on every pull request',
  },
  {
    name: 'ci tools',
    test: /^eng\/ci\//,
    needs: ALL,
    why: 'the code that decides what runs: prove it with a full run',
  },
  // --- Files that the Windows job never reads ----------------------------------------------
  {
    name: 'other workflows',
    test: /^\.github\/workflows\/(android|android-release|msix|npm|packages|release|web-demo)\.yml$/,
    needs: NONE,
    why: 'each runs on its own; ci.yml does not read them',
  },
  {
    name: 'GitHub templates',
    test: /^\.github\/(ISSUE_TEMPLATE\/.*|pull_request_template\.md)$/,
    needs: NONE,
    why: 'issue and pull request templates',
  },
  {
    name: 'root documents',
    test: /^(README|ROADMAP|CONTRIBUTING|CODE_OF_CONDUCT|SECURITY|CODE_SIGNING_POLICY)\.md$/,
    needs: NONE,
    why: 'project documents; no build, test or package reads them',
  },
  {
    name: 'documentation site',
    test: /^docs\//,
    needs: NONE,
    why: 'the GitHub Pages site; the web demo workflow builds it',
  },
  {
    name: 'store listings',
    test: /^(store|store-screenshots|fastlane)\//,
    needs: NONE,
    why: 'Microsoft Store and Google Play listing text and images',
  },
  {
    name: 'embed SDK package',
    test: /^(packages\/pdfviewer-embed|tests\/sdk)\//,
    needs: NONE,
    why: 'the npm package; the Embed SDK Package workflow checks it',
  },
  // --- Everything else ---------------------------------------------------------------------
  {
    name: 'everything else',
    test: /^/,
    needs: ALL,
    why: 'no narrower rule matched: run everything',
  },
];

/**
 * @param {{ event: string, labels: string[], paths: string[] }} input
 * @returns {{ outputs: Record<string, boolean>, reason: string, matches: { path: string, rule: string }[] }}
 */
export function decide({ event, labels, paths }) {
  const full = (reason) => ({ outputs: allOutputs(true), reason, matches: [] });
  if (event !== 'pull_request') return full(`the event is ${event || 'unknown'}, not pull_request`);
  if (labels.includes(FORCE_LABEL)) return full(`the pull request has the label ${FORCE_LABEL}`);
  const clean = paths.map((p) => p.trim()).filter(Boolean);
  if (clean.length === 0) return full('the list of changed files is empty');

  const outputs = allOutputs(false);
  const matches = [];
  for (const path of clean) {
    const rule = RULES.find((r) => r.test.test(path));
    matches.push({ path, rule: rule.name });
    for (const need of rule.needs) outputs[need] = true;
  }
  return { outputs, reason: 'by the changed files', matches };
}

function allOutputs(value) {
  return Object.fromEntries(JOBS.map(({ output }) => [output, value]));
}

/** @param {ReturnType<typeof decide>} result */
export function summary(result) {
  const lines = ['### CI change detection', '', `Decision: ${result.reason}.`, ''];
  for (const { job, output, title } of JOBS) {
    lines.push(`- ${result.outputs[output] ? 'RUN' : 'skip'}: ${title} (\`${job}\`)`);
  }
  if (result.matches.length > 0) {
    lines.push('', '| Changed file | Rule |', '| --- | --- |');
    for (const { path, rule } of result.matches.slice(0, 200)) lines.push(`| \`${path}\` | ${rule} |`);
    if (result.matches.length > 200) lines.push(`| ... ${result.matches.length - 200} more | |`);
  }
  return lines.join('\n') + '\n';
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const { values } = parseArgs({
    options: {
      event: { type: 'string', default: '' },
      labels: { type: 'string', default: '[]' },
      'output-file': { type: 'string' },
      'summary-file': { type: 'string' },
    },
  });
  let labels;
  try {
    labels = JSON.parse(values.labels);
    if (!Array.isArray(labels)) throw new Error('not an array');
  } catch {
    console.error('::error::ci-changes: --labels is not a JSON array.');
    process.exit(1);
  }
  const paths = readFileSync(0, 'utf8').split(/[\0\n]/);
  const result = decide({ event: values.event, labels, paths });
  const out = Object.entries(result.outputs).map(([k, v]) => `${k}=${v}`).join('\n') + '\n';
  const md = summary(result);
  process.stdout.write(out + md);
  if (values['output-file']) appendFileSync(values['output-file'], out);
  if (values['summary-file']) appendFileSync(values['summary-file'], md);
}
