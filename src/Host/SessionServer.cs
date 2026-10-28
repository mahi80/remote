using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Host
{
    public sealed class SessionServer : ISessionSource
    {
        private readonly TcpListener _listener;
        private readonly SessionHandler _handler;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _acceptTask;
        private int _busy;

        public SessionServer(string bindAddress, int port, string passcode, X509Certificate2 certificate)
        {
            BindAddress = bindAddress;
            Port = port;
            _handler = new SessionHandler(passcode, certificate);
            _listener = new TcpListener(IPAddress.Parse(bindAddress), port);
        }

        public string BindAddress { get; private set; }
        public int Port { get; private set; }

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
            _listener.Start();
            _acceptTask = Task.Run(new Func<Task>(AcceptLoopAsync));
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            _handler.WriteLog("Stopped listening.");
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

                if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                {
                    _handler.WriteLog("Rejected a second connection: one session at a time.");
                    client.Close();
                    continue;
                }

                TcpClient session = client;
                string remote = session.Client.RemoteEndPoint == null
                    ? "unknown"
                    : session.Client.RemoteEndPoint.ToString();

#pragma warning disable 4014
                Task.Run(async delegate
                {
                    try
                    {
                        using (session)
                        {
                            session.NoDelay = true;
                            await _handler.HandleAsync(session.GetStream(), remote, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _handler.WriteLog("Session error: " + ex.Message);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busy, 0);
                    }
                });
#pragma warning restore 4014
            }
        }

        public void Dispose()
        {
            Stop();
            _handler.Dispose();
            _cts.Dispose();
        }
    }
}
