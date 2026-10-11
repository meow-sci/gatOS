#!/usr/bin/env python3
"""Offline tests of guest publication and both real download scripts."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ('base.qcow2', 'vmlinuz-virt', 'initramfs-virt', 'manifest.toml',
          'id_ed25519', 'id_ed25519.pub', 'host_key_fingerprint.txt', 'sha256sums.txt')
DESTINATION = 'meow-sci/gatOS-guest'


class GuestReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.guest = self.root / 'guest'
        self.guest.mkdir()
        (self.root / 'tools').mkdir()
        for name in ('GUEST_REPOSITORY', 'fetch-guest.sh', 'fetch-guest.ps1'):
            shutil.copyfile(ROOT / 'guest' / name, self.guest / name)
        (self.guest / 'GUEST_VERSION').write_text('17\n')
        shutil.copyfile(ROOT / 'tools/publish-guest.sh', self.root / 'tools/publish-guest.sh')
        self.assets = self.root / 'assets'
        self.assets.mkdir()
        for name in ASSETS[:-1]:
            (self.assets / name).write_text('fixture ' + name + '\n')
        (self.assets / 'manifest.toml').write_text('guest_version = 17\nalpine_version = "3.24.1"\n')
        (self.assets / 'sha256sums.txt').write_text(''.join(
            hashlib.sha256((self.assets / name).read_bytes()).hexdigest() + '  ' + name + '\n'
            for name in ASSETS[:-1]))
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.log = self.root / 'calls.jsonl'
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'],
                        GUEST_TEST_CALLS=str(self.log), GUEST_TEST_ASSETS=str(self.assets))
        for name in ('GH_TOKEN', 'GITHUB_TOKEN', 'GATOS_GUEST_REPO'):
            self.env.pop(name, None)

    def stub(self, name, body):
        path = self.bin / name
        path.write_text('#!/usr/bin/env python3\nimport json, os, pathlib, shutil, sys\n' + body)
        path.chmod(0o755)

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def publish(self, existing=False, api_failure=False, token=True):
        if not (self.guest / 'out').exists():
            shutil.copytree(self.assets, self.guest / 'out')
        self.stub('gh', '''args = sys.argv[1:]
record = {'args': args}
if '--notes-file' in args:
    record['notes'] = pathlib.Path(args[args.index('--notes-file') + 1]).read_text()
with open(os.environ['GUEST_TEST_CALLS'], 'a') as log:
    log.write(json.dumps(record) + '\\n')
if args[0] == 'api':
    if os.environ['GUEST_TEST_API_FAILURE'] == '1':
        sys.exit(1)
    if os.environ['GUEST_TEST_EXISTS'] == '1':
        print('guest-v17')
''')
        env = dict(self.env, GITHUB_REPOSITORY='meow-sci/gatOS', GITHUB_SHA='abc123',
                   GITHUB_RUN_ID='456', GUEST_TEST_EXISTS=str(int(existing)),
                   GUEST_TEST_API_FAILURE=str(int(api_failure)))
        if token:
            env['GH_TOKEN'] = 'test-only-token'
        return subprocess.run(['bash', str(self.root / 'tools/publish-guest.sh')],
                              env=env, capture_output=True, text=True)

    def test_new_release_destination_assets_and_provenance(self):
        self.assertEqual(self.publish().returncode, 0)
        lookup, create = self.calls()
        self.assertIn('repos/' + DESTINATION + '/releases?per_page=100', lookup['args'])
        self.assertIn('--paginate', lookup['args'])
        self.assertEqual(create['args'][:3], ['release', 'create', 'guest-v17'])
        self.assertEqual(create['args'][3:11], list(ASSETS))
        self.assertEqual(create['args'][create['args'].index('--repo') + 1], DESTINATION)
        self.assertNotIn('--target', create['args'])
        self.assertIn('https://github.com/meow-sci/gatOS/commit/abc123', create['notes'])
        self.assertIn('https://github.com/meow-sci/gatOS/actions/runs/456', create['notes'])

    def test_existing_release_replaces_assets_and_refreshes_provenance(self):
        self.assertEqual(self.publish(existing=True).returncode, 0)
        calls = self.calls()
        self.assertEqual([call['args'][0:2] for call in calls[1:]],
                         [['release', 'upload'], ['release', 'edit']])
        self.assertIn('--clobber', calls[1]['args'])
        for call in calls[1:]:
            self.assertEqual(call['args'][call['args'].index('--repo') + 1], DESTINATION)

    def test_api_error_never_attempts_publication(self):
        self.assertNotEqual(self.publish(api_failure=True).returncode, 0)
        self.assertEqual(len(self.calls()), 1)

    def test_missing_token_fails_before_api(self):
        self.assertNotEqual(self.publish(token=False).returncode, 0)
        self.assertEqual(self.calls(), [])

    def test_source_repository_is_rejected_case_insensitively(self):
        (self.guest / 'GUEST_REPOSITORY').write_text('MEOW-SCI/GATOS\n')
        self.assertNotEqual(self.publish().returncode, 0)
        self.assertEqual(self.calls(), [])

    def test_corrupt_asset_fails_before_api(self):
        (self.assets / 'base.qcow2').write_text('corrupted')
        self.assertNotEqual(self.publish().returncode, 0)
        self.assertEqual(self.calls(), [])

    def test_missing_asset_fails_before_api(self):
        (self.assets / 'vmlinuz-virt').unlink()
        self.assertNotEqual(self.publish().returncode, 0)
        self.assertEqual(self.calls(), [])

    def test_download_override_does_not_redirect_publication(self):
        self.env['GATOS_GUEST_REPO'] = 'test/guest-mirror'
        self.assertEqual(self.publish().returncode, 0)
        create = self.calls()[-1]['args']
        self.assertEqual(create[create.index('--repo') + 1], DESTINATION)

    def fetch(self, shell, override=None):
        env = self.env.copy()
        if override:
            env['GATOS_GUEST_REPO'] = override
        if shell == 'bash':
            self.stub('gh', 'sys.exit("Fetching public guests must not need gh")\n')
            self.stub('curl', '''args = sys.argv[1:]
url = args[-1]
with open(os.environ['GUEST_TEST_CALLS'], 'a') as log:
    log.write(json.dumps(url) + '\\n')
shutil.copyfile(pathlib.Path(os.environ['GUEST_TEST_ASSETS']) / url.rsplit('/', 1)[-1],
                args[args.index('-o') + 1])
''')
            command = ['bash', str(self.guest / 'fetch-guest.sh')]
        else:
            if not shutil.which('pwsh'):
                if os.environ.get('GITHUB_ACTIONS') == 'true':
                    self.fail('CI must have pwsh installed to test both fetch scripts')
                self.skipTest('pwsh is not installed; CI runs both fetch scripts')
            wrapper = self.root / 'fetch-test.ps1'
            wrapper.write_text('''function Invoke-WebRequest {
    param($Uri, $OutFile, [switch]$UseBasicParsing)
    ConvertTo-Json -Compress $Uri | Add-Content $env:GUEST_TEST_CALLS
    Copy-Item (Join-Path $env:GUEST_TEST_ASSETS ($Uri.Split('/')[-1])) $OutFile
}
& (Join-Path $PSScriptRoot 'guest/fetch-guest.ps1')
''')
            command = ['pwsh', '-NoProfile', '-File', str(wrapper)]
        return subprocess.run(command, env=env, capture_output=True, text=True)

    def check_fetch(self, shell, override=None, corrupt=False):
        if corrupt:
            (self.assets / 'base.qcow2').write_text('corrupted')
        result = self.fetch(shell, override)
        self.assertEqual(result.returncode == 0, not corrupt, result.stdout + result.stderr)
        expected_repo = override or DESTINATION
        self.assertEqual(set(self.calls()), {
            f'https://github.com/{expected_repo}/releases/download/guest-v17/{name}' for name in ASSETS})
        if corrupt:
            self.assertFalse((self.guest / 'out').exists())
        else:
            for name in ASSETS:
                self.assertEqual((self.guest / 'out' / name).read_bytes(), (self.assets / name).read_bytes())
            self.assertEqual(self.fetch(shell, override).returncode, 0)
            self.assertEqual(len(self.calls()), 8, 'cache hit must not download')

    def test_bash_public_fetch_and_cache(self): self.check_fetch('bash')
    def test_bash_override(self): self.check_fetch('bash', 'test/guest-mirror')
    def test_bash_checksum_failure(self): self.check_fetch('bash', corrupt=True)
    def test_powershell_public_fetch_and_cache(self): self.check_fetch('pwsh')
    def test_powershell_override(self): self.check_fetch('pwsh', 'test/guest-mirror')
    def test_powershell_checksum_failure(self): self.check_fetch('pwsh', corrupt=True)


if __name__ == '__main__':
    unittest.main()
