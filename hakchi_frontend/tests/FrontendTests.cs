using System.Text.Json;
using HakchiDesktop;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("These checks run on Linux.");
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
var root = Path.Combine(Path.GetTempPath(), "hakchi frontend " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var helper = Path.Combine(root, "backend with spaces");
    File.WriteAllText(helper, "#!/usr/bin/env python3\nimport json, os, sys\nprint(json.dumps(sys.argv[1:]))\nprint(os.environ.get('HAKCHI_NONINTERACTIVE'), file=sys.stderr)\nsys.exit(7)\n");
    File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var arguments = new[] { "game-add", "game's name;$(touch PWNED).nes", root, "--name", "Test with spaces" };
    var result = await new Backend(helper).RunAsync(arguments);
    Assert(result.ExitCode == 7 && !result.Success, "Backend exit status was lost");
    Assert(JsonSerializer.Deserialize<string[]>(result.Output)!.SequenceEqual(arguments), "Arguments were shell-expanded or split");
    Assert(result.Error.Trim() == "1", "Desktop SSH noninteractive mode was not set");
    Assert(!File.Exists(Path.Combine(root, "PWNED")), "An argument was executed by a shell");
    Console.WriteLine("PASS: backend arguments, streams, exit status and desktop SSH mode");

    var games = GameEntry.Parse("CLV-Z-BBBBB\tZelda\t/bin/fceumm /var/games/b/game.nes\nCLV-Z-AAAAA\tDonkey Kong\t/bin/snes9x /var/games/a/game.sfc\n", root);
    Assert(games.Count == 2 && games[0].Name == "Donkey Kong", "Library parsing or sorting failed");
    Assert(games[1].Core == "fceumm" && games[0].Directory == Path.Combine(root, "CLV-Z-AAAAA"), "Game details lost their source path");
    Assert(GameEntry.Parse("", root).Count == 0, "Empty library was not handled");
    Console.WriteLine("PASS: game-list parsing, sorting, details and empty library");

    var settingsPath = Path.Combine(root, "config", "preferences.json");
    var settings = new Preferences { Library = root, UseSsh = true, Host = "192.0.2.10", Port = "2222", Core = "snes9x" };
    settings.Save(settingsPath);
    settings.Host = "classic.local";
    settings.Save(settingsPath);
    var loaded = Preferences.Load(settingsPath);
    Assert(loaded.Library == root && loaded.UseSsh && loaded.Host == "classic.local" && loaded.Port == "2222", "Settings did not round-trip atomically");
    Assert(Directory.GetFiles(Path.GetDirectoryName(settingsPath)!, "*.tmp").Length == 0, "Temporary settings were left behind");
    var contents = File.ReadAllText(settingsPath);
    Assert(!contents.Contains("Password") && !contents.Contains("PrivateKey"), "Credentials appeared in preferences");
    Console.WriteLine("PASS: settings persistence, replacement and credential-free content");
}
finally { Directory.Delete(root, true); }
