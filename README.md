# Hakchi2 CE — Linux command-line port

Manage a NES/SNES Classic from a Linux terminal using the existing Hakchi USB/FEL
code, game metadata format and emulator command mappings. The Windows application
remains in this repository; the Linux entry point is `hakchi_cli/`.

**Start with the [Linux setup and usage guide](hakchi_cli/README.md).** It covers
building, connecting, games, modules, backups, recovery and troubleshooting.
The [original Windows documentation](hakchi_gui/README.md) describes the GUI.

## Why a Linux port is possible

The Windows application targets .NET Framework 4.8 and uses Windows Forms. Its UI
and Windows USB driver installation are tied to Windows, but much of the work below
the UI is reusable C#: FEL transport, clovershell communication, `.desktop` metadata
and emulator command translation.

The Linux CLI compiles those shared sources into a .NET 10 console application.
It accesses USB through the system's `libusb-1.0` and delegates network connections
to OpenSSH. This keeps the port small and provides terminal workflows without
rebuilding the GUI. The initial CLI adapter is about 600 lines of C#; the existing
transport libraries do most of the work.

## What you can do

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
12 CLI regressions, USB packet parser checks and extracted-package smoke checks have
passed. Transfers, menu behavior, recovery boots and flashing have not been validated
on a real NES/SNES Classic in this work. ARM64 is a build target, not a tested release.

The CLI does not automate first-time firmware installation, storage expansion or
factory reset. It also omits Canoe SFROM conversion, automatic game patches, artwork
search, database enrichment and custom folder generation. The Windows feature list
does not describe the Linux CLI's capabilities.

## Quick start from source

Install Git, `libusb-1.0`, OpenSSH and the .NET 10 SDK first. See the
[requirements](hakchi_cli/README.md#requirements). These commands explicitly select
the branch containing the Linux port:

```sh
git clone --branch t3code/assess-linux-port https://github.com/dazeb/Hakchi2-CE.git
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

`hakchi_cli/` contains command dispatch, shell adapters, game packaging, FEL argument
checks, the build script and tests. It links sources from `Libraries/FelLib/`,
`hakchi_gui/Clovershell/`, `DesktopFile.cs` and `CoreCommands.cs`. Vendored submodule
source is unchanged; CLI-specific shared-shell behavior uses conditional compilation.

See [implementation details](hakchi_cli/README.md#how-the-cli-works) and
[verification](hakchi_cli/README.md#verification-and-contributing) before changing it.
Generated packages are local build outputs; pushing this source branch does not
publish downloadable release assets.

## Credits and license

Hakchi2 was created by ClusterM, building on hakchi by madmonkey. Hakchi2 CE was
developed by Team Shinkansen and its contributors. This port reuses their work and
retains the repository's [GPL license](LICENSE) and existing library attributions.
