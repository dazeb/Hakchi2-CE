#!/usr/bin/env bash
# Native Linux packaging. Build on an older supported distro for wider compatibility.
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
case "$(uname -m)" in
  x86_64)
    runtime=linux-x64; arch=x86_64; ld_arch=x86-64
    tool_sha=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
    runtime_sha=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;;
  aarch64)
    runtime=linux-arm64; arch=aarch64; ld_arch=AArch64
    tool_sha=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158
    runtime_sha=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;;
  *) printf 'AppImage packaging supports native x86_64/aarch64 Linux builds.\n' >&2; exit 2 ;;
esac
for dependency in curl sha256sum python3 desktop-file-validate ldd file; do
  command -v "$dependency" >/dev/null || { printf 'Missing build tool: %s\n' "$dependency" >&2; exit 1; }
done
command -v "${DOTNET:-dotnet}" >/dev/null || { printf 'Set DOTNET to a .NET 10 SDK executable.\n' >&2; exit 1; }
ldconfig=$(command -v ldconfig || printf /sbin/ldconfig)
mkdir -p bin/appimage
output="$PWD/bin/appimage"
work=$(mktemp -d "$output/build.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
appdir="$work/Hakchi.AppDir"
mkdir -p "$appdir/usr/lib/hakchi" "$appdir/usr/share/doc/hakchi/licenses"
licenses="$appdir/usr/share/doc/hakchi/licenses"

"${DOTNET:-dotnet}" publish hakchi_cli.csproj -c Release -r "$runtime" \
  --self-contained true -p:PublishSingleFile=false -p:DebugType=None \
  -o "$appdir/usr/lib/hakchi"

"${DOTNET:-dotnet}" publish ../hakchi_frontend/hakchi_frontend.csproj -c Release -r "$runtime" \
  --self-contained true -p:PublishSingleFile=false -p:DebugType=None \
  -o "$appdir/usr/lib/hakchi-desktop"
cp ../hakchi_frontend/README.md "$appdir/usr/lib/hakchi-desktop/"

# Bundle USB libraries, but leave glibc and the standard .NET native OS dependencies
# to the host. Select native libraries explicitly on multiarch build machines.
for library in libusb-1.0.so.0 libudev.so.1; do
  path=$("$ldconfig" -p | awk -v lib="$library" -v arch="$ld_arch" \
    '$1 == lib && index($0, arch) && !found { print $NF; found=1 }')
  if [[ -z "$path" ]]; then
    printf 'Missing native %s. Install libusb-1.0-0 and libudev1.\n' "$library" >&2
    exit 1
  fi
  cp -L -- "$path" "$appdir/usr/lib/$library"
done
# Fail rather than silently shipping a USB build with unbundled extra dependencies.
if LD_LIBRARY_PATH="$appdir/usr/lib" ldd "$appdir/usr/lib/libusb-1.0.so.0" \
  "$appdir/usr/lib/libudev.so.1" | awk '/=>/ && $1 !~ /^(libudev.so.1|libc.so.6|libpthread.so.0|librt.so.1|libdl.so.2)$/ { bad=1; print > "/dev/stderr" } END { exit !bad }'; then
  printf 'USB libraries have extra dependencies; use the documented Debian/Ubuntu builder.\n' >&2
  exit 1
fi
cp /usr/share/doc/libusb-1.0-0/copyright "$licenses/libusb-copyright"
cp /usr/share/doc/libudev1/copyright "$licenses/libudev-copyright"
cp /usr/share/common-licenses/LGPL-2.1 "$licenses/LGPL-2.1"
cp appimage/LibUsbDotNet-LICENSE appimage/AppImage-runtime-LICENSE "$licenses/"
python3 - "$appdir" "$runtime" <<'PY'
import json, shutil, sys
from pathlib import Path
appdir, runtime = Path(sys.argv[1]), sys.argv[2]
options = json.loads((appdir / 'usr/lib/hakchi/hakchi.runtimeconfig.json').read_text())['runtimeOptions']
version = next(f['version'] for f in options['includedFrameworks'] if f['name'] == 'Microsoft.NETCore.App')
folders = json.loads(Path('obj/project.assets.json').read_text())['packageFolders']
package = next(Path(p) / f'microsoft.netcore.app.runtime.{runtime}/{version}' for p in folders
               if (Path(p) / f'microsoft.netcore.app.runtime.{runtime}/{version}/LICENSE.TXT').is_file())
for name in ('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'):
    shutil.copyfile(package / name, appdir / 'usr/share/doc/hakchi/licenses' / ('dotnet-' + name))
PY
python3 ../hakchi_frontend/package_licenses.py "$appdir/usr/share/doc/hakchi/licenses"
cp appimage/AppRun appimage/hakchi.desktop appimage/hakchi.png "$appdir/"
chmod 755 "$appdir/AppRun"
ln -s hakchi.png "$appdir/.DirIcon"
desktop-file-validate "$appdir/hakchi.desktop"
printf 'Source: https://github.com/dazeb/Hakchi2-CE\nRevision: %s\nRuntime: %s\nappimagetool: 1.9.1\nAppImage runtime: 20251108\n' \
  "${HAKCHI_BUILD_REVISION:-$(git describe --always --dirty 2>/dev/null || printf unknown)}" \
  "$runtime" > "$appdir/usr/share/doc/hakchi/BUILD.txt"

# Fixed upstream releases and SHA-256 checks; never execute an unchecked download.
download() {
  local url=$1 destination=$2 checksum=$3
  curl --fail --location --retry 3 --silent --show-error "$url" -o "$destination"
  printf '%s  %s\n' "$checksum" "$destination" | sha256sum --check --status
}
download "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-$arch.AppImage" \
  "$work/appimagetool.AppImage" "$tool_sha"
download "https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-$arch" \
  "$work/runtime" "$runtime_sha"
chmod 755 "$work/appimagetool.AppImage"
# Extract the packaging tool so building does not require FUSE or elevated privileges.
(cd "$work" && ./appimagetool.AppImage --appimage-extract >/dev/null)
image="$output/hakchi-$runtime.AppImage"
ARCH="$arch" "$work/squashfs-root/AppRun" --runtime-file "$work/runtime" \
  --no-appstream "$appdir" "$work/hakchi.AppImage"
chmod 755 "$work/hakchi.AppImage"
mv -- "$work/hakchi.AppImage" "$image"
(cd "$output" && sha256sum "${image##*/}" > "${image##*/}.sha256")
printf '\nCreated: %s\nOpen desktop: %s\nCLI help: %s --help\n' "$image" "$image" "$image"
