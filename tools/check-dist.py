#!/usr/bin/env python3
"""Validate a freshly built shared distribution before release packaging."""
import argparse
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parent.parent
LOADER_ASSEMBLIES = {'0Harmony.dll', 'StarMap.API.dll'}
GUEST_FILES = ('base.qcow2', 'vmlinuz-virt', 'initramfs-virt', 'manifest.toml',
               'id_ed25519', 'id_ed25519.pub', 'host_key_fingerprint.txt', 'sha256sums.txt')


def qemu_files():
    return [line.strip() for line in (REPO / 'tools/qemu-win64-files.txt').read_text().splitlines()
            if line.strip() and not line.lstrip().startswith('#')]


def validate(root, version=''):
    package = root / 'gatOS'
    if sorted(p.name for p in root.iterdir()) != ['gatOS']:
        raise ValueError('Expected only gatOS/ in dist before packaging')
    if list(root.rglob('mod.toml')) != [package / 'mod.toml']:
        raise ValueError('Expected one StarMap manifest')
    manifest = (package / 'mod.toml').read_text()
    if not re.search(r'^EntryAssembly = "gatOS.GameMod"$', manifest, re.M):
        raise ValueError('Wrong StarMap entry assembly')
    if version and not re.search(r'^version = "' + re.escape(version) + r'"$', manifest, re.M):
        raise ValueError('Mod version does not match release version')

    required = {'gatOS.GameMod.dll', 'gatOS.GameMod.deps.json', 'gatos.default.toml',
                'LICENSE', 'THIRD-PARTY-NOTICES.md',
                'purrTTY.CustomShellContract.dll', 'purrTTY.Logging.dll'}
    # Follow project references rather than maintaining a second assembly list.
    pending = [REPO / 'gatOS.GameMod/gatOS.GameMod.csproj']
    visited = set()
    forbidden = set(LOADER_ASSEMBLIES)
    while pending:
        path = pending.pop().resolve()
        if path in visited:
            continue
        visited.add(path)
        project = ET.parse(path).getroot()
        required.add((project.findtext('.//AssemblyName') or path.stem) + '.dll')
        pending.extend(path.parent / ref.attrib['Include'].replace('\\', '/')
                       for ref in project.findall('.//ProjectReference'))
        forbidden.update(ref.attrib['Include'] + '.dll'
                         for ref in project.findall('.//Reference')
                         if (ref.findtext('Private') or '').lower() == 'false')

    deps = json.loads((package / 'gatOS.GameMod.deps.json').read_text())
    for library in deps['targets'][deps['runtimeTarget']['name']].values():
        required.update(Path(asset).name for asset in library.get('runtime', {})
                        if asset.endswith('.dll') and Path(asset).name not in LOADER_ASSEMBLIES)
    required.update('guest/' + name for name in GUEST_FILES)
    required.update('qemu/win-x64/' + name for name in qemu_files())
    required.update('third-party-licenses/' + p.name
                    for p in (REPO / 'third-party-licenses').glob('*') if p.is_file())
    for name in sorted(required):
        path = package / name
        if not path.is_file() or path.stat().st_size == 0:
            raise ValueError(f'Missing or empty runtime file: {name}')
    for path in package.glob('*.dll'):
        if path.name in forbidden:
            raise ValueError(f'Do not distribute game/loader assembly: {path.name}')
    for name in ('gatos.toml', 'disks', 'logs'):
        if (package / name).exists():
            raise ValueError(f'Do not distribute player data: {name}')
    pin = (REPO / 'guest/GUEST_VERSION').read_text().strip()
    if not re.search(r'^guest_version = ' + re.escape(pin) + r'$',
                     (package / 'guest/manifest.toml').read_text(), re.M):
        raise ValueError('Guest image does not match guest/GUEST_VERSION')
    return len(required)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dist', type=Path)
    parser.add_argument('--version', default='')
    args = parser.parse_args()
    try:
        count = validate(args.dist, args.version)
    except (ValueError, OSError) as error:
        parser.exit(1, f'Distribution validation failed: {error}\n')
    print(f'PASS: one gatOS mod, {count} required files, guest and Windows QEMU included')
