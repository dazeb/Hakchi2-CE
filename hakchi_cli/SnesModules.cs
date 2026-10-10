using System.Security.Cryptography;
using System.Text.Json;

namespace HakchiCli;

internal sealed record BundledModule(string Name, string File, string Sha256);
internal sealed record ModuleManifest(string Repository, string Commit, BundledModule[] Modules);

internal static class SnesModules
{
    private static readonly ModuleManifest Manifest = JsonSerializer.Deserialize<ModuleManifest>(
        typeof(SnesModules).Assembly.GetManifestResourceStream("HakchiCli.modules.json"),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    // Check the entire set before opening a connection or installing either module.
    public static string[] Validate()
    {
        return Manifest.Modules.Select(module =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "payloads", "snes-modules", module.File);
            if (!System.IO.File.Exists(path))
                throw new IOException($"Bundled {module.Name} is missing. Rebuild or download the SNES desktop AppImage.");
            using var input = System.IO.File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(input)) != module.Sha256)
                throw new IOException($"Bundled {module.Name} failed its SHA-256 check. Nothing was sent to the console.");
            return path;
        }).ToArray();
    }

    public static void Verify(Shell shell)
    {
        shell.Check("test -x /bin/retroarch && test -x /bin/retroarch-clover && " +
            "test -x /bin/snes9x && test -s /etc/libretro/core/km_snes9x_libretro.so");
        Console.WriteLine("RetroArch and Snes9x installed; launcher and core files verified. " +
            "Import .sfc/.smc games with --core snes9x, then sync and test a game on your console.");
    }
}
