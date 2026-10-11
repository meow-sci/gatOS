#!/usr/bin/env python3
"""Offline distribution-validator checks; fixtures contain no real game or VM binaries."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('dist', Path(__file__).with_name('check-dist.py'))
dist = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dist)


class DistributionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / 'gatOS'
        files = ['gatOS.GameMod.dll', 'gatos.default.toml', 'LICENSE', 'THIRD-PARTY-NOTICES.md',
                 'purrTTY.CustomShellContract.dll', 'purrTTY.Logging.dll', 'Example.Dependency.dll']
        files += ['guest/' + name for name in dist.GUEST_FILES]
        files += ['qemu/win-x64/' + name for name in dist.qemu_files()]
        files += ['third-party-licenses/' + p.name
                  for p in (dist.REPO / 'third-party-licenses').glob('*') if p.is_file()]
        for path in dist.REPO.glob('gatOS.*/*.csproj'):
            if not path.stem.endswith('.Tests'):
                files.append((ET.parse(path).findtext('.//AssemblyName') or path.stem) + '.dll')
        for name in files:
            self.write(name, 'fixture')
        self.write('mod.toml', 'version = "1.123.0"\n[StarMap]\nEntryAssembly = "gatOS.GameMod"\n')
        self.write('guest/manifest.toml', 'guest_version = '
                   + (dist.REPO / 'guest/GUEST_VERSION').read_text().strip() + '\n')
        self.write('gatOS.GameMod.deps.json', json.dumps({
            'runtimeTarget': {'name': 'test'}, 'targets': {'test': {
                'dependency': {'runtime': {'lib/net10.0/Example.Dependency.dll': {}}},
                'loader': {'runtime': {'lib/net10.0/0Harmony.dll': {}, 'StarMap.API.dll': {}}},
            }}}))

    def write(self, name, contents):
        path = self.package / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(contents)

    def test_complete_shared_distribution(self):
        self.assertGreater(dist.validate(self.root, '1.123.0'), 100)

    def test_missing_guest_qemu_or_managed_dependency(self):
        for name in ('guest/base.qcow2', 'guest/id_ed25519', 'qemu/win-x64/qemu-img.exe',
                     'qemu/win-x64/share/bios-256k.bin', 'Example.Dependency.dll', 'gatOS.Ssh.dll'):
            with self.subTest(name=name):
                path = self.package / name
                contents = path.read_bytes()
                path.unlink()
                with self.assertRaisesRegex(ValueError, 'Missing or empty runtime file'):
                    dist.validate(self.root)
                path.write_bytes(contents)

    def test_empty_artifact(self):
        self.write('guest/base.qcow2', '')
        with self.assertRaisesRegex(ValueError, 'Missing or empty runtime file'):
            dist.validate(self.root)

    def test_game_and_loader_assemblies_rejected(self):
        for name in ('KSA.dll', 'Brutal.Core.Common.dll', 'BepuPhysics.dll',
                     '0Harmony.dll', 'StarMap.API.dll'):
            with self.subTest(name=name):
                self.write(name, 'fixture')
                with self.assertRaisesRegex(ValueError, 'game/loader assembly'):
                    dist.validate(self.root)
                (self.package / name).unlink()

    def test_version_and_guest_pin_must_match(self):
        with self.assertRaisesRegex(ValueError, 'Mod version'):
            dist.validate(self.root, '1.124.0')
        self.write('guest/manifest.toml', 'guest_version = 0\n')
        with self.assertRaisesRegex(ValueError, 'Guest image'):
            dist.validate(self.root)

    def test_extra_mod_and_player_data_rejected(self):
        self.write('other/mod.toml', 'fixture')
        with self.assertRaisesRegex(ValueError, 'one StarMap manifest'):
            dist.validate(self.root)
        (self.package / 'other/mod.toml').unlink()
        self.write('gatos.toml', 'player configuration')
        with self.assertRaisesRegex(ValueError, 'player data'):
            dist.validate(self.root)
        (self.package / 'gatos.toml').unlink()
        (self.root / 'other-mod').mkdir()
        with self.assertRaisesRegex(ValueError, 'only gatOS/'):
            dist.validate(self.root)


if __name__ == '__main__':
    unittest.main()
