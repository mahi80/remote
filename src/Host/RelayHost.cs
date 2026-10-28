using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace Host
{
    // Dials out to a relay and stays registered under a session code, so the host
    // needs no inbound port or port forwarding. Reconnects after each session.
    public sealed class RelayHost : ISessionSource, IDisposable
    {
        private readonly string _relayHost;
        private readonly int _relayPort;
        private readonly string _code;
        private readonly SessionHandler _handler;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _activeGate = new object();
        private TcpClient _active;
        private Task _loop;
        private int _started;

        public RelayHost(string relayHost, int relayPort, string code, string passcode, X509Certificate2 certificate)
        {
            _relayHost = relayHost;
            _relayPort = relayPort;
            _code = code;
            _handler = new SessionHandler(passcode, certificate);
        }

        public string Passcode
        {
            get { return _handler.Passcode; }
            set { _handler.Passcode = value; }
        }

        public event Action<string> Logged
        {
            add { _handler.Logged += value; }
            remove { _handler.Logged -= value; }
        }

        public event Func<string, Task<bool>> ConsentRequested
        {
            add { _handler.ConsentRequested += value; }
            remove { _handler.ConsentRequested -= value; }
        }

        public event Action<string> SessionStarted
        {
            add { _handler.SessionStarted += value; }
            remove { _handler.SessionStarted -= value; }
        }

        public event Action SessionEnded
        {
            add { _handler.SessionEnded += value; }
            remove { _handler.SessionEnded -= value; }
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _loop = Task.Run(new Func<Task>(RunAsync));
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            CloseActive();
            _handler.WriteLog("Stopped connecting to the relay.");
        }

        private void CloseActive()
        {
            TcpClient active;
            lock (_activeGate)
            {
                active = _active;
                _active = null;
            }

            if (active == null) return;
            try { active.Close(); } catch (Exception) { }
        }

        private async Task RunAsync()
        {
            int delay = 1000;

            while (!_cts.IsCancellationRequested)
            {
                TcpClient client = new TcpClient();
                try
                {
                    client.NoDelay = true;
                    await client.ConnectAsync(_relayHost, _relayPort).ConfigureAwait(false);

                    lock (_activeGate) _active = client;

                    using (client)
                    {
                        using (CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                        {
                            handshake.CancelAfter(TimeSpan.FromSeconds(10));

                            Stream raw = client.GetStream();
                            await LineProtocol.WriteLineAsync(raw, "HOST " + _code, handshake.Token).ConfigureAwait(false);
                            string reply = await LineProtocol.ReadLineAsync(raw, handshake.Token).ConfigureAwait(false);
                            if (reply == null) throw new IOException("The relay closed the connection.");
                            if (!reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Relay refused registration: " + reply);
                        }

                        _handler.WriteLog("Registered with relay " + _relayHost + ":" + _relayPort + " (session code " + _code + ").");
                        delay = 1000;

                        string paired = await LineProtocol.ReadLineAsync(client.GetStream(), _cts.Token).ConfigureAwait(false);
                        if (paired == null) throw new IOException("The relay closed the connection.");
                        if (!paired.StartsWith("PAIRED", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Unexpected relay reply: " + paired);

                        string remote = "a viewer via the relay";
                        string extra = paired.Substring("PAIRED".Length).Trim();
                        if (extra.Length > 0) remote = extra + " (via relay)";

                        await _handler.HandleAsync(client.GetStream(), remote, _cts.Token).ConfigureAwait(false);
                        delay = 500;
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                    if (!_cts.IsCancellationRequested) _handler.WriteLog("Relay connection closed.");
                }
                catch (Exception ex)
                {
                    _handler.WriteLog("Relay connection problem: " + ex.Message);
                }
                finally
                {
                    lock (_activeGate)
                    {
                        if (ReferenceEquals(_active, client)) _active = null;
                    }
                    try { client.Close(); } catch (Exception) { }
                }

                if (_cts.IsCancellationRequested) break;

                try
                {
                    await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (delay < 15000) delay = Math.Min(delay * 2, 15000);
            }

            _handler.WriteLog("No longer connected to the relay.");
        }

        public void Dispose()
        {
            Stop();
            _handler.Dispose();
            _cts.Dispose();
        }
    }
}
