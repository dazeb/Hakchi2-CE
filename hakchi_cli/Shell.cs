using System.Diagnostics;
using System.Text;
using com.clusterrr.clovershell;

namespace HakchiCli;

// Keep network authentication, host-key checking and keys in standard OpenSSH.
internal sealed class Shell : IDisposable
{
    private readonly ClovershellConnection usb;
    private readonly string host;
    private readonly int port, timeout;
    private readonly CancellationToken cancellation;

    public Shell(string host, int port, int timeout, CancellationToken cancellation)
    {
        this.host = host;
        this.port = port;
        this.timeout = timeout;
        this.cancellation = cancellation;
        if (host != null) return;
        usb = new ClovershellConnection { AutoReconnect = true };
        usb.Enabled = true;
        var clock = Stopwatch.StartNew();
        try
        {
            while (!usb.IsOnline)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!usb.Enabled || clock.Elapsed.TotalSeconds >= timeout)
                    throw new IOException(usb.LastError ?? "No USB clovershell found. Boot with clovershell installed, or use --host for network/USB Ethernet SSH.");
                Thread.Sleep(50);
            }
        }
        catch { usb.Dispose(); throw; }
    }

    public int Run(string command, Stream input = null, Stream output = null, Stream error = null)
    {
        output ??= Console.OpenStandardOutput();
        error ??= Console.OpenStandardError();
        cancellation.ThrowIfCancellationRequested();
        if (usb != null)
        {
            using var cancel = cancellation.Register(() => usb.Enabled = false);
            return usb.Execute(command, input, output, error, checked(timeout * 1000));
        }
        var start = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-T", "-o", $"ConnectTimeout={timeout}", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2", "-p", port.ToString(), "--", $"root@{host}", command })
            start.ArgumentList.Add(arg);
        if (Environment.GetEnvironmentVariable("HAKCHI_NONINTERACTIVE") == "1")
        {
            start.ArgumentList.Insert(0, "BatchMode=yes");
            start.ArgumentList.Insert(0, "-o");
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start OpenSSH.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var stop = deadline.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(output, deadline.Token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(error, deadline.Token);
        try
        {
            if (input != null) input.CopyToAsync(process.StandardInput.BaseStream, deadline.Token).GetAwaiter().GetResult();
            process.StandardInput.Close();
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            return process.ExitCode;
        }
        catch (Exception) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        { throw new TimeoutException($"Command timed out after {timeout} seconds."); }
    }

    public void Check(string command, Stream input = null, Stream output = null)
    {
        var result = Run(command, input, output);
        if (result != 0) throw new IOException($"Console command failed with exit code {result}.");
    }

    public string Read(string command)
    {
        using var output = new MemoryStream();
        Check(command, output: output);
        return Encoding.UTF8.GetString(output.ToArray()).Trim();
    }

    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public void Dispose() => usb?.Dispose();
}
