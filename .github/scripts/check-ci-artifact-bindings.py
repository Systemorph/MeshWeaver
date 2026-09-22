#!/usr/bin/env python3
"""Check the explicitly migrated artifact graphs, without copying CI policy into satellites."""
import argparse
from pathlib import Path
import re
import sys


ROOTS = {
    'node-repo-module-pack.yml': 'select',
    'node-repo-gate.yml': 'plan',
    'node-repo-compile-check.yml': 'compile-check',
    'node-repo-module-publish.yml': 'publish',
    'node-repo-publish-bake.yml': 'publish-bake',
    'ci.yml': 'change-set',
}
TRANSFER = re.compile(r'uses: Systemorph/MeshWeaver/\.github/actions/(?:upload|download)-artifact@main')
CALLEE = re.compile(r'uses: Systemorph/MeshWeaver/\.github/workflows/node-repo-(?:module-pack|gate|compile-check|module-publish|publish-bake)\.yml@main')


def errors(text, filename):
    """A run has one graph root; consumers carry its outputs, never their own fresh identity."""
    root = ROOTS[filename]
    text = '\n'.join(line for line in text.splitlines() if not line.lstrip().startswith('#')) + '\n'
    jobs = dict(re.findall(r'^  ([a-z][a-z0-9-]*):\n([\s\S]*?)(?=^  [a-z][a-z0-9-]*:\n|\Z)', text, re.M))
    failures = []
    if root not in jobs:
        return [f'missing artifact graph root {root}']
    resolver = 'uses: Systemorph/MeshWeaver/.github/actions/resolve-artifact-store@main'
    if text.count(resolver) != 1 or resolver not in jobs[root]:
        failures.append('resolve the physical store exactly once, in the first producer')
    if filename != 'ci.yml' and 'expected-store-id: ${{ inputs.artifact-store-id }}' not in jobs[root]:
        failures.append('a reusable graph must validate its caller identity')
    if filename != 'ci.yml':
        if "require-expected-store-id: 'true'" not in jobs[root]:
            failures.append('a reusable must reject a missing caller identity before resolution')
        if "store: ${{ inputs.artifact-store || 'gha' }}" not in jobs[root]:
            failures.append('legacy reusable callers must remain on gha regardless of org variables')
        declared = re.search(r"^      artifact-store:\n([\s\S]*?)(?=^      [a-z][a-z-]*:|\Z)", text, re.M)
        if not declared or "default: 'gha'" not in declared[1]:
            failures.append('the reusable artifact-store input must default to gha')
    total = 0
    for name, job in jobs.items():
        producer = name == root
        identity = 'steps.store.outputs.store-id' if producer else f'needs.{root}.outputs.artifact-store-id'
        mode = 'steps.store.outputs.store' if producer else f'needs.{root}.outputs.artifact-store'
        steps = re.split(r'\n      -', job)
        bound = False
        for step in steps:
            if re.search(r'uses: actions/(?:upload|download)-artifact@', step):
                failures.append(f'{name}: direct GitHub artifact storage bypasses the selected backend')
            if TRANSFER.search(step):
                total += 1
                bound = True
                if f'expected-store-id: ${{{{ {identity} }}}}' not in step:
                    failures.append(f'{name}: transfer does not assert the first producer identity')
                if f"store: ${{{{ {mode} || 'unresolved' }}}}" not in step:
                    failures.append(f'{name}: transfer does not use the first producer backend')
                if 'continue-on-error:' in step:
                    failures.append(f'{name}: a transfer failure cannot be swallowed')
            if 'uses: actions/cache@' in step:
                condition = re.search(r'^        if: (.+)$', step, re.M)
                if not condition or f"{mode} == 'gha'" not in condition[1] or '||' in condition[1]:
                    failures.append(f'{name}: GitHub cache must be disabled in own-store mode')
            if 'uses: actions/setup-node@' in step:
                if f"package-manager-cache: ${{{{ {mode} == 'gha' }}}}" not in step:
                    failures.append(f'{name}: setup-node automatic caching must follow the backend')
        if CALLEE.search(job):
            bound = True
            if f"artifact-store: ${{{{ {mode} || 'unresolved' }}}}" not in job or f'artifact-store-id: ${{{{ {identity} }}}}' not in job:
                failures.append(f'{name}: reusable caller must carry backend and physical identity')
        if bound and not producer:
            needed = re.search(r'^    needs: (.+)$', job, re.M)
            if not needed or root not in re.findall(r'[a-z][a-z0-9-]*', needed[1]):
                failures.append(f'{name}: no direct dependency on the artifact graph root')
    if total == 0:
        failures.append('the migrated graph must contain artifact transfers')
    # Multi-job roots publish both outputs; a missing output would otherwise expand empty.
    if any(f'needs.{root}.outputs.artifact-store' in job for job in jobs.values()):
        for output, field in [('artifact-store', 'store'), ('artifact-store-id', 'store-id')]:
            if f'{output}: ${{{{ steps.store.outputs.{field} }}}}' not in jobs[root]:
                failures.append(f'{root}: missing {output} output')
    return failures


