using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HakchiCli;

// A real boundary: firmware orchestration is independent of SSH/USB lifetime and
// can be exercised with a console that refuses writes or drops the connection.
internal interface IInstallShell
{
    int Run(string command, Stream input = null, Stream output = null, Stream error = null);
}

internal sealed record StockBackup(string Model, string KernelSha256, string FirmwareSha256, string ConsoleId);

internal sealed class FirmwareInstaller
{
    private readonly IInstallShell shell;
    private readonly FirmwarePackage firmware;
    private readonly Func<byte[], string> identify;
    private readonly Action<string> progress;

    internal FirmwareInstaller(IInstallShell shell, FirmwarePackage firmware, Action<string> progress = null,
        Func<byte[], string> identify = null)
    {
        this.shell = shell; this.firmware = firmware;
        this.progress = progress ?? Console.Error.WriteLine;
        this.identify = identify ?? IdentifyStockKernel;
    }

    // These stock-image fingerprints come from Shared.CorrectKernels in the
    // Windows installer. MD5 identifies known images; backups also use SHA-256.
    internal static readonly IReadOnlyDictionary<string, string> StockKernels = new Dictionary<string, string>
    {
        ["d76c2a091ebe7b4614589fc6954653a5"] = "SNES Europe",
        ["c2b57b550f35d64d1c6ce66f9b5180ce"] = "SNES Europe",
        ["0f890bc78cbd9ede43b83b015ba4c022"] = "SNES Europe",
        ["5296e64818bf2d1dbdc6b594f3eefd17"] = "SNES USA",
        ["449b711238575763c6701f5958323d48"] = "SNES USA",
        ["228967ab1035a347caa9c880419df487"] = "SNES USA",
        ["632e179db63d9bcd42281f776a030c14"] = "Super Famicom",
        ["c3378edfc1b96a5268a066d5fbe12d89"] = "Super Famicom"
    };

    internal static string IdentifyStockKernel(byte[] kernel)
    {
        try { FelCommands.ValidateBoot(kernel, FelLib.Fel.kernel_max_size); }
        catch (ArgumentException ex) { throw new IOException(ex.Message, ex); }
        var md5 = Convert.ToHexString(MD5.HashData(kernel)).ToLowerInvariant();
        if (!StockKernels.TryGetValue(md5, out var model))
            throw new IOException("The kernel is not a recognized stock SNES Mini/Super Famicom image. Installation was stopped.");
        return model;
    }

    private byte[] ReadStock()
    {
        Check("sntool isRecovery && [ -f /hakchi/config ] && grep -q 'hakchi-shell' /proc/cmdline",
            "Connect to the RAM recovery system first, using firmware-boot.");
        Check("hakchi isOriginalBoot2", "The console is not stock. This installer does not repair or migrate modified consoles.");
        using var output = new MemoryStream();
        Check("hakchi readBoot2", "Could not read the original kernel.", output: output);
        if (output.Length == 0 || output.Length > FelLib.Fel.kernel_max_size) throw new IOException("Invalid original-kernel backup size.");
        return output.ToArray();
    }

