# Hakchi2 CE — Linux desktop and CLI

Manage a NES/SNES Classic from a native Linux desktop or terminal using the existing
Hakchi USB/FEL code, game metadata format and emulator command mappings. The Windows
application remains in this repository; the Linux frontend is `hakchi_frontend/`
and its backend is `hakchi_cli/`.

**Start with the [Linux desktop guide](hakchi_frontend/README.md).** For terminal
workflows, the [CLI setup and usage guide](hakchi_cli/README.md) covers
building, connecting, games, modules, backups, recovery and troubleshooting.
The [original Windows documentation](hakchi_gui/README.md) describes the GUI.

## Download and run on Linux

The [experimental Linux desktop v0.2.0 release](https://github.com/dazeb/Hakchi2-CE/releases/tag/linux-desktop-v0.2.0)
provides a single-file **x86-64 AppImage**, its SHA-256 checksum and a source archive
including the FEL submodule. The AppImage includes .NET, USB libraries, CLI payloads,
Avalonia frontend, the setup guides and license notices. Opening it without arguments
launches the compact dark GUI; explicit command arguments run the CLI.

Download and verify it before running:

```sh
curl -fLO https://github.com/dazeb/Hakchi2-CE/releases/download/linux-desktop-v0.2.0/hakchi-linux-x64.AppImage
curl -fLO https://github.com/dazeb/Hakchi2-CE/releases/download/linux-desktop-v0.2.0/hakchi-linux-x64.AppImage.sha256
sha256sum -c hakchi-linux-x64.AppImage.sha256
chmod +x hakchi-linux-x64.AppImage
./hakchi-linux-x64.AppImage
```

The release is built on Debian 12. Use a supported glibc-based Linux distribution
with the standard .NET native dependencies (including ICU and OpenSSL); SSH commands
also need OpenSSH. If FUSE is unavailable, run
`./hakchi-linux-x64.AppImage --appimage-extract-and-run`. The GUI needs X11 or
XWayland and standard desktop libraries; see the [desktop prerequisites](hakchi_frontend/README.md).
For USB permissions, connection setup and build instructions, see the
[AppImage guide](hakchi_cli/README.md#appimage).

## Recent changes

- Added a native compact dark frontend with game import, search and artwork preview,
  connection settings, status checks, library sync, module installation and backup saving.
- The AppImage now opens a GUI by default, with desktop integration and settings
  stored outside the bundle. Existing terminal commands remain available.

- Added a native .NET 10 Linux CLI for game preparation and sync, modules, file
  transfers, stored backups, SSH/USB shell access and low-level FEL recovery.
- Added clovershell packet buffering and CLI cancellation, timeout and exit handling.
- Added AppImage packaging with bundled native USB libraries, udev rule output,
  license notices and checksum-verified packaging tools.
- Added CLI regressions, USB protocol checks and AppImage relocation/native-library
  checks, plus a Linux packaging workflow in GitHub Actions.

The Linux frontend, CLI and packaging are on this fork's `mainline` branch. The
Windows GUI and its original documentation remain available separately.

## Why a Linux port is possible

The Windows application targets .NET Framework 4.8 and uses Windows Forms. Its UI
and Windows USB driver installation are tied to Windows, but much of the work below
the UI is reusable C#: FEL transport, clovershell communication, `.desktop` metadata
and emulator command translation.

The Linux CLI compiles those shared sources into a .NET 10 console application.
It accesses USB through the system's `libusb-1.0` and delegates network connections
to OpenSSH. This keeps the port small and provides terminal workflows without
porting the Windows Forms UI. The native Avalonia frontend invokes the same backend.
The initial CLI adapter is about 600 lines of C#; the existing
transport libraries do most of the work.

## What you can do

The desktop offers local library preparation, status/USB discovery, synchronization,
module installation and stored-backup retrieval. File transfers and low-level FEL
operations remain available through the CLI below.

| Task | Commands | Requirements |
| --- | --- | --- |
| Prepare and inspect games | `game-add`, `game-list` | Local ROM files; no console needed |
| Check firmware and storage | `status` | Running hakchi firmware with a working shell |
| Upload games to menu `000` | `sync` | Writable game storage and installed emulator commands |
| Manage console modules | `mod-install`, `mod-uninstall` | Compatible `.hmod` modules and a working shell |
| Run commands and transfer files | `exec`, `upload`, `download` | USB clovershell or SSH |
| Retrieve a stored stock-kernel backup | `backup` | Firmware already holding that backup |
| Boot recovery into RAM | `boot`, `fel memboot` | USB FEL access and compatible payloads |
| Read/write flash regions | `fel read-nand`, `fel flash-*` | Compatible payloads and knowledge of the flash layout |

**This is a minimal port with limited hardware validation.** The Linux x64 build,
CLI regressions, frontend adapter and window checks, USB packet parser checks and
extracted-package checks have
passed. Transfers, menu behavior, recovery boots and flashing have not been validated
on a real NES/SNES Classic in this work. ARM64 is a build target, not a tested release.

The CLI does not automate first-time firmware installation, storage expansion or
factory reset. It also omits Canoe SFROM conversion, automatic game patches, artwork
search, database enrichment and custom folder generation. The Windows feature list
does not describe the Linux CLI's capabilities.

## Quick start from source

Install Git, `libusb-1.0`, OpenSSH and the .NET 10 SDK first. See the
[requirements](hakchi_cli/README.md#requirements). Clone this fork's `mainline` branch:

```sh
git clone --branch mainline https://github.com/dazeb/Hakchi2-CE.git
cd Hakchi2-CE
git submodule update --init Libraries/FelLib
bash hakchi_cli/publish.sh
export PATH="$PWD/hakchi_cli/bin/publish/linux-x64:$PATH"
hakchi --help

# No console is required for these two commands.
hakchi game-add ./game.nes ./games --core fceumm --name 'My Game'
hakchi game-list ./games
```

For a console already running suitable hakchi firmware, establish its shell connection,
retrieve any existing backup, check the installed emulator and select its actual game
storage path before syncing. Follow the
[first-use workflow](hakchi_cli/README.md#first-use-workflow) for those steps.

A stock console requires a separate, model-appropriate first-time installation
procedure. `backup` cannot obtain a stock backup from an unmodified console, and a
recovery RAM boot does not install firmware.

## How it works and how to contribute

`hakchi_frontend/` contains the native window, backend adapter and saved settings.
`hakchi_cli/` contains command dispatch, shell adapters, game packaging, FEL argument
checks, the build script and tests. It links sources from `Libraries/FelLib/`,
`hakchi_gui/Clovershell/`, `DesktopFile.cs` and `CoreCommands.cs`. Vendored submodule
source is unchanged; CLI-specific shared-shell behavior uses conditional compilation.

See [implementation details](hakchi_cli/README.md#how-the-cli-works) and
[verification](hakchi_cli/README.md#verification-and-contributing) before changing it.
Local build outputs are ignored by Git. Downloadable packages are published on this
fork's [Releases page](https://github.com/dazeb/Hakchi2-CE/releases); the Linux
packaging workflow also provides AppImage/checksum artifacts for its checked builds.

## Credits and license

Hakchi2 was created by ClusterM, building on hakchi by madmonkey. Hakchi2 CE was
developed by Team Shinkansen and its contributors. This port reuses their work and
retains the repository's [GPL license](LICENSE) and existing library attributions.
