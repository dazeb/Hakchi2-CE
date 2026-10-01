# Hakchi Linux CLI

A small terminal port that reuses Hakchi's FEL/USB shell libraries, desktop metadata
serializer and emulator command mappings. No WinForms, Wine or Windows driver installer.

This is a minimal port, not feature parity with the Windows GUI. Hardware transfers and
flashing still need validation on a real NES/SNES Classic. Modern hakchi firmware normally
uses SSH over USB Ethernet or a network connection; legacy clovershell uses direct USB.

## Build and run

Initialize the source submodules if you have just cloned the repository:

```sh
git submodule update --init Libraries/FelLib
# Build requires the .NET 10 SDK. The published executable includes its .NET runtime.
bash hakchi_cli/publish.sh
hakchi_cli/bin/publish/linux-x64/hakchi --help
```

Keep the `payloads/` directory beside the executable. It contains the repository's
FES1 loader and a default game icon. `publish.sh linux-arm64` builds for ARM64 Linux.
The system still needs `libusb-1.0` (Debian/Ubuntu: `libusb-1.0-0`), and `openssh-client`
for network commands. The versioned Linux libusb library loads automatically.

For direct USB/FEL access, install the provided rule, reload udev, and reconnect the cable:

```sh
sudo install -m 644 hakchi_cli/70-hakchi.rules /etc/udev/rules.d/70-hakchi.rules
sudo udevadm control --reload-rules
```

The rule grants the active local desktop user access. Headless machines should grant
USB access to an appropriate local group instead. The CLI does not require running as root.

## Commands

For convenience, replace `hakchi` below with the published executable path, or add
its directory to `PATH`. Global options go **before** the command. Network commands use
the standard `ssh` client and its existing host-key checks, keys, configuration and prompts.
Use your console's actual address; `hakchi.local` depends on local hostname discovery.

```sh
hakchi devices                              # exit 3 when no console is found
hakchi --timeout 5 status                    # direct USB clovershell
hakchi --host hakchi.local status            # modern firmware, SSH
hakchi --host hakchi.local exec 'ls /var/lib/hakchi'
printf 'hello\n' | hakchi --host hakchi.local exec 'cat > /tmp/example'
hakchi --host hakchi.local upload ./config.txt /tmp/config.txt
hakchi --host hakchi.local download /tmp/config.txt ./downloaded.txt
hakchi --host hakchi.local backup ./stock-kernel.img
hakchi --host hakchi.local mod-install ./snes9x.hmod
hakchi --host hakchi.local mod-uninstall snes9x
```

`backup` downloads `hakchi getBackup2` from firmware which already has that backup.
It does not create an original stock-console backup before initial modification.
Downloads keep the old destination on failure and print a SHA256 after success.
`mod-install` accepts gzip-compressed tar `.hmod` files or unpacked `.hmod` directories.
Modules run code on the console; choose modules intended for your model/firmware.

### Add and upload games

```sh
hakchi game-add ./game.sfc ./games --core snes9x --name 'My Game'
hakchi game-add ./game.nes ./games --core fceumm --icon ./cover.png
hakchi game-list ./games
# Choose the writable game storage path from status/existing console configuration.
hakchi --host hakchi.local --timeout 300 sync ./games /var/lib/hakchi/games/snes-usa
```

The named emulator command must be installed on the console (usually through RetroArch
and an appropriate core hmod). Raw SNES ROMs work through a suitable core; this port
does not convert them to Canoe SFROM files. The importer copies ROM bytes unchanged.
It generates the same desktop format as the Windows app, using `/var/games` and
`/var/saves`, and supplies a default icon. Optional PNG artwork is copied without resizing.
Edit the generated `.desktop` file for players, save slots, arguments and metadata.

`sync` also accepts existing flat `CLV-*` game directories with valid `.desktop` files.
It stages all files, then merges the supplied games into menu `000`. Existing games,
other menu pages and saves are preserved; this does not prune, rebuild custom folders,
or copy stock games automatically. Choose the path matching your console's separate
game storage setting. A failed commit keeps previous copies in the reported staging
directory for recovery. Symlinked local game inputs are rejected.

### FEL and recovery

```sh
hakchi fel --help
# Put the console in FEL mode using the normal reset/power sequence for its model.
hakchi --timeout 15 boot ./hakchi.hmod         # boot its recovery image into RAM
hakchi --host hakchi.local boot ./hakchi.hmod # request SSH in the recovery boot image
hakchi fel memboot -u ./uboot.bin -b ./boot.img
hakchi fel read-nand -u ./uboot.bin -a 0x600000 -l 0x400000 -o ./kernel-region.bin
hakchi fel flash-boot -u ./uboot.bin -b ./boot.img --yes
```

Supply compatible U-Boot/boot images or a compatible `hakchi.hmod` recovery payload;
firmware is not automatically downloaded. `boot` reads only the boot image and U-Boot
from the hmod and forces the requested shell mode. It does not install firmware.
Initial installation, storage expansion and factory reset are not automated; the
existing low-level FEL write commands remain available with an explicit `--yes`.
Keep a verified original backup before using write commands. `flash-nand` requires
128 KiB address alignment, pads to full sectors, and supports `--verify` at the written
address. `read-nand` requires aligned addresses and lengths and refuses to overwrite an
existing file. `flash-uboot` writes the primary image; alternate copies are not managed.
Boot images are checked before opening the device; boot/U-Boot writes verify their
actual destination addresses.

`--timeout` bounds device discovery and each remote shell command. FEL transfers retain
the underlying library's USB timeouts. Ctrl-C cancels discovery and transfer progress;
a USB operation already in progress may take time to return. Exit codes: 0 success,
1 operation failure, 2 invalid arguments, 3 no console in `devices`, 130 cancellation;
`exec` forwards the remote command/OpenSSH exit code.

## Verification

```sh
python3 hakchi_cli/tests/test_cli.py --binary hakchi_cli/bin/publish/linux-x64/hakchi
dotnet run --project hakchi_cli/tests/ProtocolTests.csproj -c Release
```

The regression suite exercises real local shell/tar/file operations through a fixture
SSH adapter, including binary transfers, rollback and cancellation. It does not emulate
USB hardware, Nintendo's menu, SSH authentication or console firmware. `devices` and
missing-device timeouts can be checked on Linux without modifying any console.
The separate USB protocol check covers fragmented and combined frames, binary output,
EOF, exit status and disposal without opening a device.

The Windows GUI project and vendored submodules remain intact. CLI-specific cancellation
uses conditional compilation in the shared clovershell files. Source redistribution
retains the repository's GPL license and existing library attributions.
