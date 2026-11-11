"""run-e2e-tests.ps1 finishes and returns dotnet's exit code when Windows PowerShell redirects its output."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

LAUNCHER = Path(__file__).resolve().parents[2] / 'run-e2e-tests.ps1'


@unittest.skipUnless(os.name == 'nt' and shutil.which('powershell'), 'Windows PowerShell 5.1 only')
class E2eLauncherTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        root = Path(self.temporary.name)
        # A stand-in dotnet that writes to both streams, as a failing test run does, and exits with 3.
        self.tools = root / 'tools'
        self.tools.mkdir()
        (self.tools / 'dotnet.cmd').write_text(
            '@echo off\r\necho stdout-line\r\necho stderr-line 1>&2\r\nexit /b 3\r\n', encoding='ascii')
        self.artifacts = root / 'artifacts'
        assembly = self.artifacts / 'bin' / 'UniversalMediaOS.Tests.E2E' / 'release' / 'UniversalMediaOS.Tests.E2E.dll'
        assembly.parent.mkdir(parents=True)
        assembly.write_bytes(b'stand-in')
        self.executable = root / 'UniversalMediaOS.WPF.exe'
        self.executable.write_bytes(b'stand-in')
        self.output = root / 'launcher.txt'

    def tearDown(self):
        self.temporary.cleanup()

    def test_redirected_output_keeps_both_streams_and_the_exit_code(self):
        def quote(path):
            return "'" + str(path).replace("'", "''") + "'"
        command = (f"& {quote(LAUNCHER)} -Configuration Release -NoBuild -ArtifactsPath {quote(self.artifacts)} "
                   f"-ExecutablePath {quote(self.executable)} -Filter 'FullyQualifiedName~Anything' *> {quote(self.output)}; "
                   "exit $LASTEXITCODE")
        environment = dict(os.environ, PATH=str(self.tools) + os.pathsep + os.environ.get('PATH', ''))
        result = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command],
                                env=environment, capture_output=True, text=True, timeout=120)
        written = self.output.read_text(encoding='utf-16', errors='replace') if self.output.exists() else ''
        self.assertEqual(3, result.returncode, written + result.stdout + result.stderr)
        self.assertIn('stdout-line', written)
        self.assertIn('stderr-line', written)


if __name__ == '__main__':
    unittest.main()
