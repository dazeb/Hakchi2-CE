using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using com.clusterrr.hakchi_gui;

namespace HakchiCli;

internal static class Program
{
    private static bool usbConfigured;
    private const string Help = """
    hakchi — Linux terminal port
    Usage: hakchi [--host HOST] [--port 22] [--timeout 30] COMMAND ...
    Default transport: USB clovershell. --host uses root@HOST via OpenSSH.

      devices                         List USB devices (console ID: 1f3a:efe8)
      status                          Firmware, storage and game sync path
      exec 'COMMAND'                  Run a console command; supports piped stdin
      upload FILE /REMOTE/FILE        Upload a file
      download /REMOTE/FILE FILE      Download a file atomically
      backup FILE                     Download the console's stock kernel backup
      mod-install MOD.hmod            Install a tar.gz hmod file or hmod directory
      mod-uninstall NAME              Uninstall a module
      game-add ROM LIBRARY --core CORE [--name NAME] [--icon PNG]
      game-list LIBRARY               List prepared games, offline
      sync LIBRARY /REMOTE/GAMES      Merge games into menu 000; keep other games/saves
      boot HAKCHI.hmod                FEL RAM boot from a compatible hakchi recovery hmod
      fel ARGS...                     FEL commands: memboot, read-nand, flash-boot,
                                      flash-nand, flash-uboot, run-command, update-enter-fel
      fel --help                      Show FEL options; --yes required for flash commands

    --timeout is in seconds and applies per connection/remote command. Ctrl-C cancels.
    Requires libusb-1.0; network commands also require OpenSSH. See hakchi_cli/README.md.
    """;

