using System.Text;
using System.Text.Json;
using HakchiCli;

var passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action<Fixture> check)
{
    using var fixture = new Fixture();
    check(fixture);
    passed++;
    Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string expected)
{
    try { action(); }
    catch (IOException ex) when (ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new Exception("Expected rejection: " + expected);
}

Test("stock backup is saved, verified, private and reusable", f =>
{
    var record = f.Installer.BackUp(f.Directory);
    Assert(record.Model == "SNES Europe" && record.KernelSha256 == FirmwarePackage.Hash(f.Shell.Kernel), "Backup identity differs");
    Assert(File.ReadAllBytes(f.KernelPath).SequenceEqual(f.Shell.Kernel), "Kernel bytes differ");
    Assert(f.Installer.BackUp(f.Directory) == record, "Same-console retry failed");
    if (OperatingSystem.IsLinux()) Assert((File.GetUnixFileMode(f.KernelPath) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "Backup is not private");
    Assert(!f.Shell.InstallStarted, "Backup wrote installation flags");
});
Test("unknown stock kernels are rejected", f =>
{
    Reject(() => FirmwareInstaller.IdentifyStockKernel(new byte[1024]), "Android");
    var boot = new byte[2048]; Encoding.ASCII.GetBytes("ANDROID!").CopyTo(boot, 0);
    BitConverter.GetBytes(1u).CopyTo(boot, 8); BitConverter.GetBytes(1024u).CopyTo(boot, 36);
    Reject(() => FirmwareInstaller.IdentifyStockKernel(boot), "recognized stock");
});
Test("a running modified system cannot enter the stock installer", f =>
{
    f.Shell.Stock = false;
    Reject(() => f.Installer.BackUp(f.Directory), "not stock");
    Assert(!File.Exists(f.KernelPath) && !f.Shell.InstallStarted, "Rejected console changed state");
});
Test("FEL or a normal boot cannot be mistaken for RAM recovery", f =>
{
    f.Shell.Recovery = false;
    Reject(() => f.Installer.BackUp(f.Directory), "RAM recovery");
    Assert(!File.Exists(f.KernelPath), "Backup written from wrong system");
});
Test("a backup from another console is never overwritten", f =>
{
    Directory.CreateDirectory(f.Directory); File.WriteAllText(f.KernelPath, "another console");
    Reject(() => f.Installer.BackUp(f.Directory), "different kernel");
    Assert(File.ReadAllText(f.KernelPath) == "another console", "Backup overwritten");
});
Test("tampered backup prevents installation", f =>
{
    f.Installer.BackUp(f.Directory); File.AppendAllText(f.KernelPath, "corrupt");
    Reject(() => f.Installer.Install(f.Directory, f.Modules), "verification failed");
    Assert(!f.Shell.InstallStarted, "Corrupt backup started install");
});
Test("firmware change after backup prevents installation", f =>
{
    f.Installer.BackUp(f.Directory);
    var changed = new FirmwareInstaller(f.Shell, new FirmwarePackage(new byte[] { 7 }, new byte[0], new byte[0]), _ => { }, _ => "SNES Europe");
    Reject(() => changed.Install(f.Directory, f.Modules), "verification failed");
    Assert(!f.Shell.InstallStarted, "Changed firmware started install");
});
Test("a different console after backup prevents installation", f =>
{
    f.Installer.BackUp(f.Directory); f.Shell.Kernel = Encoding.UTF8.GetBytes("a different stock kernel");
    Reject(() => f.Installer.Install(f.Directory, f.Modules), "no longer matches");
    Assert(!f.Shell.InstallStarted, "Swapped console started install");
});
Test("failed upload verification stops before install flags and boot", f =>
{
    f.Installer.BackUp(f.Directory); f.Shell.BadUpload = true;
    Reject(() => f.Installer.Install(f.Directory, f.Modules), "checksum mismatch");
    Assert(!f.Shell.InstallStarted && !f.Shell.Commands.Contains("boot"), "Bad upload started install");
});
Test("an identical stock kernel on another unit still prevents installation", f =>
{
    f.Installer.BackUp(f.Directory); f.Shell.ConsoleId = "11111111 22222222 33333333 44444444";
    Reject(() => f.Installer.Install(f.Directory, f.Modules), "identity");
    Assert(!f.Shell.InstallStarted, "Another unit started install");
});
Test("an unreadable hardware identity prevents backup and installation", f =>
{
    f.Shell.ConsoleId = "";
    Reject(() => f.Installer.BackUp(f.Directory), "hardware identity");
    Assert(!File.Exists(f.KernelPath) && !f.Shell.InstallStarted, "Unknown hardware changed state");
});
Test("unfinished transfer requires a fresh recovery", f =>
{
    f.Installer.BackUp(f.Directory); f.Shell.TransferExists = true;
    Reject(() => f.Installer.Install(f.Directory, f.Modules), "unfinished transfer");
    Assert(!f.Shell.InstallStarted, "Unfinished transfer restarted");
});
Test("installation uses upstream RAM-transfer and boot workflow", f =>
{
    f.Installer.BackUp(f.Directory); f.Installer.Install(f.Directory, f.Modules);
    Assert(f.Shell.InstallStarted && f.Shell.Commands.Last() == "boot", "Installation not handed off");
    Assert(f.Shell.Commands.FindIndex(c => c.StartsWith("sha256sum")) < f.Shell.Commands.FindIndex(c => c.Contains("cf_install=y")), "Install flags preceded validation");
    Assert(!f.Shell.Commands.Any(c => c.Contains("sunxi-flash") || c.Contains("sunxi-part") || c.Contains("mod_uninstall")), "Installer used raw flash or repartition commands");
});
Test("flash verification errors remain failures after SSH disconnect", f =>
{
    f.Installer.BackUp(f.Directory); f.Shell.BootExit = 255; f.Shell.BootOutput = "flash md5 mismatch! expected != actual";
    Reject(() => f.Installer.Install(f.Directory, f.Modules), "installation failure");
});
Test("SSH disconnect does not count as verified installation", f =>
{
    f.Installer.BackUp(f.Directory); f.Shell.BootExit = 255; f.Installer.Install(f.Directory, f.Modules);
    Reject(() => f.Installer.Verify(f.Directory), "not ready");
});
Test("final verification checks normal boot and original backup", f =>
{
    f.Installer.BackUp(f.Directory); f.Installer.Install(f.Directory, f.Modules);
    f.Shell.Recovery = false; f.Shell.Installed = true;
    Assert(f.Installer.Verify(f.Directory).Model == "SNES Europe", "Installed console not verified");
    f.Shell.Kernel = Encoding.UTF8.GetBytes("wrong backup");
    Reject(() => f.Installer.Verify(f.Directory), "does not match");
});
Test("verification requires the pinned boot payload in persistent flash", f =>
{
    var expected = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
    var installed = expected.ToArray();
    Array.Clear(installed, 64, 512);
    Assert(FirmwareInstaller.MatchesInstalledBoot(installed, expected), "Expected device-specific command line was rejected");
    installed[1000] ^= 1;
    Assert(!FirmwareInstaller.MatchesInstalledBoot(installed, expected), "Corrupt flash payload was accepted");
    Assert(!FirmwareInstaller.MatchesInstalledBoot(new byte[0], expected), "Missing flash payload was accepted");
});
Test("verification rejects an incorrect U-Boot image in flash", f =>
{
    f.Installer.BackUp(f.Directory); f.Shell.Recovery = false; f.Shell.Installed = true;
    f.Shell.InstalledUboot = new byte[] { 9 };
    Reject(() => f.Installer.Verify(f.Directory), "U-Boot stored in flash");
});
Test("unpinned firmware packages are rejected before USB access", f =>
{
    var path = Path.Combine(f.Directory, "bad.hmod"); Directory.CreateDirectory(f.Directory); File.WriteAllText(path, "untrusted firmware");
    Reject(() => FirmwarePackage.Load(path), "SHA-256");
    Assert(FirmwarePackage.Release.Sha256.Length == 64 && FirmwarePackage.Release.Url.StartsWith("https://github.com/TeamShinkansen/"), "Invalid pinned release");
});
Console.WriteLine($"{passed} installer checks passed. No real console was accessed.");

sealed class Fixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "hakchi installer test " + Guid.NewGuid().ToString("N"));
    public string KernelPath => Path.Combine(Directory, "stock-kernel.img");
    public FakeShell Shell { get; } = new();
    public byte[] Modules { get; } = Encoding.UTF8.GetBytes("base modules fixture");
    public FirmwareInstaller Installer { get; }
    public Fixture() => Installer = new(Shell, new FirmwarePackage(Encoding.UTF8.GetBytes("pinned firmware fixture"), Shell.InstalledBoot, Shell.InstalledUboot),
        _ => { }, _ => "SNES Europe");
    public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
}

