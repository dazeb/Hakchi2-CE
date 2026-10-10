#!/usr/bin/env python3
"""Check bundle relocation, embedded permissions and actual native library loading."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import tarfile
import unittest

parser = argparse.ArgumentParser()
parser.add_argument('--binary', required=True)
options, test_args = parser.parse_known_args()
binary = Path(options.binary).resolve()


class AppImageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix='hakchi appimage ')
        cls.root = Path(cls.temporary.name)
        cls.image = cls.root / 'Hakchi CLI.AppImage'
        shutil.copy2(binary, cls.image)
        cls.image.chmod(0o755)
        subprocess.run([str(cls.image), '--appimage-extract'], cwd=cls.root,
                       check=True, stdout=subprocess.DEVNULL, timeout=60)
        cls.appdir = cls.root / 'squashfs-root'

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def test_relative_paths_and_bundled_payload_after_relocation(self):
        (self.root / 'game.nes').write_bytes(b'NES\x1a' + bytes(32780))
        result = subprocess.run([str(self.image), 'game-add', 'game.nes', 'games',
                                 '--core', 'fceumm', '--name', 'AppImage Test'],
                                cwd=self.root, capture_output=True, check=True, timeout=60)
        game = Path(result.stdout.decode().strip())
        if not game.is_absolute():
            game = self.root / game
        self.assertEqual((game / (game.name + '.png')).read_bytes(),
                         (self.appdir / 'usr/lib/hakchi/payloads/blank_app.png').read_bytes())
        listing = subprocess.run([str(self.image), 'game-list', 'games'], cwd=self.root,
                                 capture_output=True, check=True, timeout=60)
        self.assertIn(b'AppImage Test', listing.stdout)

    def test_embedded_usb_rule(self):
        result = subprocess.run([str(self.image), '--print-udev-rules'],
                                capture_output=True, check=True, timeout=60)
        self.assertEqual(result.stdout,
                         (self.appdir / 'usr/lib/hakchi/70-hakchi.rules').read_bytes())
        self.assertIn(b'1f3a', result.stdout)

    def test_usb_enumeration_loads_bundled_libraries(self):
        env = dict(os.environ, LD_DEBUG='libs')
        env.pop('APPDIR', None)
        result = subprocess.run([str(self.appdir / 'AppRun'), 'devices'], env=env,
                                cwd=self.root, capture_output=True, timeout=15)
        # devices returns 3 when enumeration succeeds but no Classic is attached.
        self.assertIn(result.returncode, (0, 3), result.stderr)
        for name in ('libusb-1.0.so.0', 'libudev.so.1'):
            self.assertIn(b'calling init: ' + str(self.appdir / 'usr/lib' / name).encode(), result.stderr)

    def test_desktop_entry_and_distribution_files(self):
        self.assertIn('Terminal=false', (self.appdir / 'hakchi.desktop').read_text())
        for name in ('payloads/fes1.bin', 'README.md', 'LICENSE'):
            self.assertTrue((self.appdir / 'usr/lib/hakchi' / name).is_file(), name)
        for name in ('LGPL-2.1', 'libusb-copyright', 'libudev-copyright',
                     'LibUsbDotNet-LICENSE', 'AppImage-runtime-LICENSE',
                     'dotnet-LICENSE.TXT', 'dotnet-THIRD-PARTY-NOTICES.TXT'):
            self.assertTrue((self.appdir / 'usr/share/doc/hakchi/licenses' / name).is_file(), name)
        for name in ('hakchi-desktop', 'hakchi-desktop.dll', 'README.md'):
            self.assertTrue((self.appdir / 'usr/lib/hakchi-desktop' / name).is_file(), name)
        self.assertTrue((self.appdir / 'usr/share/doc/hakchi/licenses/frontend/Avalonia-MIT.txt').is_file())
        self.assertTrue((self.appdir / 'usr/share/doc/hakchi/licenses/frontend/DEPENDENCIES.txt').is_file())

    def test_missing_desktop_keeps_cli_available(self):
        env = dict(os.environ)
        env.pop('DISPLAY', None)
        result = subprocess.run([str(self.appdir / 'AppRun')], env=env, capture_output=True, timeout=15)
        self.assertEqual(result.returncode, 1)
        self.assertIn(b'X11 or XWayland', result.stderr)
        result = subprocess.run([str(self.appdir / 'AppRun'), '--help'], env=env, capture_output=True, timeout=15)
        self.assertEqual(result.returncode, 0)
        self.assertIn(b'game-add', result.stdout)

    def test_snes_modules_and_notices_are_bundled(self):
        docs = self.appdir / 'usr/share/doc/hakchi/snes-modules'
        manifest = json.loads((docs / 'manifest.json').read_text())
        payload = self.appdir / 'usr/lib/hakchi/payloads/snes-modules'
        for module in manifest['modules']:
            path = payload / module['file']
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), module['sha256'])
            with tarfile.open(path, 'r:gz') as archive:
                names = {name.removeprefix('./') for name in archive.getnames()}
                self.assertIn('install', names)
                self.assertIn('readme.md', names)
                if 'snes9x' in module['file']:
                    self.assertIn('bin/snes9x', names)
                    self.assertIn('etc/libretro/core/km_snes9x_libretro.so', names)
                else:
                    self.assertIn('bin/retroarch', names)
                    self.assertIn('bin/retroarch-clover', names)
        for name in ('KMFD-LGPL-3.0.txt', 'RetroArch-GPL-3.0.txt', 'Snes9x-LICENSE.txt'):
            self.assertTrue((docs / 'licenses' / name).is_file(), name)

    def test_corrupt_second_module_is_rejected_before_connection(self):
        path = self.appdir / 'usr/lib/hakchi/payloads/snes-modules/_km_snes9x_xtreme.hmod'
        original = path.read_bytes()
        try:
            path.write_bytes(original + b'corrupted')
            adapter = self.root / 'snes-adapter'
            adapter.mkdir()
            marker = adapter / 'contacted'
            ssh = adapter / 'ssh'
            ssh.write_text('#!/bin/sh\n' + 'touch "' + str(marker) + '"\nexit 99\n')
            ssh.chmod(0o755)
            env = dict(os.environ, PATH=str(adapter) + os.pathsep + os.environ['PATH'])
            result = subprocess.run([str(self.appdir / 'AppRun'), '--host', 'fixture', 'snes-modules-install', '--yes'],
                                    env=env, capture_output=True, timeout=15)
            self.assertEqual(result.returncode, 1)
            self.assertIn(b'SHA-256 check', result.stderr)
            self.assertFalse(marker.exists(), 'A module was installed before validating the entire set')
        finally:
            path.write_bytes(original)

    def test_stock_installer_has_pinned_firmware_and_base_modules(self):
        manifest = json.loads((self.appdir / 'usr/share/doc/hakchi/firmware.json').read_text())
        payload = self.appdir / 'usr/lib/hakchi/payloads'
        self.assertEqual(hashlib.sha256((payload / 'hakchi.hmod').read_bytes()).hexdigest(), manifest['sha256'])
        self.assertTrue((payload / 'base-modules/clovercon.hmod/install').is_file())
        self.assertTrue((payload / 'base-modules/copyleft.hmod').is_dir())
        result = subprocess.run([str(self.image), 'firmware-install', str(self.root / 'backup')],
                                capture_output=True, timeout=15)
        self.assertEqual(result.returncode, 2)
        self.assertIn(b'--yes', result.stderr)
        self.assertFalse((self.root / 'backup').exists())
        # The checked archive must also parse through the runtime loader. Stop
        # at a fixture SSH adapter before any actual console can be contacted.
        adapter = self.root / 'adapter'
        adapter.mkdir()
        ssh = adapter / 'ssh'
        ssh.write_text("#!/bin/sh\nprintf 'fixture connection refused\\n' >&2\nexit 1\n")
        ssh.chmod(0o755)
        env = dict(os.environ, PATH=str(adapter) + os.pathsep + os.environ['PATH'])
        result = subprocess.run([str(self.image), 'firmware-check', str(self.root / 'backup')],
                                env=env, capture_output=True, timeout=15)
        self.assertEqual(result.returncode, 1)
        self.assertIn(b'fixture connection refused', result.stderr)
        self.assertFalse((self.root / 'backup').exists())


if __name__ == '__main__':
    unittest.main(argv=[__file__, *test_args])