    public StockBackup BackUp(string directory)
    {
        progress("Reading and identifying the stock kernel…");
        var kernel = ReadStock();
        var backup = new StockBackup(identify(kernel), FirmwarePackage.Hash(kernel), FirmwarePackage.Hash(firmware.Archive), ReadConsoleId());
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "stock-kernel.img");
        // Never replace another console's backup or an incomplete existing file.
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).SequenceEqual(kernel)) throw new IOException("Backup directory contains a different kernel. Choose a new directory.");
        }
        else WriteNew(path, kernel);
        if (FirmwarePackage.Hash(File.ReadAllBytes(path)) != backup.KernelSha256) throw new IOException("Local backup verification failed.");
        EnsureFile(Path.Combine(directory, "stock-kernel.img.sha256"), Encoding.UTF8.GetBytes(backup.KernelSha256 + "  stock-kernel.img\n"));
        EnsureFile(Path.Combine(directory, "installation.json"), JsonSerializer.SerializeToUtf8Bytes(backup, new JsonSerializerOptions { WriteIndented = true }));
        progress($"Verified {backup.Model}. Original kernel saved to {path}");
        return backup;
    }

    private StockBackup LoadBackup(string directory)
    {
        var backup = JsonSerializer.Deserialize<StockBackup>(File.ReadAllText(Path.Combine(directory, "installation.json")))
            ?? throw new IOException("Invalid installation record. Run firmware-check first.");
        var kernel = File.ReadAllBytes(Path.Combine(directory, "stock-kernel.img"));
        if (identify(kernel) != backup.Model || FirmwarePackage.Hash(kernel) != backup.KernelSha256 ||
            backup.FirmwareSha256 != FirmwarePackage.Hash(firmware.Archive) || !ValidConsoleId(backup.ConsoleId))
            throw new IOException("Backup or firmware verification failed. No installation was started.");
        return backup;
    }

    public void Install(string directory, byte[] modules)
    {
        var backup = LoadBackup(directory);
        var current = ReadStock();
        if (identify(current) != backup.Model || FirmwarePackage.Hash(current) != backup.KernelSha256 || ReadConsoleId() != backup.ConsoleId)
            throw new IOException("The console identity or kernel no longer matches the verified backup. Installation was stopped.");
        Check("command -v sha256sum >/dev/null && [ ! -d /hakchi/transfer ]",
            "Recovery has an unfinished transfer. Power off and boot a fresh RAM recovery before retrying.");
        var stage = "/tmp/hakchi-install-" + Guid.NewGuid().ToString("N");
        progress("Uploading and verifying firmware and base modules in RAM…");
        Check($"mkdir -p {Shell.Quote(stage)}", "Could not create RAM staging directory.");
        // Until every upload verifies, no install flags or persistent writes occur.
        Upload(stage + "/hakchi.hmod", firmware.Archive);
        Upload(stage + "/base-modules.tar", modules);
        Check($"mkdir {Shell.Quote(stage + "/transfer")} && tar -xf {Shell.Quote(stage + "/base-modules.tar")} -C {Shell.Quote(stage + "/transfer")} && mv {Shell.Quote(stage + "/hakchi.hmod")} {Shell.Quote(stage + "/transfer/hakchi.hmod")}",
            "Could not prepare installation modules.");
        Check("printf '\nexport cf_install=y\nexport cf_update=y\n' >> /hakchi/config", "Could not set recovery installation flags.");
        Check($"mv {Shell.Quote(stage + "/transfer")} /hakchi/transfer", "Could not activate the RAM transfer.");
        progress("Installing Hakchi. Keep USB and power connected; do not switch the console off.");
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var exit = shell.Run("boot", output: output, error: error);
        var detail = Encoding.UTF8.GetString(output.ToArray()) + Encoding.UTF8.GetString(error.ToArray());
        progress(detail);
        if (detail.Contains("flash md5 mismatch!", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("flash boot0 failed!", StringComparison.OrdinalIgnoreCase) ||
            (exit != 0 && exit != 255))
            throw new IOException("Recovery reported an installation failure. Keep the backup and inspect the activity output before retrying.");
        // SSH normally closes at switch_root. A disconnect is never proof of a
        // successful flash; the next command checks the running system and backup.
        progress("Installation handoff finished; success is not confirmed yet. Wait for the menu, then run firmware-verify with this backup directory.");
    }

    public StockBackup Verify(string directory)
    {
        var backup = LoadBackup(directory);
        Check("! sntool isRecovery && [ -f /etc/preinit ] && [ -d /var/lib/hakchi ] && [ \"$(hakchi currentFirmware)\" = _nand_ ]",
            "The installed system is not ready. Wait for its menu and check the SSH connection before retrying verification.");
        if (ReadConsoleId() != backup.ConsoleId)
            throw new IOException("The console identity does not match the saved backup. Installation could not be verified.");
        using var installedUboot = new MemoryStream();
        Check("hakchi readBoot1", "Could not verify U-Boot stored in flash.", output: installedUboot);
        var uboot = installedUboot.ToArray();
        if (uboot.Length < firmware.Uboot.Length || !uboot.AsSpan(0, firmware.Uboot.Length).SequenceEqual(firmware.Uboot) ||
            uboot.AsSpan(firmware.Uboot.Length).IndexOfAnyExcept((byte)0) != -1)
            throw new IOException("U-Boot stored in flash does not match the bundled firmware. Installation could not be verified.");
        using var installedBoot = new MemoryStream();
        Check("hakchi readBoot2", "Could not verify the firmware stored in flash.", output: installedBoot);
        if (!MatchesInstalledBoot(installedBoot.ToArray(), firmware.Boot))
            throw new IOException("The boot image stored in flash does not match the bundled firmware. Installation could not be verified.");
        using var output = new MemoryStream();
        Check("hakchi getBackup2", "The installed system could not return its original-kernel backup.", output: output);
        if (FirmwarePackage.Hash(output.ToArray()) != backup.KernelSha256)
            throw new IOException("The console's original-kernel backup does not match the saved backup. Installation could not be verified.");
        progress($"Verified Hakchi is installed on {backup.Model}; the original-kernel backup matches.");
        return backup;
    }

    internal static bool MatchesInstalledBoot(byte[] installed, byte[] expected)
    {
        if (installed.Length < expected.Length || expected.Length < 576) return false;
        // Upstream insertKeyFile changes only the 512-byte Android command line.
        // The kernel, ramdisk, header and remaining payload must match the release.
        return installed.AsSpan(0, 64).SequenceEqual(expected.AsSpan(0, 64)) &&
            installed.AsSpan(576, expected.Length - 576).SequenceEqual(expected.AsSpan(576)) &&
            installed.AsSpan(expected.Length).IndexOfAnyExcept((byte)0) == -1;
    }

    private static bool ValidConsoleId(string id) => id != null &&
        System.Text.RegularExpressions.Regex.IsMatch(id, "\\A[0-9a-f]{8}( [0-9a-f]{8}){3}\\z");

    private string ReadConsoleId()
    {
        using var output = new MemoryStream();
        Check("hakchi hwid", "Could not read the console hardware identity.", output: output);
        var id = Encoding.ASCII.GetString(output.ToArray()).Trim().ToLowerInvariant();
        if (!ValidConsoleId(id)) throw new IOException("Invalid console hardware identity. Installation was stopped.");
        return id;
    }

    private void Upload(string path, byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        Check($"cat > {Shell.Quote(path)}", "RAM upload failed.", input);
        using var output = new MemoryStream();
        Check($"sha256sum {Shell.Quote(path)}", "Could not verify RAM upload.", output: output);
        var returned = Encoding.UTF8.GetString(output.ToArray()).Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (returned != FirmwarePackage.Hash(bytes)) throw new IOException("RAM upload checksum mismatch. Installation was not started.");
    }

    private void Check(string command, string message, Stream input = null, Stream output = null)
    {
        using var error = new MemoryStream();
        if (shell.Run(command, input, output ?? Stream.Null, error) != 0)
            throw new IOException(message + " " + Encoding.UTF8.GetString(error.ToArray()).Trim());
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        file.Write(bytes); file.Flush(true);
    }

    private static void EnsureFile(string path, byte[] bytes)
    {
        if (!File.Exists(path)) WriteNew(path, bytes);
        else if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new IOException("Backup metadata already exists and differs. Choose a new backup directory.");
    }
}
