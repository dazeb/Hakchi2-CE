using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HakchiCli;

internal sealed record FirmwareRelease(string Version, string Url, string Sha256);

internal sealed class FirmwarePackage
{
    internal static readonly FirmwareRelease Release = JsonSerializer.Deserialize<FirmwareRelease>(
        typeof(FirmwarePackage).Assembly.GetManifestResourceStream("HakchiCli.firmware.json"),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "payloads", "hakchi.hmod");
    public byte[] Archive { get; }
    public byte[] Boot { get; }
    public byte[] Uboot { get; }

    internal FirmwarePackage(byte[] archive, byte[] boot, byte[] uboot)
    { Archive = archive; Boot = boot; Uboot = uboot; }

    public static FirmwarePackage Load(string path)
    {
        if (!File.Exists(path)) throw new IOException("Firmware is missing. Use the desktop AppImage, or run hakchi firmware-download FILE.");
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Firmware package is too large.");
        var archive = File.ReadAllBytes(path);
        if (Hash(archive) != Release.Sha256) throw new IOException("Firmware SHA-256 does not match the pinned Hakchi release. Nothing was sent to the console.");
        byte[] boot = null, uboot = null;
        using var input = new MemoryStream(archive);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry entry;
        while ((entry = tar.GetNextEntry()) != null)
        {
            var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (name is not ("boot/boot.img" or "boot/uboot.bin")) continue;
            var maximum = name == "boot/boot.img" ? FelLib.Fel.transfer_max_size : FelLib.Fel.uboot_maxsize_f;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Length == 0 || entry.Length > maximum)
                throw new IOException("Invalid firmware recovery payload.");
            using var data = new MemoryStream();
            entry.DataStream.CopyTo(data);
            if (name == "boot/boot.img") { if (boot != null) throw new IOException("Duplicate boot image."); boot = data.ToArray(); }
            else { if (uboot != null) throw new IOException("Duplicate U-Boot image."); uboot = data.ToArray(); }
        }
        if (boot == null || uboot == null) throw new IOException("Firmware recovery payloads are missing.");
        FelCommands.ValidateBoot(boot, FelLib.Fel.transfer_max_size);
        return new FirmwarePackage(archive, boot, uboot);
    }

    public static void Download(string path, CancellationToken cancellation)
    {
        var target = Path.GetFullPath(path);
        if (File.Exists(target)) throw new IOException("Firmware destination already exists; choose a new filename.");
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var response = client.GetAsync(Release.Url, HttpCompletionOption.ResponseHeadersRead, cancellation).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var input = response.Content.ReadAsStream(cancellation);
            using (var output = new FileStream(temporary, FileMode.CreateNew))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = input.ReadAsync(buffer, cancellation).AsTask().GetAwaiter().GetResult()) != 0)
                {
                    if (output.Length + count > 16 * 1024 * 1024) throw new IOException("Firmware download is too large.");
                    output.Write(buffer, 0, count);
                }
                output.Flush(true);
            }
            Load(temporary);
            File.Move(temporary, target);
            Console.WriteLine($"Verified firmware {Release.Version}: {target}");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public int BootRecovery(int timeout, CancellationToken cancellation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "hakchi-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var boot = Boot.ToArray();
            var command = System.Text.Encoding.ASCII.GetString(boot, 64, 512).TrimEnd('\0')
                .Replace("hakchi-shell", "").Replace("hakchi-clovershell", "").Trim() + " hakchi-shell";
            var bytes = System.Text.Encoding.ASCII.GetBytes(command);
            if (bytes.Length >= 512) throw new IOException("Recovery command line is too long.");
            Array.Clear(boot, 64, 512);
            bytes.CopyTo(boot, 64);
            var bootPath = Path.Combine(directory, "boot.img");
            var ubootPath = Path.Combine(directory, "uboot.bin");
            File.WriteAllBytes(bootPath, boot);
            File.WriteAllBytes(ubootPath, Uboot);
            Console.Error.WriteLine("Booting recovery into RAM. Console firmware is not being installed yet.");
            return FelCommands.Run(new[] { "memboot", "-u", ubootPath, "-b", bootPath }, TimeSpan.FromSeconds(timeout), cancellation);
        }
        finally { Directory.Delete(directory, true); }
    }

    public static byte[] BaseModules()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "payloads", "base-modules");
        if (!Directory.Exists(Path.Combine(path, "clovercon.hmod"))) throw new IOException("Base modules are missing. Use a complete desktop AppImage.");
        using var output = new MemoryStream();
        TarFile.CreateFromDirectory(path, output, false);
        return output.ToArray();
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
