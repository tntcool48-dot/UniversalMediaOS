"""The release manifest classifies published files from their own headers and refuses mismatched sources."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'write-release-manifest.py'
spec = importlib.util.spec_from_file_location('write_release_manifest', SCRIPT)
manifest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest)


def portable_executable(managed, magic=0x20B):
    data = bytearray(1024)
    data[0:2] = b'MZ'
    data[60:64] = (128).to_bytes(4, 'little')
    data[128:132] = b'PE\0\0'
    optional = 128 + 24
    data[optional:optional + 2] = magic.to_bytes(2, 'little')
    directories = optional + (112 if magic == 0x20B else 96)
    data[directories - 4:directories] = (16).to_bytes(4, 'little')
    if managed:
        clr = directories + 14 * 8
        data[clr:clr + 4] = (0x2000).to_bytes(4, 'little')
        data[clr + 4:clr + 8] = (72).to_bytes(4, 'little')
    return bytes(data)


class ReleaseManifestTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.package = self.root / 'publish'
        self.package.mkdir()

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, relative, content):
        path = self.package / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        return path

    def test_files_are_classified_from_their_own_headers(self):
        self.write('Newtonsoft.Json.dll', portable_executable(managed=True))
        self.write('runtimes/win-x64/native/e_sqlite3.dll', portable_executable(managed=False))
        self.write('libvlc/win-x86/libvlc.dll', portable_executable(managed=False, magic=0x10B))
        self.write('UniversalMediaOS.WPF.exe', portable_executable(managed=True))
        self.write('scraper.py', b'print("scraper")\n')
        self.write('notes.dll', b'not a portable executable')
        self.write('Assets/icon.png', b'\x89PNG')
        self.write('BUILD-IDENTITY.txt', b'generated')
        categories = {entry['path']: entry['category'] for entry in manifest.published_files(self.package)}
        self.assertEqual({
            'Assets/icon.png': 'asset', 'Newtonsoft.Json.dll': 'managed-dependency',
            'UniversalMediaOS.WPF.exe': 'application', 'libvlc/win-x86/libvlc.dll': 'native',
            'notes.dll': 'asset', 'runtimes/win-x64/native/e_sqlite3.dll': 'native', 'scraper.py': 'script',
        }, categories)

    def test_deployed_scripts_must_match_their_source(self):
        source = self.root / 'repository' / 'UniversalMediaOS.Core'
        source.mkdir(parents=True)
        (source / 'scraper.py').write_bytes(b'print("scraper")\n')
        self.write('scraper.py', b'print("scraper")\n')
        manifest.check_scripts(self.package, self.root / 'repository')
        self.write('scraper.py', b'print("changed")\n')
        with self.assertRaisesRegex(manifest.ManifestError, 'differs'):
            manifest.check_scripts(self.package, self.root / 'repository')
        self.write('scraper.py', b'print("scraper")\n')
        self.write('unknown.py', b'')
        with self.assertRaisesRegex(manifest.ManifestError, 'no source'):
            manifest.check_scripts(self.package, self.root / 'repository')

    def test_application_assemblies_must_carry_the_source_commit(self):
        source = '0123456789abcdef0123456789abcdef01234567'
        for name in manifest.STAMPED_ASSEMBLIES:
            self.write(name, b'metadata 1.0.0+' + source.encode('ascii') + b' rest')
        manifest.check_stamp(self.package, source)
        self.write('UniversalMediaOS.Core.dll', b'metadata 1.0.0+ffffffffffffffffffffffffffffffffffffffff')
        with self.assertRaisesRegex(manifest.ManifestError, 'UniversalMediaOS.Core.dll was not built'):
            manifest.check_stamp(self.package, source)

    def test_verify_reports_every_changed_missing_and_unlisted_file(self):
        self.write('a.dll', portable_executable(managed=True))
        self.write('b.json', b'{}')
        self.write('BUILD-IDENTITY.txt', b'first')
        recorded = {'publishedFiles': manifest.published_files(self.package)}
        self.assertEqual([], manifest.verify(self.package, recorded))
        self.write('BUILD-IDENTITY.txt', b'regenerated identity is not a published input')
        self.assertEqual([], manifest.verify(self.package, recorded))
        self.write('a.dll', portable_executable(managed=False))
        (self.package / 'b.json').unlink()
        self.write('c.txt', b'new')
        self.assertEqual(['missing b.json', 'unlisted c.txt', 'changed a.dll'],
                         manifest.verify(self.package, recorded))


if __name__ == '__main__':
    unittest.main()
