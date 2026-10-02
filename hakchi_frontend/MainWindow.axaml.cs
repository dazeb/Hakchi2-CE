using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

namespace HakchiDesktop;

public partial class MainWindow : Window
{
    private readonly Backend backend = new();
    private Preferences preferences = new();
    private IReadOnlyList<GameEntry> games = Array.Empty<GameEntry>();
    private Bitmap? artwork;
    private bool busy;
    private string? startupError;

    public MainWindow()
    {
        InitializeComponent();
        using var icon = AssetLoader.Open(new Uri("avares://hakchi-desktop/Assets/hakchi.png"));
        Icon = new WindowIcon(icon);
        try { preferences = Preferences.Load(); }
        catch (Exception ex) { startupError = "Could not read saved settings: " + ex.Message; }
        LibraryPath.Text = preferences.Library;
        ConnectionType.SelectedIndex = preferences.UseSsh ? 1 : 0;
        HostBox.Text = preferences.Host; PortBox.Text = preferences.Port; RemotePath.Text = preferences.Remote;
        ConnectionType.SelectionChanged += (_, _) => UpdateConnectionFields();
        UpdateConnectionFields();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F5) { RefreshClick(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F) { SearchBox.Focus(); e.Handled = true; }
            else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.L) { LibraryPath.Focus(); e.Handled = true; }
            else if (!busy && e.KeyModifiers == KeyModifiers.Control && e.Key == Key.O) { ImportClick(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.C)
            { CopyClick(this, new RoutedEventArgs()); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            Log("Hakchi Desktop", "Prepare games locally, then check your console connection before syncing.");
            if (startupError != null) Log("Settings", startupError);
            await BusyAsync("Loading library", ReloadAsync);
        };
        Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            StatusText.Text = "An operation is running. Wait for it to finish before closing.";
        };
        Closed += (_, _) => artwork?.Dispose();
    }

    private void UpdateConnectionFields() => HostBox.IsEnabled = PortBox.IsEnabled = ConnectionType.SelectedIndex == 1;

    private void SaveSettings()
    {
        var library = LibraryPath.Text?.Trim();
        if (string.IsNullOrWhiteSpace(library) || !Path.IsPathFullyQualified(library))
            throw new ArgumentException("Choose an absolute local library directory.");
        preferences.Library = Path.GetFullPath(library);
        preferences.UseSsh = ConnectionType.SelectedIndex == 1;
        preferences.Host = HostBox.Text?.Trim() ?? "";
        preferences.Port = PortBox.Text?.Trim() ?? "22";
        preferences.Remote = RemotePath.Text?.Trim() ?? "";
        preferences.Save();
    }

    private string[] ConnectionArguments()
    {
        if (!preferences.UseSsh) return new[] { "--timeout", "30" };
        if (string.IsNullOrWhiteSpace(preferences.Host)) throw new ArgumentException("Enter your console's IP address or hostname.");
        return new[] { "--host", preferences.Host, "--port", preferences.Port, "--timeout", "30" };
    }

    private async Task BusyAsync(string label, Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        ImportButton.IsEnabled = RefreshButton.IsEnabled = LibraryButton.IsEnabled = LibraryPath.IsEnabled = false;
        SyncButton.IsEnabled = false;
        ConnectionPanel.IsEnabled = false;
        OperationProgress.IsVisible = true;
        StatusText.Text = label + "…";
        try
        {
            SaveSettings();
            await operation();
            StatusText.Text = "Ready · " + label + " finished";
        }
        catch (Exception ex)
        {
            Log(label + " failed", ex.Message);
            StatusText.Text = label + " failed. See activity for details.";
        }
        finally
        {
            busy = false;
            ImportButton.IsEnabled = RefreshButton.IsEnabled = LibraryButton.IsEnabled = LibraryPath.IsEnabled = true;
            ConnectionPanel.IsEnabled = true;
            SyncButton.IsEnabled = games.Count > 0;
            OperationProgress.IsVisible = false;
        }
    }

    private async Task<CommandResult> ExecuteAsync(string label, IEnumerable<string> arguments, bool allowNoDevice = false)
    {
        var result = await backend.RunAsync(arguments);
        Log(label, result.Detail.Length == 0 ? (result.Success ? "Completed." : $"Exit code {result.ExitCode}.") : result.Detail);
        if (!result.Success && !(allowNoDevice && result.ExitCode == 3))
            throw new IOException($"{label}: {result.Detail} (exit {result.ExitCode})");
        return result;
    }

    private async Task ReloadAsync()
    {
        games = Array.Empty<GameEntry>();
        FilterGames();
        if (Directory.Exists(preferences.Library))
        {
            var result = await ExecuteAsync("Read library", new[] { "game-list", preferences.Library });
            games = GameEntry.Parse(result.Output, preferences.Library);
        }
        FilterGames();
    }

    private void FilterGames()
    {
        if (GameList == null) return;
        var selected = (GameList.SelectedItem as GameEntry)?.Code;
        var search = SearchBox.Text?.Trim() ?? "";
        var filtered = games.Where(g => (g.Name + " " + g.Core + " " + g.Code).Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        GameList.ItemsSource = filtered;
        GameList.SelectedItem = filtered.FirstOrDefault(g => g.Code == selected) ?? filtered.FirstOrDefault();
        EmptyState.IsVisible = filtered.Length == 0;
        EmptyTitle.Text = games.Count == 0 ? "Build your Classic library" : "No matching games";
        EmptyDescription.Text = games.Count == 0 ? "Add ROMs and choose their emulator. You can prepare games before connecting your console." : "Try another game name, emulator command or game ID.";
        LibraryCount.Text = search.Length == 0 ? $"{games.Count} games · Local library" : $"{filtered.Length} of {games.Count} games";
        SyncButton.IsEnabled = !busy && games.Count > 0;
        ShowSelection();
    }

    private void ShowSelection()
    {
        var game = GameList.SelectedItem as GameEntry;
        GameName.Text = game?.Name ?? "Select a game";
        GameDetails.Text = game == null ? "Preview its artwork and emulator command here." : game.Code + "\n" + game.Command;
        FolderButton.IsEnabled = game != null;
        GameArt.Source = null;
        artwork?.Dispose(); artwork = null;
        if (game == null) return;
        try
        {
            var path = Path.Combine(game.Directory, game.Code + ".png");
            if (File.Exists(path)) GameArt.Source = artwork = new Bitmap(path);
        }
        catch (Exception ex) { Log("Artwork", "Could not read game artwork: " + ex.Message); }
    }

    private void Log(string label, string text)
    {
        var activity = (Activity.Text ?? "") + $"[{DateTime.Now:HH:mm:ss}] {label}\n{text}\n\n";
        Activity.Text = activity.Length > 64000 ? activity[^64000..] : activity;
        Activity.CaretIndex = Activity.Text.Length;
    }

    private async void RefreshClick(object? sender, RoutedEventArgs e) => await BusyAsync("Refresh library", ReloadAsync);
    private void SearchChanged(object? sender, TextChangedEventArgs e) => FilterGames();
    private void GameSelected(object? sender, SelectionChangedEventArgs e) => ShowSelection();
    private async void LibraryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose game library", AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            LibraryPath.Text = path;
            await BusyAsync("Load library", ReloadAsync);
        }
        catch (Exception ex) { Log("Choose library", ex.Message); }
    }

    private async void ImportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add game ROMs", AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Game ROMs") { Patterns = new[] { "*.nes", "*.sfc", "*.smc", "*.gb", "*.gbc", "*.gba", "*.bin", "*.md", "*.sms", "*.gg", "*.n64", "*.z64" } }, FilePickerFileTypes.All }
            });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToArray();
            if (paths.Length == 0) return;
            var core = new TextBox { Text = preferences.Core, PlaceholderText = "Installed emulator command" };
            var name = new TextBox { Text = Path.GetFileNameWithoutExtension(paths[0]), IsEnabled = paths.Length == 1 };
            Avalonia.Automation.AutomationProperties.SetName(core, "Emulator command");
            Avalonia.Automation.AutomationProperties.SetName(name, "Game name");
            var body = new StackPanel { Spacing = 10, Children =
            {
                new TextBlock { Text = $"{paths.Length} ROM file(s) selected", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Emulator command", Classes = { "secondary" } }, core,
                new TextBlock { Text = "Game name (multiple games use their filenames)", Classes = { "secondary" } }, name,
                new TextBlock { Text = "Use a core installed on your console, such as fceumm or snes9x. The default artwork is added automatically.", TextWrapping = TextWrapping.Wrap, Classes = { "secondary" } }
            } };
            if (!await PromptAsync("Add games", body, "Add to library")) return;
            preferences.Core = core.Text?.Trim() ?? "";
            await BusyAsync("Import games", async () =>
            {
                try
                {
                    foreach (var path in paths)
                    {
                        var args = new List<string> { "game-add", path, preferences.Library, "--core", preferences.Core };
                        if (paths.Length == 1) args.AddRange(new[] { "--name", name.Text ?? "" });
                        await ExecuteAsync("Import " + Path.GetFileName(path), args);
                    }
                }
                finally { await ReloadAsync(); }
            });
        }
        catch (Exception ex) { Log("Import games", ex.Message); }
    }

    private async void StatusClick(object? sender, RoutedEventArgs e) => await BusyAsync("Console status", async () =>
        await ExecuteAsync("Console status", ConnectionArguments().Append("status")));
    private async void DevicesClick(object? sender, RoutedEventArgs e) => await BusyAsync("USB discovery", async () =>
    {
        var result = await ExecuteAsync("USB devices", new[] { "devices" }, true);
        if (result.ExitCode == 3) Log("USB discovery", "No FEL/clovershell Classic found. Check the data cable, console mode and udev permissions. SSH connections do not require this USB ID.");
    });

    private async void SyncClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettings();
            var destination = preferences.Remote;
            var library = preferences.Library;
            var connection = preferences.UseSsh ? preferences.Host : "USB clovershell";
            if (!await PromptAsync("Synchronize library", new TextBlock
            {
                Text = $"Send all {games.Count} games from {library} to {connection}, in {destination}/000?\n\nExisting copies of these game IDs are replaced. Other games and saves are preserved. Confirm this is the console's actual writable game storage path.",
                TextWrapping = TextWrapping.Wrap
            }, "Synchronize")) return;
            await BusyAsync("Synchronize library", async () =>
                await ExecuteAsync("Synchronize library", ConnectionArguments().Concat(new[] { "sync", library, destination })));
        }
        catch (Exception ex) { Log("Synchronize library", ex.Message); }
    }

    private async void ModuleClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose console module", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Hakchi module") { Patterns = new[] { "*.hmod" } } }
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            if (!await PromptAsync("Install module", new TextBlock { Text = $"Install {Path.GetFileName(path)} on your console? Module scripts change console firmware. Use a compatible module from a trusted source.", TextWrapping = TextWrapping.Wrap }, "Install module")) return;
            await BusyAsync("Install module", async () => await ExecuteAsync("Install module", ConnectionArguments().Concat(new[] { "mod-install", path })));
        }
        catch (Exception ex) { Log("Install module", ex.Message); }
    }

    private async void BackupClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save existing stock-kernel backup", SuggestedFileName = "stock-kernel.img", ShowOverwritePrompt = true });
            if (file?.TryGetLocalPath() is not { } path) return;
            await BusyAsync("Save stored backup", async () => await ExecuteAsync("Save stored backup", ConnectionArguments().Concat(new[] { "backup", path })));
        }
        catch (Exception ex) { Log("Save backup", ex.Message); }
    }

    private async Task<bool> PromptAsync(string title, Control body, string action)
    {
        var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "Cancel" };
        var accept = new Button { Content = action, Classes = { "accent" } };
        cancel.Click += (_, _) => dialog.Close(false);
        accept.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children =
        {
            new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold }, body,
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } }
        } };
        return await dialog.ShowDialog<bool>(this);
    }

    private async void FolderClick(object? sender, RoutedEventArgs e)
    {
        try { if (GameList.SelectedItem is GameEntry game) await Launcher.LaunchUriAsync(new Uri(game.Directory + "/")); }
        catch (Exception ex) { Log("Open game folder", ex.Message); }
    }

    private async void GuideClick(object? sender, RoutedEventArgs e)
    {
        try { await Launcher.LaunchUriAsync(new Uri("https://github.com/dazeb/Hakchi2-CE/blob/mainline/hakchi_frontend/README.md")); }
        catch (Exception ex) { Log("Setup guide", ex.Message); }
    }

    private async void CopyClick(object? sender, RoutedEventArgs e)
    {
        try { if (Clipboard != null) await Clipboard.SetTextAsync(Activity.Text ?? ""); }
        catch (Exception ex) { Log("Copy activity", ex.Message); }
    }
}
