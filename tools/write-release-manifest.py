"""Writes a published candidate's readable build identity and its source and published-file manifest.

Usage:
  python -I tools/write-release-manifest.py --package <publish folder> [--evidence <folder>] [--checkpoint <text>]
  python -I tools/write-release-manifest.py --package <publish folder> --verify <manifest.json>

Writing refuses when tracked application inputs have uncommitted changes, when the published application
assemblies do not carry the HEAD commit the SDK stamps into their version, or when a deployed Python
script differs from its source. Verify reports every published file that differs from a manifest.
"""
import argparse
import hashlib
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[1]
APPLICATION_INPUTS = ['UniversalMediaOS.Core', 'UniversalMediaOS.WPF', 'Directory.Build.props', 'global.json']
GENERATED = {'BUILD-IDENTITY.txt', 'source-build-manifest.json'}
STAMPED_ASSEMBLIES = ['UniversalMediaOS.WPF.dll', 'UniversalMediaOS.Core.dll']
EXTERNAL_RUNTIMES = ('.NET 9 Windows Desktop runtime, WebView2 runtime, FFmpeg on PATH, Python 3.12 with '
                     'pinned scraper packages installed on demand, optional qBittorrent')


class ManifestError(Exception):
    pass


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def is_managed_pe(path):
    """True for a .NET assembly, False for a native PE file, None when the file is not a PE image."""
    with path.open('rb') as stream:
        data = stream.read(65536)
    if len(data) < 64 or data[:2] != b'MZ':
        return None
    header = int.from_bytes(data[60:64], 'little')
    if data[header:header + 4] != b'PE\0\0':
        return None
    optional = header + 24
    magic = int.from_bytes(data[optional:optional + 2], 'little')
    directories = optional + {0x10B: 96, 0x20B: 112}.get(magic, 0)
    if directories == optional:
        return None
    # The CLR runtime header is data directory 14; NumberOfRvaAndSizes precedes the directories.
    if int.from_bytes(data[directories - 4:directories], 'little') <= 14:
        return False
    clr = directories + 14 * 8
    return int.from_bytes(data[clr:clr + 4], 'little') != 0 and int.from_bytes(data[clr + 4:clr + 8], 'little') != 0


def classify(relative, path):
    name = relative.lower()
    if name.endswith('.py'):
        return 'script'
    if Path(name).name.startswith('universalmediaos.'):
        return 'application'
    if name.endswith(('.dll', '.exe')):
        managed = is_managed_pe(path)
        return 'managed-dependency' if managed else 'native' if managed is False else 'asset'
    if name.endswith('.pdb'):
        return 'symbols'
    return 'asset'


def published_files(package):
    files = []
    for path in sorted(package.rglob('*')):
        relative = path.relative_to(package).as_posix()
        if path.is_file() and relative not in GENERATED:
            files.append({'path': relative, 'bytes': path.stat().st_size, 'sha256': digest(path),
                          'category': classify(relative, path)})
    return files


def check_scripts(package, repository):
    for script in sorted(package.glob('*.py')):
        source = repository / 'UniversalMediaOS.Core' / script.name
        if not source.is_file():
            raise ManifestError(f'Published script has no source: {script.name}')
        if digest(script) != digest(source):
            raise ManifestError(f'Published script differs from its source: {script.name}')


def check_stamp(package, source):
    for name in STAMPED_ASSEMBLIES:
        assembly = package / name
        if not assembly.is_file() or f'+{source}'.encode('ascii') not in assembly.read_bytes():
            raise ManifestError(f'{name} was not built from {source}; publish this source first.')


def git(repository, *args):
    return subprocess.check_output(['git', *args], cwd=repository, text=True).strip()


