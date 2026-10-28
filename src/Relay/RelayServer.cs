using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace Relay
{
    // Pairs one host and one viewer by session code, then copies bytes both ways.
    // The tunnel is opaque: the host/viewer TLS session runs end to end through it.
    public sealed class RelayServer : IDisposable
    {
        private const int MaxHosts = 128;
        private const int HandshakeSeconds = 10;
        private const int ThrottleAttempts = 10;
        private const int ThrottleSeconds = 60;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _gate = new object();
        private readonly Dictionary<string, Pending> _hosts = new Dictionary<string, Pending>();
        private readonly Dictionary<string, Throttle> _throttle = new Dictionary<string, Throttle>();
        private Task _acceptTask;

        public RelayServer(string bindAddress, int port)
        {
            BindAddress = bindAddress;
            Port = port;
            _listener = new TcpListener(IPAddress.Parse(bindAddress), port);
        }

        public string BindAddress { get; private set; }
        public int Port { get; private set; }

        public event Action<string> Logged;

        public void Start()
        {
            _listener.Start();
            _acceptTask = Task.Run(new Func<Task>(AcceptLoopAsync));
            RaiseLog("Listening on " + BindAddress + ":" + Port + ".");
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { _listener.Stop(); } catch (ObjectDisposedException) { }

            List<Pending> hosts;
            lock (_gate)
            {
                hosts = new List<Pending>(_hosts.Values);
                _hosts.Clear();
                _throttle.Clear();
            }

            foreach (Pending host in hosts) host.Close();
            RaiseLog("Relay stopped.");
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            CancellationToken ct = _cts.Token;

            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (SocketException) { break; }

                TcpClient accepted = client;
#pragma warning disable 4014
                Task.Run(async delegate
                {
                    try
                    {
                        await HandleConnectionAsync(accepted, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        RaiseLog("Connection error: " + ex.Message);
                        try { accepted.Close(); } catch (Exception) { }
                    }
                });
#pragma warning restore 4014
            }
        }

        private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                client.NoDelay = true;
                string remote = client.Client.RemoteEndPoint == null
                    ? "unknown"
                    : client.Client.RemoteEndPoint.ToString();
                Stream raw = client.GetStream();

                string line;
                using (CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(HandshakeSeconds));
                    line = await LineProtocol.ReadLineAsync(raw, handshake.Token).ConfigureAwait(false);
                }

                if (line == null) return;

                string verb = null;
                string code = null;
                int space = line.IndexOf(' ');
                if (space > 0)
                {
                    verb = line.Substring(0, space).Trim();
                    code = line.Substring(space + 1).Trim();
                }

                if (verb == null || !SessionCodes.Valid(code))
                {
                    await LineProtocol.WriteLineAsync(raw, "ERR MALFORMED", CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(verb, "HOST", StringComparison.OrdinalIgnoreCase))
                    await HandleHostAsync(client, raw, remote, code, ct).ConfigureAwait(false);
                else if (string.Equals(verb, "VIEW", StringComparison.OrdinalIgnoreCase))
                    await HandleViewerAsync(client, raw, remote, code, ct).ConfigureAwait(false);
                else
                    await LineProtocol.WriteLineAsync(raw, "ERR MALFORMED", CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task HandleHostAsync(TcpClient host, Stream stream, string remote, string code, CancellationToken ct)
        {
            Pending pending = new Pending(host, stream, remote);

            string failure = null;
            lock (_gate)
            {
                if (_hosts.Count >= MaxHosts) failure = "ERR LIMIT";
                else if (_hosts.ContainsKey(code)) failure = "ERR INUSE";
                else _hosts.Add(code, pending);
            }

            if (failure != null)
            {
                await LineProtocol.WriteLineAsync(stream, failure, CancellationToken.None).ConfigureAwait(false);
                RaiseLog("Rejected host " + remote + " for code " + code + ": " + failure + ".");
                return;
            }

            await LineProtocol.WriteLineAsync(stream, "OK", ct).ConfigureAwait(false);
            RaiseLog("Host " + remote + " registered code " + code + ".");

            try
            {
                while (true)
                {
                    Task completed = await Task.WhenAny(pending.Paired.Task, Task.Delay(1000, ct)).ConfigureAwait(false);
                    if (completed == pending.Paired.Task) break;

                    ct.ThrowIfCancellationRequested();
                    if (IsClosed(host))
                    {
                        RaiseLog("Host for code " + code + " disconnected while waiting.");
                        return;
                    }
                }

                await pending.Paired.Task.ConfigureAwait(false);
                await pending.Done.Task.ConfigureAwait(false);
            }
            finally
            {
                RemoveHost(code, pending);
                pending.Close();
            }
        }

        private async Task HandleViewerAsync(TcpClient viewer, Stream stream, string remote, string code, CancellationToken ct)
        {
            if (Throttled(remote))
            {
                RaiseLog("Throttled viewer " + remote + " for code " + code + ".");
                await LineProtocol.WriteLineAsync(stream, "ERR THROTTLE", CancellationToken.None).ConfigureAwait(false);
                return;
            }

            Pending host = null;
            string failure = null;
            lock (_gate)
            {
                Pending candidate;
                if (!_hosts.TryGetValue(code, out candidate)) failure = "ERR NOHOST";
                else if (candidate.Claimed) failure = "ERR BUSY";
                else
                {
                    candidate.Claimed = true;
                    host = candidate;
                }
            }

            if (host == null)
            {
                if (string.Equals(failure, "ERR NOHOST", StringComparison.Ordinal))
                {
                    RecordFailure(remote);
                    RaiseLog("Viewer " + remote + " tried unknown code " + code + ".");
                }

                await LineProtocol.WriteLineAsync(stream, failure, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            try
            {
                ClearFailures(remote);

                await LineProtocol.WriteLineAsync(stream, "OK", ct).ConfigureAwait(false);
                await LineProtocol.WriteLineAsync(host.Stream, "PAIRED " + remote, ct).ConfigureAwait(false);
                RaiseLog("Paired code " + code + ": host " + host.Remote + " <-> viewer " + remote + ".");
                host.Paired.TrySetResult(true);

                await PipeAsync(host.Client, viewer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                RaiseLog("Relay session error: " + ex.Message);
            }
            finally
            {
                host.Done.TrySetResult(true);
                RemoveHost(code, host);
            }
        }

        private static async Task PipeAsync(TcpClient a, TcpClient b, CancellationToken ct)
        {
            Stream aStream = a.GetStream();
            Stream bStream = b.GetStream();

            Task up = CopyAsync(aStream, bStream, ct);
            Task down = CopyAsync(bStream, aStream, ct);

            await Task.WhenAny(up, down).ConfigureAwait(false);

            try { a.Close(); } catch (Exception) { }
            try { b.Close(); } catch (Exception) { }

            await Swallow(up).ConfigureAwait(false);
            await Swallow(down).ConfigureAwait(false);
        }

        private static async Task CopyAsync(Stream from, Stream to, CancellationToken ct)
        {
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                int read = await from.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (read <= 0) return;
                await to.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
            }
        }

        private static async Task Swallow(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { }
        }

        private void RemoveHost(string code, Pending pending)
        {
            lock (_gate)
            {
                Pending current;
                if (_hosts.TryGetValue(code, out current) && ReferenceEquals(current, pending))
                    _hosts.Remove(code);
            }
        }

        private static bool IsClosed(TcpClient client)
        {
            try
            {
                return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private bool Throttled(string remote)
        {
            string ip = IpOf(remote);
            lock (_gate)
            {
                Throttle entry;
                if (!_throttle.TryGetValue(ip, out entry)) return false;
                if ((DateTime.UtcNow - entry.Start).TotalSeconds > ThrottleSeconds)
                {
                    _throttle.Remove(ip);
                    return false;
                }
                return entry.Count >= ThrottleAttempts;
            }
        }

        private void RecordFailure(string remote)
        {
            string ip = IpOf(remote);
            lock (_gate)
            {
                Throttle entry;
                if (!_throttle.TryGetValue(ip, out entry) ||
                    (DateTime.UtcNow - entry.Start).TotalSeconds > ThrottleSeconds)
                {
                    entry = new Throttle();
                    entry.Start = DateTime.UtcNow;
                    _throttle[ip] = entry;
                }
                entry.Count++;
            }
        }

        private void ClearFailures(string remote)
        {
            string ip = IpOf(remote);
            lock (_gate) { _throttle.Remove(ip); }
        }

        private static string IpOf(string remote)
        {
            int colon = remote.LastIndexOf(':');
            return colon > 0 ? remote.Substring(0, colon) : remote;
        }

        private void RaiseLog(string message)
        {
            Action<string> handler = Logged;
            if (handler != null) handler(message);
        }

        private sealed class Pending
        {
            public readonly TcpClient Client;
            public readonly Stream Stream;
            public readonly string Remote;
            public readonly TaskCompletionSource<bool> Paired = new TaskCompletionSource<bool>();
            public readonly TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>();
            public bool Claimed;

            public Pending(TcpClient client, Stream stream, string remote)
            {
                Client = client;
                Stream = stream;
                Remote = remote;
            }

            public void Close()
            {
                try { Client.Close(); } catch (Exception) { }
            }
        }

        private sealed class Throttle
        {
            public DateTime Start;
            public int Count;
        }
    }
}
