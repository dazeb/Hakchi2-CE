#!/usr/bin/env python3
"""Copy restored frontend dependency notices and provenance into an AppDir."""
import json
from pathlib import Path
import shutil
import sys
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent
assets = json.loads((root / 'obj/project.assets.json').read_text())
destination = Path(sys.argv[1]) / 'frontend'
destination.mkdir(parents=True, exist_ok=True)
shutil.copyfile(root / 'licenses/Avalonia-MIT.txt', destination / 'Avalonia-MIT.txt')
manifest = ['Frontend NuGet dependency metadata and license notices',
            'Includes restored platform dependencies; only the selected Linux RID is published.', '']
for identity, entry in sorted(assets['libraries'].items()):
    if entry['type'] != 'package' or identity.startswith('Microsoft.NETCore.App.'):
        continue
    package = next(Path(folder) / entry['path'] for folder in assets['packageFolders']
                   if (Path(folder) / entry['path']).is_dir())
    notices = destination / identity.replace('/', '-')
    notices.mkdir()
    spec = next(package.glob('*.nuspec'))
    shutil.copyfile(spec, notices / spec.name)
    metadata = ET.parse(spec).getroot()
    def value(name):
        element = metadata.find('.//{*}' + name)
        return '' if element is None else (element.text or '')
    manifest.extend([identity, 'Authors: ' + value('authors'),
                     'Copyright: ' + value('copyright'), 'License: ' + value('license'),
                     'Project: ' + value('projectUrl'), ''])
    for relative in entry.get('files', []):
        path = Path(relative)
        if any(word in path.name.lower() for word in ('license', 'licence', 'notice', 'copying')):
            source = package / path
            if source.is_file():
                target = notices / path
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
(destination / 'DEPENDENCIES.txt').write_text('\n'.join(manifest))
