#!/usr/bin/env python3
"""Exercise the real no-argument AppImage window in an isolated X11 session."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time

parser = argparse.ArgumentParser()
parser.add_argument('--binary', required=True)
parser.add_argument('--screenshots')
options = parser.parse_args()
binary = Path(options.binary).resolve()

def command(*args, **kwargs):
    return subprocess.run(args, check=True, capture_output=True, text=True, timeout=20, **kwargs).stdout

with tempfile.TemporaryDirectory(prefix='hakchi desktop ') as temporary:
    root = Path(temporary)
    env = dict(os.environ, XDG_CONFIG_HOME=str(root / 'config'), XDG_DATA_HOME=str(root / 'data'),
               APPIMAGE_EXTRACT_AND_RUN='1')
    library = root / 'library with spaces'
    for index, name in enumerate(('Classic test game', 'Second test game')):
        rom = root / (name + '.nes')
        rom.write_bytes(b'NES\x1a' + bytes(12) + bytes([index + 1]) * 32768)
        command(str(binary), 'game-add', str(rom), str(library), '--core', 'fceumm', '--name', name, env=env)
    config = root / 'config/hakchi/preferences.json'
    config.parent.mkdir(parents=True)
    config.write_text(json.dumps({'Library': str(library), 'Core': 'fceumm'}))
    with (root / 'desktop.log').open('w+') as log:
        process = subprocess.Popen([str(binary)], env=env, stdout=log, stderr=log)
        try:
            window = None
            for _ in range(100):
                found = subprocess.run(['xdotool', 'search', '--onlyvisible', '--name', '^Hakchi Desktop$'],
                                       env=env, capture_output=True, text=True)
                if found.returncode == 0:
                    window = found.stdout.splitlines()[0]
                    break
                if process.poll() is not None:
                    log.seek(0)
                    raise AssertionError('Desktop exited: ' + log.read())
                time.sleep(0.1)
            assert window is not None, 'The default AppImage entry point did not open a frontend window'
            command('xdotool', 'windowfocus', '--sync', window, env=env)

            def keys(*values):
                command('xdotool', 'key', '--clearmodifiers', *values, env=env)

            def activity():
                keys('ctrl+shift+c')
                time.sleep(0.15)
                result = subprocess.run(['xclip', '-out', '-selection', 'clipboard'], env=env,
                                        capture_output=True, text=True, timeout=10)
                return result.stdout if result.returncode == 0 else ''

            def wait_for(text):
                for _ in range(60):
                    if text in activity(): return
                    time.sleep(0.1)
                log.seek(0)
                raise AssertionError('Activity did not contain ' + repr(text) + ': ' + activity() + '\n' + log.read())

            def screenshot(name):
                if options.screenshots:
                    output = Path(options.screenshots)
                    output.mkdir(parents=True, exist_ok=True)
                    command('import', '-window', window, str(output / (name + '.png')), env=env)

            time.sleep(0.5)
            screenshot('desktop-startup')
            wait_for('Second test game')
            assert json.loads(config.read_text())['Library'] == str(library)
            screenshot('desktop-library')
            print('PASS: no-argument graphical launch, real backend library read and persisted settings', flush=True)

            keys('ctrl+f')
            command('xdotool', 'type', '--clearmodifiers', 'no-matching-game', env=env)
            time.sleep(0.2)
            screenshot('desktop-search-empty')
            keys('ctrl+a', 'BackSpace')
            keys('ctrl+l', 'ctrl+a')
            command('xdotool', 'type', '--clearmodifiers', 'relative-path', env=env)
            keys('F5')
            wait_for('Choose an absolute local library directory.')
            screenshot('desktop-error')
            keys('ctrl+l', 'ctrl+a')
            command('xdotool', 'type', '--clearmodifiers', str(library), env=env)
            keys('F5')
            time.sleep(0.5)
            assert activity().count('Second test game') >= 2, 'Refresh did not recover after a settings error'
            assert process.poll() is None
            screenshot('desktop-recovered')
            print('PASS: search, invalid-location error and refresh recovery through the real window', flush=True)
            keys('ctrl+o')
            picker = None
            for _ in range(50):
                found = subprocess.run(['xdotool', 'search', '--onlyvisible', '--name', '^Add game ROMs$'],
                                       env=env, capture_output=True, text=True)
                if found.returncode == 0:
                    picker = found.stdout.splitlines()[0]
                    break
                time.sleep(0.1)
            assert picker is not None, 'The Add games action did not open a file picker: ' + activity()
            time.sleep(0.6)
            if options.screenshots:
                command('import', '-window', picker, str(Path(options.screenshots) / 'desktop-file-picker.png'), env=env)
            command('xdotool', 'windowfocus', '--sync', picker, env=env)
            rom = root / 'Third test game.nes'
            rom.write_bytes(b'NES\x1a' + bytes(12) + bytes([3]) * 32768)
            geometry = dict(line.split('=', 1) for line in command('xdotool', 'getwindowgeometry', '--shell', picker, env=env).splitlines())
            width, height = int(geometry['WIDTH']), int(geometry['HEIGHT'])
            command('xdotool', 'mousemove', '--window', picker, '400', '22', 'click', '1', env=env)
            keys('ctrl+a')
            command('xdotool', 'type', '--clearmodifiers', str(root), env=env)
            keys('Return')
            time.sleep(0.5)
            # This isolated folder has two folders and three ROMs. Select the
            # third ROM in the pinned managed picker's 24px rows.
            command('xdotool', 'mousemove', '--window', picker, '250', '180', 'click', '1', env=env)
            time.sleep(0.4)
            if options.screenshots:
                command('import', '-window', picker, str(Path(options.screenshots) / 'desktop-file-selected.png'), env=env)
            command('xdotool', 'mousemove', '--window', picker, str(width - 132), str(height - 27), 'click', '1', env=env)
            dialog = None
            for _ in range(50):
                found = subprocess.run(['xdotool', 'search', '--onlyvisible', '--name', '^Add games$'], env=env, capture_output=True, text=True)
                if found.returncode == 0:
                    dialog = found.stdout.splitlines()[0]
                    break
                time.sleep(0.1)
            assert dialog is not None, 'ROM selection did not open the import settings dialog'
            time.sleep(0.4)
            geometry = dict(line.split('=', 1) for line in command('xdotool', 'getwindowgeometry', '--shell', dialog, env=env).splitlines())
            command('xdotool', 'mousemove', '--window', dialog, str(int(geometry['WIDTH']) - 90), str(int(geometry['HEIGHT']) - 42), 'click', '1', env=env)
            command('xdotool', 'windowfocus', '--sync', window, env=env)
            wait_for('Third test game')
            listing = command(str(binary), 'game-list', str(library), env=env)
            assert len(listing.strip().splitlines()) == 3, 'The graphical import did not add a game to the library'
            screenshot('desktop-imported')
            print('PASS: graphical ROM selection, import settings, backend import and library refresh', flush=True)
        finally:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
