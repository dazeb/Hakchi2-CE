using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace HakchiDesktop;

public sealed record InstallationIdentity(string Model, string KernelSha256, string FirmwareSha256, string ConsoleId);

public sealed class InstallerWindow : Window
{
    private readonly Backend backend;
    private readonly TextBox host = new() { Text = "169.254.13.37", PlaceholderText = "Recovery console IP / hostname" };
    private readonly TextBox backup = new() { Text = Path.Combine(Preferences.BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]) };
    private readonly TextBlock status = new() { Text = "Start with a stock SNES Mini or Super Famicom. No console storage is written during recovery boot or backup.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox activity = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160 };
    private readonly CheckBox consent = new() { Content = "Install firmware on the verified console using this saved backup", IsEnabled = false };
    private readonly Button boot = new() { Content = "1. Boot recovery" };
    private readonly Button check = new() { Content = "2. Back up and check" };
    private readonly Button install = new() { Content = "3. Install Hakchi", IsEnabled = false, Classes = { "accent" } };
    private readonly Button verify = new() { Content = "4. Verify installation", IsEnabled = false };
    private readonly Action<string> log;
    private InstallationIdentity? identity;
    private bool running, handedOff;
    public string? InstalledHost { get; private set; }

    public InstallerWindow(Backend backend, Action<string> log)
    {
        this.backend = backend; this.log = log;
        Title = "Set up stock SNES Mini";
        Width = 840; Height = 820; MinWidth = 740; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => { if (!running) Close(); };
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children =
        {
            new TextBlock { Text = "Install Hakchi on your stock SNES Mini", FontSize = 24, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "This experimental installer saves and verifies your original kernel before enabling additional games. It supports recognized stock SNES Mini and Super Famicom kernels.", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "1. Switch POWER off and connect a USB data cable to this computer. Hold RESET, switch POWER on, wait about three seconds, then release RESET. The power LED stays off in FEL mode. Click Boot recovery.", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "2. When recovery starts, the console appears as a USB Ethernet adapter. Set that adapter's IPv4 method to Link-Local Only in Linux network settings. The console normally uses 169.254.13.37. Approve its SSH connection once in a terminal: ssh root@169.254.13.37. Then click Back up and check.", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Recovery console address", FontWeight = FontWeight.SemiBold }, host,
            new TextBlock { Text = "Original-kernel backup directory", FontWeight = FontWeight.SemiBold }, backup,
            new TextBlock { Text = "3. Check the detected model and saved backup below. Install Hakchi only when ready. Keep power and USB connected until the menu returns, then verify installation. If SSH reports a changed host key after reboot, confirm the connected console and approve its new key in a terminal before verifying.", TextWrapping = TextWrapping.Wrap },
            status, consent,
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { boot, check, install, verify } },
            new TextBlock { Text = "Setup activity", FontWeight = FontWeight.SemiBold }, activity, close
        } } };
        host.TextChanged += (_, _) => InvalidateCheck();
        backup.TextChanged += (_, _) => InvalidateCheck();
        consent.IsCheckedChanged += (_, _) => UpdateButtons();
        boot.Click += async (_, _) => await RunStep("Boot recovery", async () =>
        {
            InvalidateCheck();
            await Command(new[] { "--timeout", "120", "firmware-boot" });
            status.Text = "Recovery boot sent. Wait for the USB Ethernet adapter, approve the SSH connection, then back up and check.";
        });
        check.Click += async (_, _) => await RunStep("Back up and check", async () =>
        {
            InvalidateCheck();
            var result = await Command(Arguments("firmware-check"));
            identity = JsonSerializer.Deserialize<InstallationIdentity>(result.Output.Trim()) ?? throw new IOException("No verified console identity was returned.");
            status.Text = $"Verified {identity.Model}. Console ID: {identity.ConsoleId}\nOriginal kernel saved in {backup.Text}\nSHA-256: {identity.KernelSha256}";
        });
        install.Click += async (_, _) =>
        {
            if (identity == null || consent.IsChecked != true) return;
            await RunStep("Install Hakchi", async () =>
            {
                await Command(Arguments("firmware-install").Append("--yes"));
                handedOff = true;
                consent.IsChecked = false;
                status.Text = "Installation handoff finished. Keep power connected and wait for the console menu. Click Verify installation to confirm success.";
            });
        };
        verify.Click += async (_, _) => await RunStep("Verify installation", async () =>
        {
            var result = await Command(Arguments("firmware-verify"));
            var verified = JsonSerializer.Deserialize<InstallationIdentity>(result.Output.Trim()) ?? throw new IOException("No installation verification was returned.");
            InstalledHost = host.Text!.Trim();
            status.Text = $"Hakchi installation verified on {verified.Model}. Keep the stock-kernel backup. You can now manage games and compatible emulator modules over SSH.";
        });
        Closing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true;
            status.Text = "An installation operation is running. Keep the console powered and wait for it to finish.";
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape && !running) { Close(); e.Handled = true; }
        };
        UpdateButtons();
    }

    private string[] Arguments(string command)
    {
        if (string.IsNullOrWhiteSpace(host.Text)) throw new ArgumentException("Enter the recovery console address.");
        if (string.IsNullOrWhiteSpace(backup.Text) || !Path.IsPathFullyQualified(backup.Text)) throw new ArgumentException("Choose an absolute backup directory.");
        return new[] { "--host", host.Text.Trim(), "--timeout", "180", command, backup.Text.Trim() };
    }

    private void InvalidateCheck()
    {
        identity = null; consent.IsChecked = false;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        host.IsEnabled = backup.IsEnabled = !running && !handedOff;
        boot.IsEnabled = check.IsEnabled = !running && !handedOff && InstalledHost == null;
        consent.IsEnabled = !running && identity != null && !handedOff;
        install.IsEnabled = !running && identity != null && consent.IsChecked == true && !handedOff;
        verify.IsEnabled = !running && handedOff && InstalledHost == null;
    }

    private async Task<CommandResult> Command(IEnumerable<string> arguments)
    {
        var result = await backend.RunAsync(arguments, line => Dispatcher.UIThread.Post(() =>
        {
            activity.Text = (activity.Text + line + "\n");
            if (activity.Text.Length > 64000) activity.Text = activity.Text[^64000..];
            activity.CaretIndex = activity.Text.Length;
        }));
        log(result.Detail);
        if (!result.Success) throw new IOException(result.Detail.Length > 0 ? result.Detail : $"Backend exited with code {result.ExitCode}.");
        return result;
    }

    private async Task RunStep(string label, Func<Task> action)
    {
        if (running) return;
        running = true; UpdateButtons(); status.Text = label + "…";
        try { await action(); }
        catch (Exception ex)
        {
            status.Text = label + " failed: " + ex.Message;
            log(status.Text);
            // A failed flash must not become an enabled retry or success state.
            if (label == "Install Hakchi")
            {
                handedOff = true;
                status.Text += " Keep the backup and power connected. Verify the console's state before any further installation attempt.";
            }
        }
        finally { running = false; UpdateButtons(); }
    }
}
