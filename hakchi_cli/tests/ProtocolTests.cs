using System.Reflection;
using com.clusterrr.clovershell;

// Exercise framing and streamed responses in the real shared USB implementation.
// No device is opened; wire packets are delivered directly to its receive parser.
var flags = BindingFlags.Instance | BindingFlags.NonPublic;
using var connection = new ClovershellConnection();
var type = typeof(ClovershellConnection);
var receive = type.GetMethod("ReceiveBytes", flags)!;
void Send(byte[] bytes) => receive.Invoke(connection, new object[] { bytes, bytes.Length });
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
byte[] Packet(byte command, byte id, byte[] data) => new[] { command, id, (byte)data.Length, (byte)(data.Length >> 8) }.Concat(data).ToArray();

var pong = Packet(1, 0, new byte[] { 1, 2, 3, 4 });
Send(pong[..2]);
Assert(type.GetField("lastPingResponse", flags)!.GetValue(connection) == null, "Partial header was dispatched");
Send(pong[2..5]);
Assert(type.GetField("lastPingResponse", flags)!.GetValue(connection) == null, "Partial payload was dispatched");
Send(pong[5..]);
Assert(((byte[])type.GetField("lastPingResponse", flags)!.GetValue(connection)!).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Split pong was corrupted");

var execType = type.Assembly.GetType("com.clusterrr.clovershell.ExecConnection")!;
using var stdout = new MemoryStream();
using var stderr = new MemoryStream();
using var exec = (IDisposable)Activator.CreateInstance(execType, connection, "fixture", null, stdout, stderr)!;
var sessions = (Array)type.GetField("execConnections", flags)!.GetValue(connection)!;
sessions.SetValue(exec, 7);
var output = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
var packets = Packet(13, 7, output)
    .Concat(Packet(14, 7, new byte[] { 69, 82, 82 }))
    .Concat(Packet(13, 7, Array.Empty<byte>()))
    .Concat(Packet(14, 7, Array.Empty<byte>()))
    .Concat(Packet(15, 7, new byte[] { 17 })).ToArray();
for (int pos = 0; pos < packets.Length; pos += 3) Send(packets[pos..Math.Min(pos + 3, packets.Length)]);
Assert(stdout.ToArray().SequenceEqual(output), "Binary stdout corrupted");
Assert(stderr.ToArray().SequenceEqual(new byte[] { 69, 82, 82 }), "stderr corrupted");
Assert((bool)execType.GetField("finished", flags)!.GetValue(exec)!, "Exit result not dispatched");
Assert((int)execType.GetField("result", flags)!.GetValue(exec)! == 17, "Exit status lost");
Assert((bool)execType.GetField("stdoutFinished", flags)!.GetValue(exec)!, "stdout EOF lost");
Assert((bool)execType.GetField("stderrFinished", flags)!.GetValue(exec)!, "stderr EOF lost");
Assert(((byte[])type.GetField("pendingData", flags)!.GetValue(connection)!).Length == 0, "Frames left buffered");
Send(Packet(1, 0, new byte[] { 5 }).Concat(Packet(1, 0, new byte[] { 6 })).ToArray());
Assert(((byte[])type.GetField("lastPingResponse", flags)!.GetValue(connection)!).SequenceEqual(new byte[] { 6 }), "Combined frames not dispatched");
Console.WriteLine("USB protocol checks passed: fragmented/combined frames, binary streams, EOF, exit status, disposal.");