def self_test():
    """Exercise the satellite graph without requiring a checkout of that repository."""
    fixture = '''jobs:
  change-set:
    outputs:
      artifact-store: ${{ steps.store.outputs.store }}
      artifact-store-id: ${{ steps.store.outputs.store-id }}
    steps:
      - uses: Systemorph/MeshWeaver/.github/actions/resolve-artifact-store@main
      - uses: Systemorph/MeshWeaver/.github/actions/upload-artifact@main
        with:
          store: ${{ steps.store.outputs.store || 'unresolved' }}
          expected-store-id: ${{ steps.store.outputs.store-id }}
  consumer:
    needs: change-set
    steps:
      - uses: Systemorph/MeshWeaver/.github/actions/download-artifact@main
        with:
          store: ${{ needs.change-set.outputs.artifact-store || 'unresolved' }}
          expected-store-id: ${{ needs.change-set.outputs.artifact-store-id }}
      - uses: actions/setup-node@v7
        with:
          package-manager-cache: ${{ needs.change-set.outputs.artifact-store == 'gha' }}
  modules:
    needs: change-set
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-pack.yml@main
    with:
      artifact-store: ${{ needs.change-set.outputs.artifact-store || 'unresolved' }}
      artifact-store-id: ${{ needs.change-set.outputs.artifact-store-id }}
'''
    assert not errors(fixture, 'ci.yml'), errors(fixture, 'ci.yml')
    mutations = [
        fixture.replace('expected-store-id: ${{ needs.change-set.outputs.artifact-store-id }}', 'expected-store-id: '),
        fixture.replace("store: ${{ needs.change-set.outputs.artifact-store || 'unresolved' }}", 'store: gha', 1),
        fixture.replace(" || 'unresolved'", '', 1),
        fixture.replace('artifact-store-id: ${{ needs.change-set.outputs.artifact-store-id }}', 'artifact-store-id: '),
        fixture.replace('needs: change-set', 'needs: other', 1),
        fixture.replace('Systemorph/MeshWeaver/.github/actions/upload-artifact@main', 'actions/upload-artifact@v7'),
        fixture.replace("package-manager-cache: ${{ needs.change-set.outputs.artifact-store == 'gha' }}", 'package-manager-cache: true'),
        fixture.replace('artifact-store-id: ${{ steps.store.outputs.store-id }}', 'artifact-store-id: '),
    ]
    for index, mutation in enumerate(mutations):
        assert errors(mutation, 'ci.yml'), f'bad satellite binding {index} was accepted'
    print(f'artifact bindings self-test: {len(mutations) + 1} rules passed')
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workflow', type=Path)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if args.workflow is None:
        parser.error('--workflow is required unless --self-test is given')
    if args.workflow.name not in ROOTS:
        parser.error('workflow is not an explicitly migrated artifact graph')
    problems = errors(args.workflow.read_text(), args.workflow.name)
    for problem in problems:
        print(f'::error::{args.workflow}: {problem}', file=sys.stderr)
    if not problems:
        print(f'artifact bindings: {args.workflow} uses one producer-resolved store identity')
    return bool(problems)


if __name__ == '__main__':
    sys.exit(main())