    public static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try { return Run(args, cancellation.Token); }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (Exception) when (cancellation.IsCancellationRequested) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally
        {
            // LibUsbDotNet owns a native event thread even when no console is present.
            if (usbConfigured) try { UsbDevice.Exit(); } catch (Exception) { }
        }
    }

    private static int Run(string[] args, CancellationToken cancellation)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Console.WriteLine(Help); return 0; }
        string host = null;
        int port = 22, timeout = 30, index = 0;
        while (index < args.Length && args[index].StartsWith("--", StringComparison.Ordinal))
        {
            var option = args[index++];
            if (index >= args.Length) throw new ArgumentException($"Missing value for {option}.");
            var value = args[index++];
            switch (option)
            {
                case "--host": host = value; break;
                case "--port": if (!int.TryParse(value, out port) || port < 1 || port > 65535) throw new ArgumentException("Invalid port."); break;
                case "--timeout": if (!int.TryParse(value, out timeout) || timeout < 1 || timeout > 86400) throw new ArgumentException("Timeout must be 1–86400 seconds."); break;
                default: throw new ArgumentException($"Unknown option: {option}");
            }
        }
        if (index == args.Length) throw new ArgumentException("Missing command. Run hakchi --help.");
        if (host != null && (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.Contains('@') || host[0] == '-'))
            throw new ArgumentException("--host must be a hostname or IP address.");
        var command = args[index++];
        var tail = args[index..];
        if (command == "game-add")
        {
            if (tail.Length < 4) throw new ArgumentException("game-add ROM LIBRARY --core CORE [--name NAME] [--icon PNG]");
            string core = null, name = null, icon = null;
            for (var i = 2; i < tail.Length; i += 2)
            {
                if (i + 1 >= tail.Length) throw new ArgumentException($"Missing value for {tail[i]}");
                switch (tail[i])
                {
                    case "--core": core = tail[i + 1]; break;
                    case "--name": name = tail[i + 1]; break;
                    case "--icon": icon = tail[i + 1]; break;
                    default: throw new ArgumentException($"Unknown game option: {tail[i]}");
                }
            }
            if (core == null) throw new ArgumentException("Choose an installed emulator with --core.");
            Games.Add(tail[0], tail[1], core, name, icon);
            return 0;
        }
        if (command == "game-list")
        {
            Require(tail, 1);
            foreach (var path in Games.Validate(tail[0]))
            {
                var code = Path.GetFileName(path);
                var desktop = new DesktopFile();
                desktop.Load(Path.Combine(path, code + ".desktop"));
                Console.WriteLine($"{code}\t{desktop.Name}\t{desktop.Exec}");
            }
            return 0;
        }
        if (command is "devices" or "fel" or "boot" || host == null) InitUsb();
        if (command == "devices")
        {
            Require(tail, 0);
            var found = false;
            foreach (UsbRegistry device in UsbDevice.AllDevices)
            {
                var console = device.Vid == 0x1f3a && device.Pid == 0xefe8;
                found |= console;
                Console.WriteLine($"{device.Vid:x4}:{device.Pid:x4}{(console ? "  FEL/clovershell console" : "")}");
            }
            return found ? 0 : 3;
        }
        if (command == "fel")
        {
            if (tail.Length == 0) tail = new[] { "--help" };
            if (tail[0].StartsWith("flash-", StringComparison.Ordinal) && !tail.Contains("--yes") && !tail.Contains("--help"))
                throw new ArgumentException("Flashing writes console storage. Add --yes after verifying your image and backup.");
            return FelCommands.Run(tail, TimeSpan.FromSeconds(timeout), cancellation);
        }
        if (command == "boot")
        {
            Require(tail, 1);
            return Boot(tail[0], host != null, timeout, cancellation);
        }
        var count = command switch
        {
            "status" => 0, "exec" or "backup" or "mod-install" or "mod-uninstall" => 1,
            "upload" or "download" or "sync" => 2,
            _ => throw new ArgumentException($"Unknown command: {command}. Run hakchi --help.")
        };
        Require(tail, count);
        // Validate local inputs before opening the console or stopping its UI.
        string[] games = command == "sync" ? Games.Validate(tail[0]) : null;
        if (command is "sync" or "upload") RemotePath(tail[1]);
        if (command == "download") RemotePath(tail[0]);
        if (command is "upload" or "mod-install" && !File.Exists(tail[0]) && !Directory.Exists(tail[0])) throw new FileNotFoundException(tail[0]);
        if (command == "upload" && Directory.Exists(tail[0])) throw new ArgumentException("upload accepts a file.");
        if (command == "mod-install" && !tail[0].EndsWith(".hmod", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Module must be a .hmod file or directory.");
        using var shell = new Shell(host, port, timeout, cancellation);
        switch (command)
        {
            case "status": return shell.Run("uname -a && cat /var/version && hakchi currentFirmware && hakchi findGameSyncStorage && df -h");
            case "exec": return shell.Run(tail[0], Console.IsInputRedirected ? Console.OpenStandardInput() : null);
            case "upload": using (var input = File.OpenRead(tail[0])) shell.Check($"cat > {Shell.Quote(tail[1])}", input); break;
            case "download": Download(shell, $"cat {Shell.Quote(tail[0])}", tail[1]); break;
            case "backup": Download(shell, "hakchi getBackup2", tail[0]); break;
            case "mod-install": InstallMod(shell, tail[0]); break;
            case "mod-uninstall": shell.Check($"hakchi pack_uninstall {Shell.Quote(tail[0])}"); break;
            case "sync": Games.Sync(shell, games, tail[1]); break;
        }
        return 0;
    }

    private static void InitUsb()
    {
        if (!OperatingSystem.IsLinux()) return;
        usbConfigured = true;
        NativeLibrary.SetDllImportResolver(typeof(UsbDevice).Assembly, (name, assembly, path) =>
            name == "libusb-1.0" ? NativeLibrary.Load("libusb-1.0.so.0", assembly, path) : IntPtr.Zero);
    }

    private static void Require(string[] args, int count)
    { if (args.Length != count) throw new ArgumentException($"Expected {count} argument(s). Run hakchi --help."); }

    private static void RemotePath(string path)
    {
        if (!path.StartsWith('/') || path == "/" || path.Split('/').Any(p => p is "." or "..") || path.Any(char.IsControl))
            throw new ArgumentException("Use an absolute console path below /, without . or .. components.");
    }

    private static void Download(Shell shell, string command, string destination)
    {
        var target = Path.GetFullPath(destination);
        var temporary = target + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew))
            {
                shell.Check(command, output: output);
                if (output.Length == 0) throw new IOException("Console returned an empty file; destination was kept.");
                output.Flush(true);
            }
            using (var file = File.OpenRead(temporary)) Console.Error.WriteLine($"SHA256 {Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant()}");
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void InstallMod(Shell shell, string path)
    {
        var remote = "/tmp/hakchi-cli-" + Guid.NewGuid().ToString("N");
        var archive = Path.GetTempFileName();
        try
        {
            shell.Check($"mkdir -p {Shell.Quote(remote + "/" + Path.GetFileName(path))}");
            if (Directory.Exists(path))
            {
                using var tar = File.Create(archive);
                TarFile.CreateFromDirectory(path, tar, false);
            }
            using var input = File.OpenRead(Directory.Exists(path) ? archive : path);
            shell.Check($"tar -x{(Directory.Exists(path) ? "" : "z")} -C {Shell.Quote(remote + "/" + Path.GetFileName(path))}", input);
            shell.Check($"hakchi packs_install {Shell.Quote(remote)}");
        }
        finally
        {
            File.Delete(archive);
            try { shell.Check($"rm -rf {Shell.Quote(remote)}"); } catch (IOException) { }
        }
    }

    private static int Boot(string path, bool network, int timeout, CancellationToken cancellation)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "hakchi-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            using var input = File.OpenRead(path);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            TarEntry entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                var name = entry.Name.TrimStart('.', '/');
                if (name is not ("boot/boot.img" or "boot/uboot.bin") || entry.DataStream == null) continue;
                if (entry.Length > (name == "boot/boot.img" ? FelLib.Fel.transfer_max_size : FelLib.Fel.uboot_maxsize_f)) throw new IOException("Recovery payload is too large.");
                using var output = File.Create(Path.Combine(temporary, Path.GetFileName(name)));
                entry.DataStream.CopyTo(output);
            }
            var boot = Path.Combine(temporary, "boot.img");
            var kernel = File.ReadAllBytes(boot);
            if (kernel.Length < 576 || System.Text.Encoding.ASCII.GetString(kernel, 0, 8) != "ANDROID!") throw new IOException("Invalid recovery boot image.");
            var cmdline = System.Text.Encoding.ASCII.GetString(kernel, 64, 512).TrimEnd('\0').Replace("hakchi-shell", "").Replace("hakchi-clovershell", "").Trim();
            var bytes = System.Text.Encoding.ASCII.GetBytes(cmdline + (network ? " hakchi-shell" : " hakchi-clovershell"));
            if (bytes.Length >= 512) throw new IOException("Recovery boot command line is too long.");
            Array.Clear(kernel, 64, 512);
            bytes.CopyTo(kernel, 64);
            File.WriteAllBytes(boot, kernel);
            return FelCommands.Run(new[] { "memboot", "-u", Path.Combine(temporary, "uboot.bin"), "-b", boot }, TimeSpan.FromSeconds(timeout), cancellation);
        }
        finally { Directory.Delete(temporary, true); }
    }
}
