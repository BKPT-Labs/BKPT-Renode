//
// Copyright (c) 2026 BKPT Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Extensions;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.PlatformDescription.UserInterface;
using Antmicro.Renode.UI;
using Antmicro.Renode.UserInterface;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.DebugSessions
{
    /// <summary>
    /// A debug session started from the command line: one model from the model catalog, one
    /// firmware image, a GDB server and an optional live memory port, all on 127.0.0.1. Every CPU
    /// is held at its reset entry until a debugger runs it, and one line on stdout says when the
    /// session is ready or why it could not start, so a debugger front end can drive Renode
    /// without the Monitor.
    /// </summary>
    public static class DebugSession
    {
        public const string ReadyPrefix = "Renode ready: ";
        public const string FailurePrefix = "Renode launch failed: ";
        public const string ResetAcknowledgement = "reset (halt)";

        /// <summary>Prints the catalog as a JSON array of {name, description, status}.</summary>
        public static int PrintModels(bool includeIncomplete)
        {
            try
            {
                var models = LoadCatalog().Where(m => includeIncomplete || m.Status == CompleteStatus)
                    .Select(m => new Dictionary<string, string> { ["name"] = m.Name, ["description"] = m.Description, ["status"] = m.Status });
                Console.Out.WriteLine(JsonSerializer.Serialize(models));
                return 0;
            }
            catch(RecoverableException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

        /// <summary>Starts the session; on any failure prints the failure line and exits with status 1.</summary>
        public static void Start(Options options)
        {
            try
            {
                StartOrThrow(options);
            }
            catch(Exception e) when(e is RecoverableException || e is IOException || e is SocketException || e is ArgumentException || e is UnauthorizedAccessException)
            {
                Console.Out.WriteLine(FailurePrefix + OneLine(e.Message));
                Console.Out.Flush();
                Environment.ExitCode = 1;
                Emulator.Exit();
            }
        }

        /// <summary>
        /// Resets the machine and holds every CPU where the reset leaves it, as a hardware reset
        /// would: memory, flash included, keeps its contents. A reset also makes Renode forget the
        /// image's sections, which tell a Cortex-M where its vector table is, so they are read
        /// again from the copy of the image taken at launch and each CPU is initialised from it
        /// as at launch (a no-op on Cortex-M; other architectures take the ELF entry point).
        /// Rebuilding the ELF on disk does not change the session. The copy is parsed before
        /// anything is reset, and live memory requests wait until the reset is complete.
        /// </summary>
        public static void ResetAndHold(IMachine machine)
        {
            if(!images.TryGetValue(machine, out var image))
            {
                throw new RecoverableException("this machine was not started as a debug session");
            }
            using(var elf = ELFUtils.LoadELF(new ReadFilePath(image)))
            {
                if(!busGate.TryEnterWriteLock(ResetGateTimeout))
                {
                    throw new RecoverableException("the session is busy or closing; reset not done");
                }
                try
                {
                    machine.Pause();
                    machine.Reset();
                    machine.SystemBus.LoadSymbolsFrom(elf);
                    foreach(var cpu in machine.SystemBus.GetCPUs())
                    {
                        (cpu as IInitableCPU)?.InitFromElf(elf);
                        cpu.ExecutionMode = ExecutionMode.SingleStep;
                    }
                    machine.Start();
                }
                finally
                {
                    busGate.ExitWriteLock();
                }
            }
        }

        private static void StartOrThrow(Options options)
        {
            var model = LoadCatalog().FirstOrDefault(m => m.Name == options.Model)
                ?? throw new RecoverableException($"unknown model '{options.Model}'; renode --list-models --include-incomplete lists them");
            // Options made the path absolute before the Monitor changed the working directory.
            var elf = options.Elf;
            if(!File.Exists(elf))
            {
                throw new RecoverableException($"ELF not found: {elf}");
            }
            // The session runs this copy: a rebuild of the ELF while the session runs changes
            // neither the firmware nor what a reset restores.
            var image = TemporaryFilesManager.Instance.GetTemporaryFile("image.elf");
            File.Copy(elf, image, overwrite: true);

            // The GDB server must not be reachable from the network, and a port another process
            // already holds on 127.0.0.1 must fail here instead of being shared.
            SocketServerProvider.ListenAddress = IPAddress.Loopback;

            var monitor = ObjectCreator.Instance.GetSurrogate<Antmicro.Renode.UserInterface.Monitor>();
            monitor.Parse("mach create");
            var machine = monitor.Machine as Machine
                ?? throw new RecoverableException("could not create the machine");
            machine.LoadPlatformDescription(model.PlatformPath);
            machine.SystemBus.LoadELF(new ReadFilePath(image));
            images[machine] = image;

            var cpus = machine.SystemBus.GetCPUs().ToList();
            if(!cpus.Any())
            {
                throw new RecoverableException($"model '{model.Name}' has no CPU");
            }
            // Held before the machine starts: starting it loads each CPU's reset values, and a CPU
            // in single-step executes nothing until the debugger continues or steps it.
            foreach(var cpu in cpus)
            {
                cpu.ExecutionMode = ExecutionMode.SingleStep;
            }
            machine.StartGdbServer(options.GdbPort, autostartEmulation: false);
            if(options.MemoryPort > 0)
            {
                var server = new MemoryServer(machine, options.MemoryPort);
                // Before the machines are disposed: no request may touch freed memory.
                Emulator.BeforeDispose += server.Close;
            }
            EmulationManager.Instance.CurrentEmulation.StartAll();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while(cpus.Any(c => c.EmulationState == EmulationCPUState.InReset))
            {
                if(DateTime.UtcNow > deadline)
                {
                    throw new RecoverableException("the CPUs did not leave reset within 10 s");
                }
                Thread.Sleep(10);
            }

            var line = $"{ReadyPrefix}GDB on 127.0.0.1:{options.GdbPort}";
            if(options.MemoryPort > 0)
            {
                line += $", memory on 127.0.0.1:{options.MemoryPort}";
            }
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }

        private static List<Model> LoadCatalog()
        {
            if(!Misc.TryGetRootDirectory(out var root))
            {
                throw new RecoverableException("Renode's root directory was not found");
            }
            var path = Path.Combine(root, "platforms", "models.json");
            if(!File.Exists(path))
            {
                throw new RecoverableException($"model catalog not found: {path}");
            }
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.EnumerateArray().Select(e => new Model
                {
                    Name = e.GetProperty("name").GetString(),
                    Description = e.GetProperty("description").GetString(),
                    Status = e.GetProperty("status").GetString(),
                    PlatformPath = Path.Combine(root, e.GetProperty("platform").GetString().Replace('/', Path.DirectorySeparatorChar)),
                }).ToList();
            }
            catch(Exception e) when(e is JsonException || e is KeyNotFoundException || e is InvalidOperationException)
            {
                throw new RecoverableException($"model catalog {path} is invalid: {e.Message}");
            }
        }

        private static string OneLine(string text)
        {
            return string.Join(" ", (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private const string CompleteStatus = "complete";

        private static readonly TimeSpan ResetGateTimeout = TimeSpan.FromSeconds(5);

        private static readonly ConcurrentDictionary<IMachine, string> images = new ConcurrentDictionary<IMachine, string>();

        // Live memory requests hold it shared, a reset and the shutdown hold it alone.
        private static readonly ReaderWriterLockSlim busGate = new ReaderWriterLockSlim();

        private class Model
        {
            public string Name;
            public string Description;
            public string Status;
            public string PlatformPath;
        }

        /// <summary>
        /// Live memory on 127.0.0.1, one request per line: "r &lt;addr&gt; &lt;len&gt;" answers
        /// "ok &lt;hex&gt;", "w &lt;addr&gt; &lt;hex&gt;" answers "ok", anything else "err &lt;reason&gt;".
        /// Addresses are hexadecimal (an 0x prefix is optional), lengths decimal. Reads and writes
        /// go through the system bus and are answered whether the CPUs run or are halted, without
        /// advancing the emulation; during a reset they wait, and answer "err busy" after a
        /// second. Each client is served on its own thread; requests longer than the largest
        /// write and clients beyond the limit are refused.
        /// </summary>
        private sealed class MemoryServer
        {
            public MemoryServer(IMachine machine, int port)
            {
                this.machine = machine;
                listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Start();
                }
                catch(SocketException e)
                {
                    throw new RecoverableException($"memory port 127.0.0.1:{port}: {e.Message}");
                }
                new Thread(AcceptLoop) { IsBackground = true, Name = "Memory server" }.Start();
            }

            /// <summary>
            /// Stops accepting, disconnects every client and waits for requests in flight; after
            /// it returns no request touches the machine again. A request that does not finish
            /// in time ends the process here: disposing the machine under it is worse.
            /// </summary>
            public void Close()
            {
                lock(clients)
                {
                    if(closed)
                    {
                        return;
                    }
                    closed = true;
                    foreach(var client in clients)
                    {
                        client.Close();
                    }
                }
                listener.Stop();
                // A peripheral or script hook that ends Renode from inside a request or a reset
                // cannot wait for itself.
                if(busGate.IsReadLockHeld || busGate.IsWriteLockHeld)
                {
                    StopWithoutCleanup("exit was requested from inside a live memory request or a reset");
                }
                // Kept for good: the process is exiting, and a request that still waits for the
                // gate gives up with "err busy" on a connection that is already closed.
                if(!busGate.TryEnterWriteLock(CloseGateTimeout))
                {
                    StopWithoutCleanup($"a live memory request did not finish within {CloseGateTimeout.TotalSeconds:0} s");
                }
            }

            private static void StopWithoutCleanup(string reason)
            {
                Console.Error.WriteLine($"Renode: {reason}; stopping without cleanup");
                Console.Error.Flush();
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                Thread.Sleep(Timeout.Infinite);
            }

            private void AcceptLoop()
            {
                while(true)
                {
                    TcpClient client;
                    try
                    {
                        client = listener.AcceptTcpClient();
                    }
                    catch(Exception e) when(e is SocketException || e is ObjectDisposedException || e is InvalidOperationException)
                    {
                        return;
                    }
                    lock(clients)
                    {
                        if(closed || clients.Count >= MaxClients)
                        {
                            Refuse(client, closed ? "err closing" : $"err more than {MaxClients} clients");
                            continue;
                        }
                        clients.Add(client);
                    }
                    new Thread(() => Serve(client)) { IsBackground = true, Name = "Memory client" }.Start();
                }
            }

            private static void Refuse(TcpClient client, string reply)
            {
                try
                {
                    var bytes = Encoding.ASCII.GetBytes(reply + "\n");
                    client.GetStream().Write(bytes, 0, bytes.Length);
                }
                catch(Exception e) when(e is IOException || e is SocketException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    // The client went away first.
                }
                client.Close();
            }

            private void Serve(TcpClient client)
            {
                try
                {
                    var stream = client.GetStream();
                    var line = new byte[MaxLineBytes];
                    var length = 0;
                    var chunk = new byte[4096];
                    int count;
                    while((count = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        for(var i = 0; i < count; i++)
                        {
                            if(chunk[i] == (byte)'\n')
                            {
                                var request = Encoding.ASCII.GetString(line, 0, length).Trim();
                                length = 0;
                                Reply(stream, Handle(request));
                            }
                            else if(length == line.Length)
                            {
                                Reply(stream, $"err request longer than {MaxLineBytes} bytes");
                                return;
                            }
                            else
                            {
                                line[length++] = chunk[i];
                            }
                        }
                    }
                }
                catch(Exception e) when(e is IOException || e is SocketException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    // The client went away, or the server is closing.
                }
                finally
                {
                    lock(clients)
                    {
                        clients.Remove(client);
                    }
                    client.Close();
                }
            }

            private static void Reply(Stream stream, string reply)
            {
                var bytes = Encoding.ASCII.GetBytes(reply + "\n");
                stream.Write(bytes, 0, bytes.Length);
            }

            private string Handle(string request)
            {
                var parts = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if(!busGate.TryEnterReadLock(RequestGateTimeout))
                {
                    return "err busy";
                }
                try
                {
                    if(closed)
                    {
                        return "err closing";
                    }
                    if(parts.Length == 3 && parts[0] == "r")
                    {
                        var address = ParseAddress(parts[1]);
                        if(!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 1 || length > MaxBytes)
                        {
                            return $"err length must be 1..{MaxBytes}";
                        }
                        return "ok " + Convert.ToHexString(machine.SystemBus.ReadBytes(address, length)).ToLowerInvariant();
                    }
                    if(parts.Length == 3 && parts[0] == "w")
                    {
                        var address = ParseAddress(parts[1]);
                        if(parts[2].Length % 2 != 0 || parts[2].Length > 2 * MaxBytes)
                        {
                            return $"err data must be 1..{MaxBytes} bytes of hex";
                        }
                        machine.SystemBus.WriteBytes(Convert.FromHexString(parts[2]), address);
                        return "ok";
                    }
                    return "err unknown request";
                }
                catch(Exception e) when(e is FormatException || e is OverflowException || e is RecoverableException || e is ArgumentException)
                {
                    return "err " + OneLine(e.Message);
                }
                finally
                {
                    busGate.ExitReadLock();
                }
            }

            private static ulong ParseAddress(string text)
            {
                if(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(2);
                }
                return ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            }

            private const int MaxBytes = 4096;
            // "w 0x" + 16 address digits + " " + the largest write, with room to spare.
            private const int MaxLineBytes = 2 * MaxBytes + 64;
            private const int MaxClients = 16;

            private static readonly TimeSpan RequestGateTimeout = TimeSpan.FromSeconds(1);
            private static readonly TimeSpan CloseGateTimeout = TimeSpan.FromSeconds(5);

            private volatile bool closed;
            private readonly IMachine machine;
            private readonly TcpListener listener;
            private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        }
    }

    public static class DebugSessionMachineExtensions
    {
        /// <summary>
        /// Resets the machine and holds every CPU at its reset entry. Answers with the
        /// acknowledgement a debugger checks through `monitor`.
        /// </summary>
        public static string ResetAndHalt(this IMachine machine)
        {
            DebugSession.ResetAndHold(machine);
            return DebugSession.ResetAcknowledgement;
        }
    }
}
