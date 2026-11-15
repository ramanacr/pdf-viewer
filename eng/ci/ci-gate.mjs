// The decision of the required `build-and-test` gate job: `NEEDS='<json>' node eng/ci/ci-gate.mjs`.
//
// NEEDS is the JSON of the `needs` context of the job: for every job it lists a `result`
// (success, failure, cancelled, skipped) and the `outputs`. The outputs of `changes` say which
// jobs had to run (ci-changes.mjs). The rules:
//   - `changes` must have succeeded. If it did not, nobody knows what had to run: FAIL.
//   - A job that had to run must have the result `success`. Skipped, cancelled and failure all
//     FAIL. (A job that had to run is skipped when a job before it failed.)
//   - A job that did not have to run may be `skipped` (the normal case) or `success`. Any other
//     result FAILS.
//   - A job missing from the input, or an output that is neither "true" nor "false": FAIL.
// The tests are in ci-gate.test.mjs (`node --test eng/ci/*.test.mjs`).
//
// Adapted from ramanacr/star-ng (tools/ci-gate.mjs).
import { fileURLToPath } from 'node:url';
import { JOBS } from './ci-changes.mjs';

/**
 * @param {Record<string, { result?: string, outputs?: Record<string, string> }>} needs
 * @returns {{ ok: boolean, lines: string[], failures: string[] }}
 */
export function evaluate(needs) {
  const lines = [];
  const failures = [];
  const changes = needs?.changes;
  lines.push(`changes: ${changes?.result ?? 'missing'}`);
  if (changes?.result !== 'success') {
    failures.push(`changes did not succeed (${changes?.result ?? 'missing'}): what had to run is unknown`);
    return { ok: false, lines, failures };
  }
  for (const { job, output } of JOBS) {
    const flag = changes.outputs?.[output];
    const result = needs[job]?.result;
    if (flag !== 'true' && flag !== 'false') {
      lines.push(`${job}: ${result ?? 'missing'} (needed: unknown)`);
      failures.push(`${job}: the output "${output}" of changes is "${flag ?? 'missing'}", not true or false`);
      continue;
    }
    const needed = flag === 'true';
    lines.push(`${job}: ${result ?? 'missing'} (needed: ${needed})`);
    if (result === undefined) {
      failures.push(`${job}: missing from the needs of the gate`);
    } else if (needed && result !== 'success') {
      failures.push(`${job}: had to run, but the result is ${result}`);
    } else if (!needed && result !== 'skipped' && result !== 'success') {
      failures.push(`${job}: did not have to run, and the result is ${result}`);
    }
  }
  return { ok: failures.length === 0, lines, failures };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  let needs;
  try {
    needs = JSON.parse(process.env.NEEDS ?? '');
  } catch {
    console.error('::error::ci-gate: the environment variable NEEDS is not valid JSON.');
    process.exit(1);
  }
  const { ok, lines, failures } = evaluate(needs);
  console.log(lines.join('\n'));
  if (!ok) {
    for (const failure of failures) console.error(`::error::${failure}`);
    console.error('Required jobs did not pass.');
    process.exit(1);
  }
  console.log('All required jobs passed (a skipped job was not needed).');
}
