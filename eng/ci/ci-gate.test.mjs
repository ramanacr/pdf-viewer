import { test } from 'node:test';
import assert from 'node:assert/strict';
import { evaluate } from './ci-gate.mjs';

const changes = (build_test, result = 'success') => ({ result, outputs: { build_test } });

test('a needed job that succeeded passes', () => {
  assert.equal(evaluate({ changes: changes('true'), 'windows-build-test': { result: 'success' } }).ok, true);
});

test('a job that was not needed and was skipped passes', () => {
  assert.equal(evaluate({ changes: changes('false'), 'windows-build-test': { result: 'skipped' } }).ok, true);
});

test('a job that was not needed and still succeeded passes', () => {
  assert.equal(evaluate({ changes: changes('false'), 'windows-build-test': { result: 'success' } }).ok, true);
});

test('a needed job that failed, was cancelled or was skipped fails', () => {
  for (const result of ['failure', 'cancelled', 'skipped']) {
    assert.equal(evaluate({ changes: changes('true'), 'windows-build-test': { result } }).ok, false, result);
  }
});

test('a job that was not needed but failed or was cancelled fails', () => {
  for (const result of ['failure', 'cancelled']) {
    assert.equal(evaluate({ changes: changes('false'), 'windows-build-test': { result } }).ok, false, result);
  }
});

test('a changes job that did not succeed fails', () => {
  for (const result of ['failure', 'cancelled', 'skipped']) {
    const r = evaluate({ changes: changes('false', result), 'windows-build-test': { result: 'skipped' } });
    assert.equal(r.ok, false, result);
  }
  assert.equal(evaluate({ 'windows-build-test': { result: 'success' } }).ok, false);
  assert.equal(evaluate(undefined).ok, false);
});

test('a missing or bad output fails', () => {
  for (const flag of [undefined, '', 'yes', 'TRUE']) {
    assert.equal(evaluate({ changes: changes(flag), 'windows-build-test': { result: 'success' } }).ok, false, String(flag));
  }
});

test('a job missing from the needs fails', () => {
  assert.equal(evaluate({ changes: changes('false') }).ok, false);
});
