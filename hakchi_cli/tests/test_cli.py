#!/usr/bin/env python3
"""CLI regressions with real local sh/tar/files, using a fixture OpenSSH adapter."""
import argparse
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest

parser = argparse.ArgumentParser()
parser.add_argument('--binary', required=True)
options, test_args = parser.parse_known_args()
binary = str(Path(options.binary).resolve())
runner = [os.environ.get('DOTNET', 'dotnet'), binary] if binary.endswith('.dll') else [binary]


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='hakchi-test-')
        self.root = Path(self.temporary.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'], HAKCHI_TEST_ROOT=str(self.root))
        self.script('ssh', '''import json, os, sys
from pathlib import Path
root = Path(os.environ['HAKCHI_TEST_ROOT'])
assert sys.argv[-2] == 'root@fixture', sys.argv
assert '-T' in sys.argv and '-p' in sys.argv and '--' in sys.argv
command = sys.argv[-1].replace('/tmp/hakchi-cli-', str(root) + '/tmp/hakchi-cli-').replace('/var/version', str(root / 'version'))
with (root / 'commands').open('a') as log: log.write(json.dumps(command) + '\\n')
os.execv('/bin/sh', ['sh', '-c', command])
''')
        self.script('hakchi', '''import os, sys
from pathlib import Path
root = Path(os.environ['HAKCHI_TEST_ROOT'])
verb = sys.argv[1]
if verb == 'getBackup2': sys.stdout.buffer.write((root / 'stock.img').read_bytes())
elif verb == 'packs_install':
    modules = sorted(Path(sys.argv[2]).glob('*.hmod'))
    assert modules and all((p / 'install').is_file() for p in modules)
    (root / 'installed').write_text(','.join(p.name for p in modules))
elif verb == 'pack_uninstall': (root / 'uninstalled').write_text(sys.argv[2])
elif verb == 'currentFirmware': print('_nand_')
elif verb == 'findGameSyncStorage': print('/var/lib/hakchi/games')
elif verb not in ('eval', 'overmount_games'): sys.exit(99)
''')
        self.script('uistop', "import os\nfrom pathlib import Path\nPath(os.environ['HAKCHI_TEST_ROOT'], 'stopped').touch()\n")
        self.script('uistart', "import os\nfrom pathlib import Path\nPath(os.environ['HAKCHI_TEST_ROOT'], 'started').touch()\n")
        self.script('mv', '''import os, sys
from pathlib import Path
marker = Path(os.environ['HAKCHI_TEST_ROOT'], 'fail-commit')
if marker.exists() and '/000/CLV-' in sys.argv[-1] and 'previous-' not in sys.argv[-2]:
    marker.unlink()
    sys.exit(19)
os.execv('/bin/mv', ['mv'] + sys.argv[1:])
''')
        (self.root / 'tmp').mkdir()
        (self.root / 'stock.img').write_bytes(b'ANDROID!' + bytes(range(256)) * 20)
        (self.root / 'version').write_text('fixture firmware\n')

    def tearDown(self):
        self.temporary.cleanup()

    def script(self, name, body):
        path = self.bin / name
        path.write_text('#!' + sys.executable + '\n' + body)
        path.chmod(0o755)

    def call(self, *args, code=0, data=None, network=False, timeout=10):
        if network:
            args = ('--host', 'fixture', '--timeout', '5', *args)
        result = subprocess.run([*runner, *args], input=data, capture_output=True, env=self.env, timeout=timeout)
        self.assertEqual(result.returncode, code, (args, result.stdout, result.stderr))
        return result

    def game(self):
        rom = self.root / "game's name.sfc"
        rom.write_bytes(bytes(range(256)) * 100)
        library = self.root / 'games'
        result = self.call('game-add', str(rom), str(library), '--core', 'snes9x', '--name', "Game's name")
        return library, Path(result.stdout.decode().strip())

    def test_help_and_argument_errors(self):
        self.assertIn(b'game-add', self.call('--help').stdout)
        self.assertIn(b'read-nand', self.call('fel', '--help').stdout)
        self.call('bogus', code=2)
        self.call('--timeout', '0', 'status', code=2)
        self.call('--port', '65536', 'status', code=2)
        self.call('fel', 'flash-boot', '-u', 'missing', '-b', 'missing', code=2)
        self.call('fel', 'bogus', code=2)
        self.call('upload', 'missing', '/tmp/x', code=1)
        self.call('download', '/../../etc/x', 'output', code=2)

    def test_game_import_and_metadata(self):
        library, game = self.game()
        code = game.name
        desktop = (game / (code + '.desktop')).read_text()
        self.assertIn(f'Exec=/bin/snes9x /var/games/{code}/game.sfc', desktop)
        self.assertIn(f'Path=/var/saves/{code}', desktop)
        self.assertIn("Name=Game's name", desktop)
        self.assertEqual((game / 'game.sfc').read_bytes(), bytes(range(256)) * 100)
        self.assertIn(code.encode(), self.call('game-list', str(library)).stdout)
        self.call('game-add', str(self.root / "game's name.sfc"), str(library), '--core', 'snes9x', code=1)

    def test_fel_rejects_invalid_images_and_ranges_before_connecting(self):
        uboot = self.root / 'uboot.bin'
        uboot.write_bytes(b'fixture')
        boot = self.root / 'boot.img'
        boot.write_bytes(b'ANDROID!' + bytes(2040))  # zero page size
        self.call('fel', 'memboot', '-u', str(uboot), '-b', str(boot), code=2)
        self.call('fel', 'read-nand', '-u', str(uboot), '-a', '1', '-l', '0x20000', '-o', str(self.root / 'out'), code=2)
        self.call('fel', 'read-nand', '-u', str(uboot), '-a', '0', '-l', '1', '-o', str(self.root / 'out'), code=2)
        self.call('fel', 'flash-nand', '-u', str(uboot), '-a', '1', '-i', str(uboot), '--yes', code=2)
        self.call('fel', 'flash-uboot', '-u', str(uboot), '--all', '--yes', code=2)
        uboot.write_bytes(bytes(2 * 1024 * 1024 + 1))
        self.call('fel', 'flash-uboot', '-u', str(uboot), '--yes', code=2)

    def test_game_rejects_invalid_input_and_symlinks(self):
        library, game = self.game()
        (game / 'leak').symlink_to('/etc')
        self.call('sync', str(library), str(self.root / 'remote'), code=2, network=True)
        self.assertFalse((self.root / 'commands').exists())
        self.call('game-add', str(self.root / "game's name.sfc"), str(self.root / 'other'), '--core', 'bad; command', code=2)
        self.assertFalse((self.root / 'other').exists())

    def test_exec_streams_and_exit_code(self):
        data = bytes(range(256)) * 8192
        result = self.call('exec', "cat; printf 'problem' >&2; exit 17", code=17, data=data, network=True)
        self.assertEqual(result.stdout, data)
        self.assertEqual(result.stderr, b'problem')
        self.assertEqual(self.call('exec', 'cat', network=True).stdout, b'')

    def test_upload_download_binary_and_quoting(self):
        source = self.root / 'source'
        data = bytes(range(256)) * 1024
        source.write_bytes(data)
        remote = self.root / "remote file's data"
        self.call('upload', str(source), str(remote), network=True)
        self.assertEqual(remote.read_bytes(), data)
        destination = self.root / 'download'
        result = self.call('download', str(remote), str(destination), network=True)
        self.assertEqual(destination.read_bytes(), data)
        self.assertIn(b'SHA256 ', result.stderr)

    def test_download_failure_preserves_destination(self):
        destination = self.root / 'download'
        destination.write_bytes(b'old data')
        self.call('download', str(self.root / 'missing'), str(destination), code=1, network=True)
        self.assertEqual(destination.read_bytes(), b'old data')
        empty = self.root / 'empty'
        empty.touch()
        self.call('download', str(empty), str(destination), code=1, network=True)
        self.assertEqual(destination.read_bytes(), b'old data')
        self.assertEqual(list(self.root.glob('*.partial-*')), [])

    def test_backup(self):
        destination = self.root / 'backup.img'
        self.call('backup', str(destination), network=True)
        self.assertEqual(destination.read_bytes(), (self.root / 'stock.img').read_bytes())

    def test_module_file_and_directory(self):
        package = self.root / 'example.hmod'
        with tarfile.open(package, 'w:gz') as archive:
            info = tarfile.TarInfo('install')
            info.size = 11
            archive.addfile(info, io.BytesIO(b'#!/bin/sh\n\n'))
        self.call('mod-install', str(package), network=True)
        self.assertEqual((self.root / 'installed').read_text(), 'example.hmod')
        unpacked = self.root / 'unpacked.hmod'
        unpacked.mkdir()
        (unpacked / 'install').write_text('#!/bin/sh\n')
        self.call('mod-install', str(unpacked), network=True)
        self.assertEqual((self.root / 'installed').read_text(), 'unpacked.hmod')
        self.assertEqual(list((self.root / 'tmp').iterdir()), [])
        self.call('mod-uninstall', "name's mod", network=True)
        self.assertEqual((self.root / 'uninstalled').read_text(), "name's mod")

    def test_sync_preserves_games_and_replaces_named_game(self):
        library, game = self.game()
        remote = self.root / "remote games' data"
        old = remote / '000' / game.name
        old.mkdir(parents=True)
        (old / 'game.sfc').write_bytes(b'old')
        other = remote / '001' / 'unrelated'
        other.mkdir(parents=True)
        (other / 'keep').write_bytes(b'keep')
        self.call('sync', str(library), str(remote), network=True)
        self.assertEqual((old / 'game.sfc').read_bytes(), (game / 'game.sfc').read_bytes())
        self.assertEqual((other / 'keep').read_bytes(), b'keep')
        self.assertTrue((self.root / 'started').exists())
        self.assertEqual(list(remote.glob('.hakchi-cli-*')), [])

    def test_sync_rolls_back_failed_commit(self):
        library, game = self.game()
        remote = self.root / 'remote'
        old = remote / '000' / game.name
        old.mkdir(parents=True)
        (old / 'game.sfc').write_bytes(b'old')
        (self.root / 'fail-commit').touch()
        result = self.call('sync', str(library), str(remote), code=1, network=True)
        self.assertEqual((old / 'game.sfc').read_bytes(), b'old')
        self.assertTrue((self.root / 'started').exists())
        self.assertIn(b'Transfer interrupted', result.stderr)
        self.assertEqual(len(list(remote.glob('.hakchi-cli-*'))), 1)

    def test_command_timeout_and_cancellation(self):
        result = self.call('--host', 'fixture', '--timeout', '1', 'exec', 'exec sleep 30', code=1)
        self.assertIn(b'timed out', result.stderr)
        commands = self.root / 'commands'
        previous_size = commands.stat().st_size
        process = subprocess.Popen([*runner, '--host', 'fixture', 'exec', 'exec sleep 30'], env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        try:
            deadline = time.monotonic() + 10
            while commands.stat().st_size == previous_size and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertGreater(commands.stat().st_size, previous_size, 'Remote command did not start')
            # A terminal's Ctrl-C signals the whole foreground process group,
            # including an AppImage extract-and-run helper and its CLI child.
            os.killpg(process.pid, signal.SIGINT)
            _, error = process.communicate(timeout=8)
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.communicate()
        # The CLI returns 130; an extract-and-run helper can itself receive SIGINT.
        # Both become status 130 in a terminal shell. Require child cancellation too.
        self.assertIn(process.returncode, (130, -signal.SIGINT), error)
        self.assertIn(b'Cancelled.', error)


if __name__ == '__main__':
    unittest.main(argv=[sys.argv[0], *test_args])
