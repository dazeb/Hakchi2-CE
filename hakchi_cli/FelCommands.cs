using System.Diagnostics;
using FelHelpers;
using FelLib;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace HakchiCli;

internal static class FelCommands
{
    public static int Run(string[] args, TimeSpan timeout, CancellationToken cancellation)
    {
        if (args.Length == 0 || args.Contains("--help") || args[0] == "help")
        {
            Console.WriteLine("""
            hakchi fel COMMAND [-f FES1.bin] -u UBOOT.bin [OPTIONS]
              memboot         -b BOOT.img                    Boot from RAM
              read-nand       -a ADDRESS -l LENGTH -o FILE   Read flash to a new file
              flash-boot      -b BOOT.img --yes              Write a boot image
              flash-uboot     --yes                          Write the primary U-Boot image
              flash-nand      -a ADDRESS -i FILE [--verify] --yes
              run-command     -c 'UBOOT COMMAND' [--noreturn]
              update-enter-fel                               Enter FEL from burn mode
            Addresses/lengths accept decimal or 0xHEX. Default FES1 is included.
            U-Boot and boot images must match your console; obtain them from compatible hakchi payloads.
            """);
            return 0;
        }
        var command = args[0];
        var values = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "--yes" or "--verify" or "--noreturn") { flags.Add(key); continue; }
            if (key is not ("-f" or "-u" or "-b" or "-a" or "-l" or "-o" or "-i" or "-c") || ++i == args.Length)
                throw new ArgumentException($"Unknown or incomplete FEL option: {key}");
            if (!values.TryAdd(key, args[i])) throw new ArgumentException($"Duplicate option: {key}");
        }
        var required = command switch
        {
            "memboot" or "flash-boot" => new[] { "-u", "-b" }, "flash-uboot" => new[] { "-u" },
            "read-nand" => new[] { "-u", "-a", "-l", "-o" }, "flash-nand" => new[] { "-u", "-a", "-i" },
            "run-command" => new[] { "-u", "-c" }, "update-enter-fel" => Array.Empty<string>(),
            _ => throw new ArgumentException($"Unknown FEL command: {command}")
        };
        foreach (var key in required) if (!values.ContainsKey(key)) throw new ArgumentException($"Missing FEL option {key}.");
        foreach (var key in values.Keys)
            if (!required.Contains(key) && !(key == "-f" && command != "update-enter-fel")) throw new ArgumentException($"Option {key} does not apply to {command}.");
        foreach (var flag in flags)
            if (!(flag == "--yes" && command.StartsWith("flash-")) && !(flag == "--verify" && command == "flash-nand") && !(flag == "--noreturn" && command == "run-command"))
                throw new ArgumentException($"Option {flag} does not apply to {command}.");
        if (command.StartsWith("flash-") && !flags.Contains("--yes")) throw new ArgumentException("Flashing writes console storage. Add --yes after verifying your image and backup.");
        byte[] fes = null, uboot = null;
        if (command != "update-enter-fel")
        {
            fes = File.ReadAllBytes(values.GetValueOrDefault("-f", Path.Combine(AppContext.BaseDirectory, "payloads", "fes1.bin")));
            uboot = File.ReadAllBytes(values["-u"]);
            if (fes.Length == 0 || uboot.Length == 0) throw new ArgumentException("FES1 and U-Boot images must not be empty.");
            if (uboot.Length > Fel.uboot_maxsize_f) throw new ArgumentException("U-Boot image exceeds 2 MiB.");
        }
        var boot = values.ContainsKey("-b") ? File.ReadAllBytes(values["-b"]) : null;
        var input = values.ContainsKey("-i") ? File.ReadAllBytes(values["-i"]) : null;
        using (var helpers = new Helpers(fes, uboot))
        {
            var address = values.ContainsKey("-a") ? Number(values["-a"]) : 0;
            var length = values.ContainsKey("-l") ? Number(values["-l"]) : 0;
            if (command == "read-nand" && (length == 0 || length > int.MaxValue || address % Fel.sector_size != 0 || length % Fel.sector_size != 0 || (ulong)address + length > uint.MaxValue))
                throw new ArgumentException("Flash read address and length must be aligned to 128 KiB sectors and fit the address range.");
            if (command == "read-nand" && File.Exists(values["-o"])) throw new IOException("Output already exists; choose a new backup filename.");
            if (boot != null) ValidateBoot(boot, command == "flash-boot" ? Fel.kernel_max_size : Fel.transfer_max_size);
            if (command == "flash-nand" && (input.Length == 0 || address % Fel.sector_size != 0 || (ulong)address + (ulong)input.Length + Fel.sector_size > uint.MaxValue))
                throw new ArgumentException("Flash input must be nonempty and the address aligned to a 128 KiB sector.");
            helpers.SetStatus += Console.Error.WriteLine;
            helpers.WriteLine += Console.Error.WriteLine;
            helpers.SetProgress += (_, _) => cancellation.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (UsbDevice.AllDevices.Cast<UsbRegistry>().Any(d => d.Vid == 0x1f3a && d.Pid == 0xefe8)) break;
                if (clock.Elapsed >= timeout) throw new TimeoutException("No FEL console found. Connect in FEL mode and check USB permissions.");
                cancellation.WaitHandle.WaitOne(100);
            }
            cancellation.ThrowIfCancellationRequested();
            switch (command)
            {
                case "memboot": helpers.Memboot(boot); break;
                case "flash-boot":
                case "flash-uboot":
                case "flash-nand":
                    // The old helper verifies kernel_base_f regardless of the supplied address.
                    // Use its underlying FEL transport so arbitrary NAND writes verify the correct range.
                    if (command == "flash-boot") { input = boot; address = Fel.kernel_base_f; }
                    if (command == "flash-uboot") { input = uboot; address = Fel.uboot_base_f; }
                    Array.Resize(ref input, checked((int)(((long)input.Length + Fel.sector_size - 1) / Fel.sector_size * Fel.sector_size)));
                    using (var fel = new Fel { Fes1Bin = fes, UBootBin = uboot })
                    {
                        if (!fel.Open()) throw new IOException("Cannot open FEL console. Check USB permissions.");
                        Fel.OnFelProgress progress = (_, _) => cancellation.ThrowIfCancellationRequested();
                        fel.WriteFlash(address, input, progress);
                        if ((command != "flash-nand" || flags.Contains("--verify")) && !fel.ReadFlash(address, (uint)input.Length, progress).SequenceEqual(input))
                            throw new IOException("Flash verification failed.");
                        if (command != "flash-nand") fel.RunUbootCmd("shutdown", true, progress);
                    }
                    break;
                case "read-nand":
                    var data = helpers.ReadFlash(fes, uboot, address, length);
                    using (var file = new FileStream(values["-o"], FileMode.CreateNew)) { file.Write(data); file.Flush(true); }
                    break;
                case "run-command": helpers.RunCommand(fes, uboot, values["-c"], flags.Contains("--noreturn")); break;
                case "update-enter-fel":
                    using (var fel = new Fel())
                    {
                        if (!fel.Open(isFel: false) || !fel.UsbUpdateProbe() || !fel.UsbUpdateEnterFel()) throw new IOException("Burn-mode handshake failed.");
                    }
                    break;
            }
        }
        return 0;
    }

    private static uint Number(string value)
    {
        try { return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(value[2..], 16) : uint.Parse(value); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new ArgumentException($"Invalid number: {value}"); }
    }

    private static void ValidateBoot(byte[] boot, uint maximum)
    {
        if (boot.Length < 576 || System.Text.Encoding.ASCII.GetString(boot, 0, 8) != "ANDROID!") throw new ArgumentException("Invalid Android boot image.");
        uint Field(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(offset, 4));
        var page = Field(36);
        if (page < 576 || page > 65536 || (page & (page - 1)) != 0) throw new ArgumentException("Invalid Android boot page size.");
        ulong size = page;
        foreach (var offset in new[] { 8, 16, 24, 40 }) size += ((ulong)Field(offset) + page - 1) / page * page;
        if (Field(8) == 0 || size > (ulong)boot.Length || boot.Length > maximum || (ulong)boot.Length > (size + Fel.sector_size - 1) / Fel.sector_size * Fel.sector_size)
            throw new ArgumentException("Invalid Android boot image size.");
    }
}
