# Hakchi Linux CLI: setup, usage and recovery

This CLI brings the reusable parts of Hakchi2 CE to a Linux terminal: prepare games,
sync them to an already modified console, manage modules, transfer files, retrieve
existing backups and use low-level FEL recovery commands.

The Windows GUI targets .NET Framework 4.8 and Windows Forms. The Linux CLI targets
.NET 10 and compiles the existing FEL/clovershell sources, game metadata serializer
and emulator command mappings directly. USB uses `libusb-1.0`; SSH uses the system's
OpenSSH client. A published build includes the .NET runtime. Wine, a desktop session
and Windows driver installers are not required to run it.

**Current status:** Linux x64 builds and software checks pass. Real console transfers,
menu behavior, recovery boots and flashing still need hardware validation. This is
not full feature parity with the Windows GUI. First-time firmware installation,
storage expansion and factory reset are not automated.

## Contents

- [Requirements](#requirements)
- [Build or use a package](#build-or-use-a-package)
- [Choose a connection](#choose-a-connection)
- [First-use workflow](#first-use-workflow)
- [Command reference](#command-reference)
- [Games and sync behavior](#games-and-sync-behavior)
- [Modules, files and backups](#modules-files-and-backups)
- [FEL and recovery](#fel-and-recovery)
- [Troubleshooting](#troubleshooting)
- [How the CLI works](#how-the-cli-works)
- [Verification and contributing](#verification-and-contributing)

## Requirements

| Requirement | When needed |
| --- | --- |
| Linux with the appropriate .NET native dependencies | Running or building the CLI |
| System `libusb-1.0` (`libusb-1.0.so.0`) | Direct USB enumeration, clovershell and FEL |
| `ssh` from OpenSSH | Commands using `--host` |
| Git, .NET 10 SDK and access to NuGet | Building from source |
| `tar` and standard shell/file utilities on the console | Game and module transfers |
| Python 3 | Running the CLI regression suite |
| Data-capable USB cable and access to the USB device | Direct USB workflows |
| Compatible hakchi firmware and emulator/core modules | Routine console management and game launch |
| Compatible U-Boot/boot images or recovery `.hmod` | FEL operations |

For Debian/Ubuntu, install the basic host tools:

```sh
sudo apt update
sudo apt install git libusb-1.0-0 openssh-client
```

Install the **SDK**, rather than only the runtime, from Microsoft's
[.NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
Follow its [Linux installation instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux)
for your distribution and native dependencies. Check that an SDK starting with `10.`
is listed:

```sh
dotnet --list-sdks
uname -m
```

`linux-x64` is the default target for x86-64 hosts. `linux-arm64` can be built for
ARM64 hosts, but has not been validated here. The provided targets are for glibc-based
Linux; musl/Alpine is not covered by the tested package. Bundling .NET does not bundle
every native operating-system dependency.

## Build or use a package

### Build from source

The Linux port is on `t3code/assess-linux-port`; cloning only the upstream/default
branch will not select it.

```sh
git clone --branch t3code/assess-linux-port https://github.com/dazeb/Hakchi2-CE.git
cd Hakchi2-CE
git submodule update --init Libraries/FelLib
bash hakchi_cli/publish.sh
export PATH="$PWD/hakchi_cli/bin/publish/linux-x64:$PATH"
hakchi --help
```

Only `Libraries/FelLib` needs initializing for the CLI. Windows GUI submodules are
not required for this build. The build restores the LibUsbDotNet NuGet package and
publishes a self-contained executable to `hakchi_cli/bin/publish/linux-x64/`.

For another target or an SDK installed outside `PATH`:

```sh
bash hakchi_cli/publish.sh linux-arm64
DOTNET=/path/to/dotnet bash hakchi_cli/publish.sh
```

The PATH change above lasts for the current shell. Use the executable's full path
if you prefer. To keep a build elsewhere, copy the **whole publish directory**.
Installing just the `hakchi` executable loses its loader and default icon.

### Use an existing package

If you already have `hakchi-linux-x64.tar.gz`:

```sh
tar -xzf hakchi-linux-x64.tar.gz
cd linux-x64
export PATH="$PWD:$PATH"
hakchi --help
```

The SDK is not needed to run this self-contained package. It still needs the native
host dependencies described above. A Git clone does not contain this generated
archive: build outputs are ignored, and pushing the source branch does not upload
release assets.

Keep these files together:

```text
linux-x64/
  hakchi
  payloads/
    fes1.bin
    blank_app.png
  70-hakchi.rules
  README.md
  LICENSE
```

To generate the same archive from a source build, run from the repository root:

```sh
tar --exclude='*.pdb' -C hakchi_cli/bin/publish \
  -czf hakchi_cli/bin/hakchi-linux-x64.tar.gz linux-x64
```

## Choose a connection

The CLI does not discover network addresses or automatically switch transports.

| Connection | Invocation | Console state |
| --- | --- | --- |
| USB clovershell | `hakchi status` | Booted firmware with legacy clovershell available |
| SSH over network or USB Ethernet | `hakchi --host HOST status` | Booted firmware with SSH and a reachable IP/hostname |
| USB FEL | `hakchi boot ...` or `hakchi fel ...` | FEL/recovery mode; compatible payloads supplied |

FEL is a low-level USB boot/recovery mode, not a running shell. A console appearing
in `devices` does not prove it can answer `status`. Conversely, an SSH-connected
console need not appear with the FEL/clovershell USB ID.

### USB permissions

From the repository root, install the rule, reload udev and reconnect the cable:

```sh
sudo install -m 644 hakchi_cli/70-hakchi.rules /etc/udev/rules.d/70-hakchi.rules
sudo udevadm control --reload-rules
hakchi devices
```

From an extracted package, the rule is `./70-hakchi.rules`. It grants access to USB
ID `1f3a:efe8` through `uaccess`, which is intended for an active local user session.
On a headless machine, arrange a suitable USB device group/ACL instead; the bundled
rule may not grant access to an SSH login. Routine CLI commands do not require
running the host process as root. The rule does not configure USB Ethernet.

### SSH

Use the console's actual address; `hakchi.local` is only an example and depends on
hostname discovery. Verify the normal OpenSSH connection first:

```sh
ssh root@hakchi.local 'uname -a'
hakchi --host hakchi.local status
hakchi --host hakchi.local --port 22 --timeout 60 status
```

The CLI connects as `root` on the **console**, using your host's SSH keys,
configuration, known-host checks and authentication prompts. It does not install
keys, supply a default password or bypass host-key checks. Root access inside the
console does not imply running the Linux CLI itself with `sudo`.

Global options must come **before** the command:

```text
hakchi [--host HOST] [--port PORT] [--timeout SECONDS] COMMAND ...
```

The default timeout is 30 seconds; valid values are 1–86400. It bounds USB/FEL
discovery and each remote shell command, not the entire multi-step workflow. FEL
transfers keep the underlying library's USB timeouts. Ctrl-C cancels discovery,
remote commands and FEL progress callbacks; an active native USB call may take time
to return. Cancellation/disconnection can leave remote work partially completed.

## First-use workflow

### If the console already runs hakchi firmware

1. Build or extract the CLI and run `hakchi --help`.
2. Establish USB clovershell or SSH, then run `status`.
3. Retrieve the existing stock-kernel backup if the firmware holds one. Keep it
   outside the game library, and keep another copy elsewhere.
4. Install the appropriate emulator/core modules if they are not already present.
5. Prepare a local library with `game-add`, then inspect it with `game-list`.
6. Confirm the console's actual writable game storage directory and free space.
7. Run `sync` against that directory, then check the console menu and launch a game.

For example, after confirming the emulator and storage path:

```sh
hakchi --host hakchi.local status
hakchi --host hakchi.local backup ./stock-kernel.img
hakchi game-add ./game.nes ./games --core fceumm --name 'My Game'
hakchi game-list ./games
# Example path only: substitute your verified console game directory.
hakchi --host hakchi.local --timeout 300 sync ./games /var/lib/hakchi/games/nes-usa
```

Do not treat the example storage path as a universal default. Console model/region,
external storage and separate game storage settings affect the correct destination.

### If the console is stock or needs first-time installation

The CLI does not provide an automated first-time installer. `backup` calls
`hakchi getBackup2` on an already running, modified firmware; it cannot obtain an
original backup from a stock console by itself.

Use a separate, model-appropriate installation/recovery procedure that creates and
retains the original backup. The existing Windows workflow is described in
`hakchi_gui/README.md` in the source tree. This CLI exposes raw FEL operations for
experienced users, but it does not determine a safe flash layout or assemble an
installation plan. A RAM recovery boot does not install firmware.

## Command reference

Paths described as remote refer to the console; other file/library paths refer to
the Linux host. Quote paths and names containing spaces.

| Command | Behavior |
| --- | --- |
| `--help`, `help` | Show top-level usage without connecting |
| `devices` | List USB vendor/product IDs; label `1f3a:efe8`; exit 3 if no matching console |
| `status` | Print kernel, version, firmware mode, game storage discovery and disk usage |
| `exec 'COMMAND'` | Run one shell command, pipe redirected stdin, stream stdout/stderr and return its exit code |
| `upload FILE /REMOTE/FILE` | Write a local file to the console; overwrite its destination directly |
| `download /REMOTE/FILE FILE` | Download a nonempty file; atomically replace local destination after success |
| `backup FILE` | Download the existing stock-kernel backup through `hakchi getBackup2` |
| `mod-install MOD.hmod` | Install a gzip-tar module archive or an unpacked `.hmod` directory |
| `mod-uninstall NAME` | Ask the firmware to uninstall a module by its installed name |
| `game-add ROM LIBRARY --core CORE [--name NAME] [--icon PNG]` | Prepare a game locally |
| `game-list LIBRARY` | Validate/list a prepared local library |
| `sync LIBRARY /REMOTE/GAMES` | Stage and merge those games into menu `000` |
| `boot HAKCHI.hmod` | Load a compatible recovery image into RAM over direct USB FEL |
| `fel --help` | Show low-level FEL commands and their required arguments |

Remote paths accepted by `upload`, `download` and `sync` must be absolute, below `/`,
and contain no `.`/`..` path components or control characters. `upload` does not create
parent directories and is not atomic; a failed upload can leave a partial remote file.

| Exit code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Operation failure |
| `2` | Invalid arguments |
| `3` | No matching console found by `devices` |
| `130` | Cancellation |
| Other | `exec` and `status` can return the remote/OpenSSH exit code |

Checksums and diagnostic messages go to stderr; `exec` streams the console's stdout
and stderr. A download's printed SHA256 identifies the received bytes; it does not
establish that an image is compatible with your console.

## Games and sync behavior

### Prepare games locally

```sh
hakchi game-add ./game.nes ./games --core fceumm --name 'My NES Game'
hakchi game-add ./game.sfc ./games --core snes9x --name 'My SNES Game' --icon ./cover.png
hakchi game-list ./games
```

Choose a command installed on the console, typically through a compatible RetroArch
and core hmod. The CLI accepts an emulator name; it does not detect or install the
core automatically. Shared aliases translate names such as `snes9x2010` to `snes10`.

The importer copies the ROM bytes unchanged. Raw SNES ROMs therefore need a suitable
emulator/core; this port does not convert them into Canoe SFROM files. It does not
extract ROM archives, patch games, fetch metadata or search for artwork.

Each import creates a flat directory of this form:

```text
games/
  CLV-Z-ABCDE/                 # illustration: actual ID comes from the ROM hash
    CLV-Z-ABCDE.desktop
    CLV-Z-ABCDE.png
    CLV-Z-ABCDE_small.png
    game.nes
```

The hash-derived game code is deterministic. An existing destination is refused
rather than overwritten; the short code is not a unique global identifier. Edit its
`.desktop` file to change display name, arguments, players, save slots or other
metadata. Defaults are one player and four save slots. Generated commands use
`/var/games/CODE/game.ext` and save paths use `/var/saves/CODE`.

Artwork must have a PNG signature. The same supplied/default image is copied into
the normal and small icon files, without resizing or full image validation. Supply
appropriately sized artwork for the console.

### Upload a library

```sh
hakchi --host hakchi.local status
# Substitute the verified destination below.
hakchi --host hakchi.local --timeout 300 sync ./games /var/lib/hakchi/games/snes-usa
```

`status` runs `hakchi findGameSyncStorage`, but a storage root is not necessarily the
final model/region-specific game directory. Inspect the existing console configuration
and directories with `exec` before choosing the destination.

`sync` validates flat `CLV-*` directories and their corresponding `.desktop` files.
Symlinked game directories or contents are rejected. Existing flat exports from
Hakchi can be used if their game codes and metadata pass validation; an arbitrary
nested Windows library is not automatically converted.

The CLI creates a local tar archive, extracts it into a unique staging directory
inside the selected remote game directory, then stops the console UI. It asks hakchi
to unmount the game overlay, merges each supplied game into `000`, syncs storage and
attempts to restore the overlay and UI.

Existing games with the same code are replaced. Unrelated games, other menu pages
and saves are not intentionally pruned. This is not a complete mirror or a folder
builder. Staging needs room for incoming games alongside the existing copies.

Replacements have a per-game rollback: the old target is moved to
`STAGING/previous-CODE` before the new game is moved into place. A failed replacement
attempts to put that old target back. If commit/restore fails, the staging path is
reported and retained for recovery. Earlier games may already have been updated;
the whole library is not one atomic transaction. Power loss or disconnection can
prevent rollback or UI restoration.

After a failure, reconnect, inspect the reported staging directory and target menu,
and preserve any `previous-*` copies before retrying or cleaning up. If connectivity
is restored and the UI needs restarting:

```sh
hakchi --host hakchi.local exec 'hakchi overmount_games && uistart'
```

The CLI has no automatic interrupted-transfer resume or staging recovery command.

## Modules, files and backups

### Modules

```sh
hakchi --host hakchi.local mod-install ./snes9x.hmod
hakchi --host hakchi.local mod-install ./custom-core.hmod
hakchi --host hakchi.local mod-uninstall snes9x
```

The input path must end in `.hmod`. Files must be gzip-compressed tar archives;
directories must contain the module's unpacked contents. Modules are extracted under
a unique `/tmp/hakchi-cli-*` directory, passed to `hakchi packs_install`, then cleanup
is attempted. Uninstall uses `hakchi pack_uninstall` with the installed module name.
The CLI does not browse a module repository, resolve dependencies, verify signatures
or download emulator modules. Use modules appropriate to your firmware/model; their
installation scripts execute on the console.

### Shell and file transfers

```sh
hakchi --host hakchi.local exec 'ls /var/lib/hakchi'
printf 'hello\n' | hakchi --host hakchi.local exec 'cat > /tmp/example'
hakchi --host hakchi.local upload ./config.txt /tmp/config.txt
hakchi --host hakchi.local download /tmp/config.txt ./downloaded.txt
```

Quote the entire remote command so the console shell interprets its arguments and
operators. `exec` is a noninteractive command transport, not an interactive SSH
terminal. Use ordinary `ssh root@HOST` for an interactive session.

### Existing stock-kernel backup

```sh
hakchi --host hakchi.local backup ./stock-kernel.img
sha256sum ./stock-kernel.img
```

`backup` and `download` write a temporary `.partial-*` file beside the destination,
reject an empty result, flush the completed file and replace the destination only
after the remote command succeeds. On failure, an existing local destination is kept
and temporary-file cleanup is attempted. On success, an existing destination is
overwritten: use distinct backup filenames when keeping multiple copies.

This backup is the firmware's stored stock-kernel image, not a full NAND, game or
save-state backup. There is no dedicated save export/restore workflow in this port.

## FEL and recovery

FEL commands always use direct USB, even when `--host` is supplied. The CLI does not
download firmware or determine payload compatibility. Keep a verified original
backup before storage writes. The required `--yes` flag acknowledges a write; it
does not establish that the image or address is safe.

Enter FEL using the reset/power sequence appropriate to your console, connect a
data-capable cable, arrange USB permissions and check `hakchi devices`. The discovery
step looks for USB ID `1f3a:efe8`; a clovershell device can share that ID, so listing
it alone does not prove it is in FEL mode.

### RAM recovery boot

```sh
hakchi --timeout 15 boot ./hakchi.hmod
# Still boots over direct USB; --host only requests SSH in this recovery image.
hakchi --host hakchi.local --timeout 15 boot ./hakchi.hmod
```

The gzip-tar hmod must contain `boot/boot.img` and `boot/uboot.bin`. The CLI reads
only those payloads and adds `hakchi-clovershell` by default, or `hakchi-shell` when
`--host` is present, to the boot image's command line. It loads the image into RAM
through FEL. It neither installs firmware nor waits for the requested shell to come
online; check `status` separately after booting. The requested shell must be supported
by the supplied recovery image.

### Low-level commands

```sh
hakchi fel --help
hakchi fel memboot -u ./uboot.bin -b ./boot.img
# Kernel region example from this transport's layout; not a complete NAND backup.
hakchi fel read-nand -u ./uboot.bin -a 0x600000 -l 0x400000 -o ./kernel-region.bin
```

| FEL command | Required arguments / behavior |
| --- | --- |
| `memboot` | `-u UBOOT -b BOOT`; boot an Android image into RAM |
| `read-nand` | `-u UBOOT -a ADDRESS -l LENGTH -o FILE`; read to a new local file |
| `flash-boot` | `-u UBOOT -b BOOT --yes`; write/verify the boot region, then request shutdown |
| `flash-uboot` | `-u UBOOT --yes`; write/verify the primary U-Boot region, then request shutdown |
| `flash-nand` | `-u UBOOT -a ADDRESS -i FILE --yes`; write a chosen region; optional `--verify` |
| `run-command` | `-u UBOOT -c 'COMMAND'`; run a U-Boot command; optional `--noreturn` |
| `update-enter-fel` | Burn-mode handshake to enter FEL; no U-Boot/FES input required |

All except `update-enter-fel` accept `-f FES1.bin`; otherwise the bundled FES1 loader
is used. Addresses and lengths accept decimal or `0xHEX`. For this transport,
`flash-boot` targets `0x600000` with a 4 MiB image limit; `flash-uboot` targets
`0x100000` with a 2 MiB limit. Alternate U-Boot copies are not managed.

Read addresses and lengths must align to 128 KiB sectors. Reads refuse an existing
output file and load the requested region into host memory before writing it to disk.
`read-nand` does not use the atomic-download path; a local disk failure can leave a
partial new file. Choose manageable regions and inspect/checksum each result.

Raw writes require a nonempty input and aligned start address. Inputs are padded
with zeros to a full 128 KiB sector, so the padded tail is also written. Raw-write
verification is optional; boot and U-Boot writes verify automatically at their actual
destination address. Android boot headers, page size and image size are checked
before device discovery. These structural checks cannot confirm model compatibility.

For deliberate boot-image recovery writes, the syntax is:

```sh
hakchi fel flash-boot -u ./uboot.bin -b ./boot.img --yes
```

Do not infer a general firmware-installation procedure from this example.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| `dotnet` missing, or SDK cannot target .NET 10 | Install the .NET 10 SDK; check `dotnet --list-sdks`; use `DOTNET=/path/to/dotnet` if necessary |
| Missing FEL source/build errors | Run `git submodule update --init Libraries/FelLib` from the repository root |
| Cannot load `libusb-1.0.so.0` | Install the matching system libusb package for your CPU/distribution |
| Executable format error | Check `uname -m`; x64 and ARM64 binaries are different targets |
| Native .NET library/dependency error | Follow Microsoft's Linux dependency instructions; the runtime is bundled but system libraries still matter |
| `devices` exits 3 | No matching FEL/clovershell USB device was found; check cable, power, mode and USB visibility |
| USB permission denied | Install/reload the udev rule and reconnect; on headless hosts check the device group/ACL |
| No USB clovershell found | Boot firmware with clovershell, or use `--host` for SSH/USB Ethernet; FEL mode cannot answer shell commands |
| `hakchi.local` does not resolve | Use the console's known IP or configure hostname discovery; the CLI does not discover it |
| SSH authentication/host-key failure | Test ordinary `ssh root@HOST`; correct credentials/configuration and verify host identity |
| `status` stops after partial output | It chains commands with `&&`; run the failing firmware command separately with `exec` |
| Timeout | Check transport first; increase `--timeout` before the command for a legitimate long transfer |
| Empty backup/download | Check that the remote file or stored stock backup exists; the previous local destination is kept |
| Imported game will not launch | Inspect its `.desktop` command, installed core, ROM format and console logs; metadata creation is not an emulator test |
| Game already exists | Edit the existing entry or use a different library; imports refuse overwriting/hash-code collisions |
| Symlink rejected | Copy actual game files into the library; symlink traversal is unsupported |
| Sync fails or leaves UI stopped | Reconnect; inspect staging/`previous-*` copies, free space and game path; restore overlay/UI if needed |
| Invalid boot image or unaligned range | Use a compatible Android boot image and aligned ranges; changing the checks is not a compatibility fix |

The CLI does not select among multiple matching USB consoles. Connect one target
console at a time. Report the command, CLI commit, Linux architecture, console model,
firmware, transport, exit code and stderr when diagnosing a problem. Remove private
keys, credentials and unrelated personal paths from reports.

## How the CLI works

| Source | Responsibility |
| --- | --- |
| `hakchi_cli/Program.cs` | Arguments, command dispatch, Linux libusb loading, file/module operations and recovery hmod reading |
| `hakchi_cli/Shell.cs` | Shared clovershell adapter or OpenSSH subprocess; streaming, timeout and cancellation |
| `hakchi_cli/Games.cs` | Offline import/validation and staged game sync |
| `hakchi_cli/FelCommands.cs` | FEL arguments, image/range checks and write verification |
| `Libraries/FelLib/FelLib/` and `FelHelpers/` | Existing low-level boot/flash transport and helpers |
| `hakchi_gui/Clovershell/` | Existing USB shell protocol implementation |
| `hakchi_gui/Apps/DesktopFile.cs`, `CoreCommands.cs` | Existing metadata and emulator command translation |

The project links shared source rather than building the Windows GUI or retargeting
the vendored submodule projects. The `HAKCHI_CLI` compile symbol selects cooperative
thread cleanup, total command timeouts, stdin EOF handling and buffered USB packet
assembly where the original shell code needed adaptation. Windows retains its own
conditional behavior; several small shared transport fixes also apply to it.

LibUsbDotNet's Linux import is resolved to `libusb-1.0.so.0`. Its native event worker
is shut down when the CLI exits, including missing-device cases. The USB parser buffers
split headers/payloads and dispatches multiple packets from a single read. FEL writes
use the underlying transport directly to avoid an old helper that verifies the kernel
address regardless of the requested write destination. Vendored submodule source is
unchanged by this port.

Features requiring substantial GUI orchestration or extra tools were left out:
first-time installation, factory reset, storage expansion, SFROM conversion, automatic
patches/metadata/artwork, folder generation and dedicated save import/export. The goal
is a small terminal adapter around existing code, with those limits explicit.

## Verification and contributing

From the repository root, after building:

```sh
python3 hakchi_cli/tests/test_cli.py --binary hakchi_cli/bin/publish/linux-x64/hakchi
dotnet run --project hakchi_cli/tests/ProtocolTests.csproj -c Release
git diff --check
```

The CLI suite has 12 tests. Its fixture replaces `ssh` with an adapter that runs real
local shell/tar/file operations. It checks argument/FEL validation, game metadata,
binary streams, exit codes, quoting, downloads, backups, modules, sync preservation,
per-game rollback, timeout and cancellation. It does not test actual SSH authentication
or a console's firmware behavior.

The separate protocol check feeds packets into the real shared USB parser without
opening hardware. It checks fragmented/combined frames, binary stdout/stderr, EOF,
exit status and disposal. Native Linux checks also confirmed USB enumeration and
bounded missing-device failures, and an extracted x64 package was smoke-tested for
help, game import, bundled icon lookup and game listing.

Hardware validation is still needed for USB streams, SSH against a console, game/menu
behavior, module scripts, stock-backup retrieval, FEL reads, RAM boot and flash
verification. A passed fixture or parser test is not proof that flashing a console
is safe. Full Windows GUI regression testing was not performed for this port.

Keep changes small, reuse shared code where practical and document commands whose
behavior depends on console firmware. If adding a capability, test the failure paths
as well as the success path, and identify which checks use fixtures versus hardware.
Do not commit generated `bin/` or `obj/` output. Published packages should keep the
payloads, usage guide, GPL license and corresponding source available together with
existing library attributions.

## Credits and license

This port builds on hakchi by madmonkey, hakchi2 by ClusterM and Hakchi2 CE by Team
Shinkansen and contributors. The repository retains its GNU GPL v3 license text in
`LICENSE`. Existing library authorship and license notices remain applicable.