def build(package, repository, checkpoint, recorded):
    source = git(repository, 'rev-parse', 'HEAD')
    if git(repository, 'status', '--porcelain', '--untracked-files=no', '--', *APPLICATION_INPUTS):
        raise ManifestError('Tracked application inputs have uncommitted changes; commit them and publish again.')
    check_stamp(package, source)
    check_scripts(package, repository)
    inputs = [{'path': name, 'bytes': (repository / name).stat().st_size, 'sha256': digest(repository / name)}
              for name in git(repository, 'ls-files', '--', *APPLICATION_INPUTS).splitlines()
              if (repository / name).is_file()]
    files = published_files(package)
    config = json.loads((package / 'UniversalMediaOS.WPF.runtimeconfig.json').read_text(encoding='utf-8'))
    frameworks = [f"{framework['name']} {framework['version']}" for framework in config['runtimeOptions']['frameworks']]
    sdk = json.loads((repository / 'global.json').read_text(encoding='utf-8'))['sdk']['version']
    counts = {}
    for entry in files:
        counts[entry['category']] = counts.get(entry['category'], 0) + 1
    manifest = {
        'schema': 1, 'buildSource': source, 'committedUtc': git(repository, 'show', '-s', '--format=%cI', source),
        'recordedUtc': recorded, 'sdk': sdk, 'selfContained': False, 'runtimeFrameworks': frameworks,
        'externalRuntimes': EXTERNAL_RUNTIMES, 'checkpoint': checkpoint,
        'publishedBytes': sum(entry['bytes'] for entry in files), 'publishedCounts': dict(sorted(counts.items())),
        'sourceInputs': inputs, 'publishedFiles': files, 'manifestSelfHashExcluded': True,
    }
    identity = '\n'.join([
        'UniversalMediaOS recovery candidate',
        f'Source: {source} (committed {manifest["committedUtc"]})',
        f'Built with SDK {sdk}; framework-dependent: {", ".join(frameworks)}',
        f'Published files: {len(files)} ({manifest["publishedBytes"]:,} bytes): ' +
        ', '.join(f'{count} {category}' for category, count in manifest['publishedCounts'].items()),
        f'Source inputs: {len(inputs)}; deployed scripts match their source',
        'Per-file SHA-256: source-build-manifest.json (its own hash excluded)',
        f'Hosted checkpoint: {checkpoint}',
        f'Not bundled: {EXTERNAL_RUNTIMES}',
        'This candidate is not final release acceptance.',
        ''])
    return manifest, identity


def verify(package, manifest):
    expected = {entry['path']: entry for entry in manifest['publishedFiles']}
    actual = {entry['path']: entry for entry in published_files(package)}
    problems = [f'missing {path}' for path in sorted(expected.keys() - actual.keys())]
    problems += [f'unlisted {path}' for path in sorted(actual.keys() - expected.keys())]
    problems += [f'changed {path}' for path in sorted(expected.keys() & actual.keys())
                 if expected[path]['sha256'] != actual[path]['sha256']]
    return problems


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--package', required=True, type=Path)
    parser.add_argument('--evidence', type=Path)
    parser.add_argument('--checkpoint', default='pending')
    parser.add_argument('--verify', type=Path)
    options = parser.parse_args(argv)
    package = options.package.resolve()
    if options.verify:
        problems = verify(package, json.loads(options.verify.read_text(encoding='utf-8-sig')))
        print(json.dumps({'unchanged': not problems, 'problems': problems}, indent=2))
        return 0 if not problems else 1
    try:
        manifest, identity = build(package, REPOSITORY, options.checkpoint,
                                   datetime.now(timezone.utc).isoformat(timespec='seconds'))
    except ManifestError as error:
        print(f'Refused: {error}', file=sys.stderr)
        return 2
    serialized = json.dumps(manifest, indent=2)
    (package / 'BUILD-IDENTITY.txt').write_text(identity, encoding='utf-8')
    (package / 'source-build-manifest.json').write_text(serialized, encoding='utf-8')
    if options.evidence:
        options.evidence.mkdir(parents=True, exist_ok=True)
        short = manifest['buildSource'][:7]
        (options.evidence / f'{short}-manifest.json').write_text(serialized, encoding='utf-8')
        (options.evidence / f'{short}-BUILD-IDENTITY.txt').write_text(identity, encoding='utf-8')
    print(identity, end='')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
