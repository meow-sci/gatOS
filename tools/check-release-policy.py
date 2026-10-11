#!/usr/bin/env python3
"""Offline checks for the actual metadata script and rolling release selection."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import sys
sys.dont_write_bytecode = True
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('retention', ROOT / 'tools/release-retention.py')
retention = importlib.util.module_from_spec(spec)
spec.loader.exec_module(retention)


class ReleasePolicyTests(unittest.TestCase):
    def metadata(self, ref, event='push', attempt='1', run_number='123', success=True):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / 'output'
            env = dict(os.environ, GITHUB_REF=ref, GITHUB_EVENT_NAME=event,
                       GITHUB_RUN_ID='12345', GITHUB_RUN_ATTEMPT=attempt,
                       GITHUB_RUN_NUMBER=run_number,
                       GITHUB_OUTPUT=str(output))
            result = subprocess.run(['bash', 'tools/release-metadata.sh'], cwd=ROOT,
                                    env=env, text=True, capture_output=True)
            self.assertEqual(result.returncode == 0, success, result.stderr)
            return dict(line.split('=', 1) for line in output.read_text().splitlines()) if output.exists() else {}

    def test_exact_versions_for_publishing_branches(self):
        for branch in ('main', 'feature/new-ux', 'feature/nested/some-work',
                       'feature/$(echo-untrusted)'):
            feature = branch.startswith('feature/')
            for event in ('push', 'workflow_dispatch'):
                for run_number, attempt in (('123', '1'), ('124', '1'), ('123', '2')):
                    with self.subTest(branch=branch, event=event, run=run_number, attempt=attempt):
                        version = f'1.{run_number}.0' + ('-beta' if feature else '')
                        data = self.metadata('refs/heads/' + branch, event,
                                             attempt=attempt, run_number=run_number)
                        self.assertEqual(data, {
                            'publish': 'true', 'version': version, 'modversion': version,
                            'tag': 'v' + version, 'title': 'gatOS ' + version,
                            'prerelease': str(feature).lower(),
                            'channel': 'feature' if feature else 'main',
                        })

    def test_invalid_run_numbers(self):
        for branch in ('main', 'feature/test'):
            for invalid in ('', '0', '01', '-1', '1.2', '$(echo-untrusted)'):
                self.metadata('refs/heads/' + branch, run_number=invalid, success=False)

    def test_only_branch_push_or_dispatch_can_publish(self):
        for ref, event in [('refs/pull/42/merge', 'pull_request'),
                           ('refs/heads/feature/test', 'pull_request'),
                           ('refs/tags/main', 'workflow_dispatch'),
                           ('refs/heads/fix/test', 'push'), ('refs/heads/chore/test', 'push'),
                           ('refs/heads/release/2.3.4', 'push'),
                           ('refs/heads/release/2.3.4', 'workflow_dispatch')]:
            self.assertEqual(self.metadata(ref, event), {'publish': 'false'})
        self.assertEqual(self.metadata('refs/heads/feature/test', 'workflow_dispatch')['publish'], 'true')

    def test_retention_all_pages_and_protected_releases(self):
        def release(number, tag=None, **flags):
            return dict(id=number, tag_name=tag or f'v1.{number}.0-beta',
                        published_at='2026-09-05T00:00:00Z', prerelease=True, draft=False) | flags
        pages = [[release(n) for n in range(1, 151)], [release(n) for n in range(151, 251)]]
        pages[1] += [release(1000, 'v2026.09.21.123', prerelease=False),
                     release(1007, 'guest-v17'), release(1008, 'feature-legacy'),
                     release(1001, 'v2026.09.21.124'),
                     release(999, 'tip-999'), release(998, 'v1.2.3', prerelease=False),
                     release(997, 'v1.997.0-beta', draft=True),
                     release(996, 'v1.996.0-beta', prerelease=False),
                     release(995, 'v1.995.0'), release(994, 'v1.994.0-beta.1')]
        stale = retention.stale_tags(pages, 'feature', 5)
        self.assertEqual(len(stale), 245)
        self.assertEqual(stale[0], 'v1.245.0-beta')
        self.assertNotIn('tip-999', stale)
        self.assertEqual(set(stale), {f'v1.{n}.0-beta' for n in range(1, 246)})
        with self.assertRaises(ValueError): retention.stale_tags(pages, 'tip', 5)
        self.assertEqual(retention.stale_tags(pages, 'feature', 300), [])
        with self.assertRaises(ValueError): retention.stale_tags(pages, 'feature', 0)

    def test_retention_uses_publication_time(self):
        for channel, keep, suffix in (('main', 10, ''), ('feature', 5, '-beta')):
            releases = [dict(id=n, tag_name=f'v1.{n + 1000}.0{suffix}',
                             prerelease=channel == 'feature', draft=False,
                             created_at=f'2026-09-{n:02}T00:00:00Z',
                             published_at=f'2026-09-{keep+2-n:02}T00:00:00Z')
                        for n in range(1, keep + 2)]
            self.assertEqual(retention.stale_tags([releases], channel, keep),
                             [f'v1.{keep+1001}.0{suffix}'])

    def test_main_retention_keeps_ten_and_isolates_channels(self):
        def release(number, tag=None, **flags):
            return dict(id=number, tag_name=tag or f'v1.{number}.0',
                        published_at='2026-09-05T00:00:00Z',
                        prerelease=False, draft=False) | flags
        pages = [[release(n) for n in range(1, 151)],
                 [release(n) for n in range(151, 251)]]
        pages[0].append(release(0, 'v2026.09.01.100'))
        pages[1] += [release(1000, draft=True), release(1001, prerelease=True),
                     release(1002, 'v1.1002.0-beta', prerelease=True),
                     release(1003, 'feature-legacy', prerelease=True),
                     release(1004, 'tip-legacy', prerelease=True),
                     release(1005, 'v1.2.3'), release(1006, 'v2026.09.21.123', prerelease=True),
                     release(1007, 'guest-v17'), release(1008, 'v1.0.0'),
                     release(1009, 'v1.0.1'), release(1010, 'v1.1.0')]
        stale = retention.stale_tags(pages, 'main', 10)
        self.assertEqual(stale, [f'v1.{n}.0' for n in range(240, 1, -1)])
        self.assertEqual(retention.stale_tags([pages[0][:10]], 'main', 10), [])
        self.assertEqual(retention.stale_tags([pages[0][:9]], 'main', 10), [])
        self.assertEqual(retention.stale_tags([], 'main', 10), [])
        with self.assertRaises(ValueError): retention.stale_tags(pages, 'main', 0)

    def publish(self, ref, tag, exists, prerelease='false', archives=None, success=True):
        # Exercise the real publisher with a recording CLI; never contact GitHub.
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            gh = folder / 'gh'
            gh.write_text('#!/usr/bin/env python3\n'
                          'import json, os, sys\n'
                          'with open(os.environ["GH_CALLS"], "a") as log:\n'
                          '    log.write(json.dumps(sys.argv[1:]) + "\\n")\n'
                          'if sys.argv[1:3] == ["release", "view"]:\n'
                          '    sys.exit(0 if os.environ["GH_EXISTS"] == "true" else 1)\n')
            gh.chmod(0o755)
            version = tag.removeprefix('v')
            if archives is None:
                archives = [f'gatOS-{version}.zip']
            for name in archives:
                (folder / name).write_bytes(b'zip fixture')
            log = folder / 'calls.jsonl'
            env = dict(os.environ, PATH=str(folder) + os.pathsep + os.environ['PATH'],
                       GH_CALLS=str(log), GH_EXISTS=str(exists).lower(), GITHUB_REF=ref,
                       GITHUB_REPOSITORY='example/repo', GITHUB_SHA='abc123',
                       GATOS_DIST_DIR=str(folder), VERSION=version, TAG=tag,
                       TITLE='gatOS ' + tag, PRERELEASE=prerelease)
            result = subprocess.run(['bash', 'tools/publish-release.sh'], cwd=ROOT,
                                    env=env, text=True, capture_output=True)
            self.assertEqual(result.returncode == 0, success, result.stderr)
            return [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []

    def test_main_publish_and_rerun_never_delete(self):
        tag = 'v1.123.0'
        calls = self.publish('refs/heads/main', tag, exists=False)
        self.assertEqual([call[1] for call in calls], ['view', 'create'])
        self.assertNotIn('--prerelease', calls[-1])
        self.assertTrue(calls[-1][3].endswith('/gatOS-1.123.0.zip'))
        self.assertIn('--notes-file', calls[-1])
        self.assertIn('--generate-notes', calls[-1])
        calls = self.publish('refs/heads/main', tag, exists=True)
        self.assertEqual([call[1] for call in calls], ['view'])

    def test_feature_publish_and_rerun(self):
        tag = 'v1.123.0-beta'
        calls = self.publish('refs/heads/feature/test', tag,
                             exists=False, prerelease='true')
        self.assertEqual([call[1] for call in calls], ['view', 'create'])
        self.assertIn('--prerelease', calls[-1])
        self.assertIn('--latest=false', calls[-1])
        calls = self.publish('refs/heads/feature/test', tag,
                             exists=True, prerelease='true')
        self.assertEqual([call[1] for call in calls], ['view'])

    def test_publish_rejects_missing_wrong_or_multiple_archives(self):
        for archives in ([], ['gatOS-windows-1.123.0.zip'],
                         ['gatOS-1.123.0.zip', 'gatOS-linux-1.123.0.zip']):
            with self.subTest(archives=archives):
                self.assertEqual(self.publish('refs/heads/main', 'v1.123.0', False,
                                              archives=archives, success=False), [])


if __name__ == '__main__':
    unittest.main()