sealed class FakeShell : IInstallShell
{
    public byte[] Kernel = Encoding.UTF8.GetBytes("known stock kernel fixture");
    public byte[] InstalledBoot = new byte[2048];
    public byte[] InstalledUboot = new byte[] { 1, 2, 3, 4 };
    public bool Stock = true, Recovery = true, Installed, InstallStarted, BadUpload, TransferExists;
    public int BootExit;
    public string BootOutput = "transferring data...\njumping to switch_root...";
    public string ConsoleId = "01234567 89abcdef 01234567 89abcdef";
    public List<string> Commands = new();
    private byte[] upload;
    public int Run(string command, Stream input = null, Stream output = null, Stream error = null)
    {
        Commands.Add(command);
        if (command == "hakchi hwid") { output.Write(Encoding.ASCII.GetBytes(ConsoleId + "\n")); return 0; }
        if (command.StartsWith("sntool isRecovery")) return Recovery ? 0 : 1;
        if (command == "hakchi isOriginalBoot2") return Stock ? 0 : 1;
        if (command == "hakchi readBoot2") { output.Write(Installed ? InstalledBoot : Kernel); return 0; }
        if (command == "hakchi readBoot1") { output.Write(InstalledUboot); return 0; }
        if (command == "hakchi getBackup2") { output.Write(Kernel); return 0; }
        if (command.StartsWith("command -v sha256sum")) return TransferExists ? 1 : 0;
        if (command.StartsWith("cat >")) { using var data = new MemoryStream(); input.CopyTo(data); upload = data.ToArray(); }
        if (command.StartsWith("sha256sum")) output.Write(Encoding.UTF8.GetBytes((BadUpload ? new string('0', 64) : FirmwarePackage.Hash(upload)) + "  file\n"));
        if (command.Contains("cf_install=y")) InstallStarted = true;
        if (command == "boot") { output.Write(Encoding.UTF8.GetBytes(BootOutput)); return BootExit; }
        if (command.StartsWith("! sntool isRecovery")) return !Recovery && Installed ? 0 : 1;
        return 0;
    }
}
