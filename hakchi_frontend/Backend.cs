using System.Diagnostics;
using System.Text.Json;

namespace HakchiDesktop;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
    public string Detail => string.Join("\n", new[] { Output.Trim(), Error.Trim() }.Where(s => s.Length > 0));
}

public sealed class Backend
{
    private readonly string executable;
    public Backend(string? executable = null) => this.executable = executable ??
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "hakchi", "hakchi"));

    public async Task<CommandResult> RunAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // A desktop operation must fail with an actionable error rather than wait
        // for an invisible OpenSSH password or host-key prompt.
        start.Environment["HAKCHI_NONINTERACTIVE"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Could not start the Hakchi backend.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, await output, await error);
    }
}

public sealed record GameEntry(string Code, string Name, string Command, string Directory)
{
    public string Core => Path.GetFileName(Command.Split(' ', 2)[0]);
    public static IReadOnlyList<GameEntry> Parse(string output, string library) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r').Split('\t', 3))
        .Where(fields => fields.Length == 3)
        .Select(fields => new GameEntry(fields[0], fields[1], fields[2], Path.Combine(library, fields[0])))
        .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
}

public sealed class Preferences
{
    public string Library { get; set; } = Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "hakchi", "games");
    public bool UseSsh { get; set; }
    public string Host { get; set; } = "";
    public string Port { get; set; } = "22";
    public string Remote { get; set; } = "/var/lib/hakchi/games";
    public string Core { get; set; } = "fceumm";

    public static string FilePath => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "hakchi", "preferences.json");
    private static string Xdg(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return value != null && Path.IsPathFullyQualified(value) ? value :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
    }

    public static Preferences Load(string? path = null)
    {
        path ??= FilePath;
        return File.Exists(path) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new() : new();
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(stage, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(stage, path, true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
