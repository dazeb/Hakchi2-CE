using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using com.clusterrr.hakchi_gui;

namespace HakchiCli;

internal static class Games
{
    public static void Add(string rom, string library, string core, string name, string icon)
    {
        if (!Regex.IsMatch(core, @"^[a-zA-Z0-9_-]+$")) throw new ArgumentException("--core must be an installed emulator command, e.g. fceumm or snes9x.");
        var extension = Path.GetExtension(rom).ToLowerInvariant();
        if (!Regex.IsMatch(extension, @"^\.[a-z0-9]+$")) throw new ArgumentException("ROM needs a file extension.");
        using var input = File.OpenRead(rom);
        if (input.Length == 0) throw new ArgumentException("ROM is empty.");
        var hash = SHA256.HashData(input);
        var code = "CLV-Z-" + new string(hash.Take(5).Select(b => (char)('A' + b % 26)).ToArray());
        var path = Path.Combine(library, code);
        if (Directory.Exists(path)) throw new IOException($"Game already exists: {path}. Edit its .desktop file to change settings.");
        name ??= Path.GetFileNameWithoutExtension(rom);
        if (name.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("Game name must be one line.");
        icon ??= Path.Combine(AppContext.BaseDirectory, "payloads", "blank_app.png");
        using (var png = File.OpenRead(icon))
        {
            var header = new byte[8];
            if (png.Read(header) != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new ArgumentException("--icon must be a PNG. Supply console-sized art; the CLI does not resize it.");
        }
        Directory.CreateDirectory(library);
        var stage = Path.Combine(library, ".add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            File.Copy(rom, Path.Combine(stage, "game" + extension));
            File.Copy(icon, Path.Combine(stage, code + ".png"));
            File.Copy(icon, Path.Combine(stage, code + "_small.png"));
            new DesktopFile
            {
                Code = code, Name = name, SortName = name.ToLowerInvariant(),
                Exec = $"/bin/{CoreCommands.GetCommand(core)} /var/games/{code}/game{extension}",
                ProfilePath = "/var/saves", IconPath = "/var/games", IconFilename = code + ".png",
                Players = 1, SaveCount = 4, Status = "Completing"
            }.SaveTo(Path.Combine(stage, code + ".desktop"), snesExtraFields: true);
            Directory.Move(stage, path);
            Console.WriteLine(path);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static string[] Validate(string library)
    {
        if (!Directory.Exists(library)) throw new DirectoryNotFoundException(library);
        var directories = Directory.GetDirectories(library, "CLV-*").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (directories.Length == 0) throw new ArgumentException("No CLV-* game directories found. Use game-add or a flat hakchi game export.");
        foreach (var directory in directories)
        {
            var code = Path.GetFileName(directory);
            if (!Regex.IsMatch(code, @"^CLV-[A-Z]-[A-Z]{5}$")) throw new ArgumentException($"Invalid game code: {code}");
            // Do not traverse symlinks into unrelated host files when packaging games.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException($"Symlinks are not supported: {directory}");
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException($"Symlinks are not supported: {entry}");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            var desktop = new DesktopFile();
            desktop.Load(Path.Combine(directory, code + ".desktop"));
            if (desktop.Code != code || string.IsNullOrWhiteSpace(desktop.Exec)) throw new ArgumentException($"Invalid desktop entry: {directory}");
        }
        return directories;
    }

    public static void Sync(Shell shell, string[] directories, string remote)
    {
        // Only merge the named games into menu 000. Never prune existing games/saves.
        var stage = remote + "/.hakchi-cli-" + Guid.NewGuid().ToString("N");
        var archive = Path.GetTempFileName();
        var keepStage = false;
        try
        {
            using (var file = File.Create(archive))
            using (var writer = new TarWriter(file, TarEntryFormat.Gnu))
                foreach (var directory in directories)
                {
                    var root = Path.GetDirectoryName(directory);
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Prepend(directory))
                        writer.WriteEntry(path, Path.GetRelativePath(root, path));
                }
            shell.Check($"mkdir -p {Shell.Quote(stage)}");
            using (var input = File.OpenRead(archive)) shell.Check($"tar -x -C {Shell.Quote(stage)}", input);
            // Extraction must succeed before the menu is touched; each replacement can roll back.
            shell.Check("uistop");
            try
            {
                shell.Check("hakchi eval 'umount \"$gamepath\"' || :");
                keepStage = true;
                shell.Check($"mkdir -p {Shell.Quote(remote + "/000")}");
                foreach (var directory in directories)
                {
                    var code = Path.GetFileName(directory);
                    var target = Shell.Quote(remote + "/000/" + code);
                    var backup = Shell.Quote(stage + "/previous-" + code);
                    shell.Check($"if [ -e {target} ]; then mv {target} {backup} || exit; fi; " +
                        $"if mv {Shell.Quote(stage + "/" + code)} {target}; then :; else [ ! -e {backup} ] || mv {backup} {target}; exit 1; fi");
                }
                shell.Check("sync");
            }
            finally { shell.Check("hakchi overmount_games && uistart"); }
            keepStage = false;
            Console.Error.WriteLine($"Uploaded {directories.Length} games to {remote}/000.");
        }
        finally
        {
            File.Delete(archive);
            if (!keepStage)
                try { shell.Check($"rm -rf {Shell.Quote(stage)}"); } catch (Exception) { }
            else Console.Error.WriteLine($"Transfer interrupted; previous game copies kept in {stage}.");
        }
    }
}
