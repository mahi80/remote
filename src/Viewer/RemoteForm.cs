using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Shared;

namespace Viewer
{
    public sealed class RemoteForm : Form
    {
        private readonly SslStream _stream;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly object _frameLock = new object();
        private readonly StatusStrip _statusStrip;
        private readonly ToolStripStatusLabel _statusLabel;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private readonly string _target;

        private Bitmap _frame;
        private Rectangle _imageRect = Rectangle.Empty;
        private long _frames;
        private long _lastFrames;
        private bool _cursorHidden;
        private static Cursor _blankCursor;

        public RemoteForm(SslStream stream, string target)
        {
            _stream = stream;
            _target = target;

            Text = "RemoteDesktop - " + target;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size((int)(work.Width * 0.8), (int)(work.Height * 0.8));
            MinimumSize = new Size(560, 360);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 24);
            Icon = SystemIcons.Application;
            KeyPreview = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _statusLabel = new ToolStripStatusLabel();
            _statusLabel.Text = "Waiting for the first frame...";
            _statusLabel.Spring = true;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

            _statusStrip = new StatusStrip();
            _statusStrip.SizingGrip = false;
            _statusStrip.Items.Add(_statusLabel);
            Controls.Add(_statusStrip);

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 500;
            _statusTimer.Tick += delegate { UpdateStatus(); };
            _statusTimer.Start();

            Shown += delegate { Focus(); };
            Task.Run(new Func<Task>(ReceiveLoopAsync));
        }

        private Rectangle DisplayArea
        {
            get
            {
                int height = Math.Max(1, ClientSize.Height - _statusStrip.Height);
                return new Rectangle(0, 0, ClientSize.Width, height);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    Packet packet = await Protocol.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                    if (packet.Type != PacketType.Video) continue;

                    VideoFrame video = VideoFrame.FromPayload(packet.Payload);
                    Bitmap bitmap = Decode(video.Jpeg);

                    lock (_frameLock)
                    {
                        Bitmap previous = _frame;
                        _frame = bitmap;
                        if (previous != null) previous.Dispose();
                    }

                    Interlocked.Increment(ref _frames);
                    SafeInvalidate();
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                SafeStatus("Disconnected from " + _target + ".");
            }
        }

        private static Bitmap Decode(byte[] jpeg)
        {
            using (MemoryStream buffer = new MemoryStream(jpeg))
            using (Image image = Image.FromStream(buffer))
            {
                return new Bitmap(image);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Rectangle area = DisplayArea;

            lock (_frameLock)
            {
                if (_frame == null)
                {
                    _imageRect = Rectangle.Empty;
                    TextRenderer.DrawText(e.Graphics, "Waiting for the first frame...", Font, area,
                        Color.Gainsboro, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }

                double scale = Math.Min(area.Width / (double)_frame.Width, area.Height / (double)_frame.Height);
                int width = Math.Max(1, (int)(_frame.Width * scale));
                int height = Math.Max(1, (int)(_frame.Height * scale));
                _imageRect = new Rectangle(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);

                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(_frame, _imageRect);
            }
        }

        private void UpdateStatus()
        {
            long frames = Interlocked.Read(ref _frames);
            long delta = frames - _lastFrames;
            _lastFrames = frames;

            int width;
            int height;
            lock (_frameLock)
            {
                width = _frame == null ? 0 : _frame.Width;
                height = _frame == null ? 0 : _frame.Height;
            }

            if (frames == 0)
            {
                _statusLabel.Text = "Waiting for the first frame...";
                return;
            }

            _statusLabel.Text = string.Format("{0}x{1}   {2} fps   Esc to disconnect", width, height, delta * 2);
        }

        private bool TryMapPoint(Point location, bool clamp, out int x, out int y)
        {
            x = 0;
            y = 0;

            Rectangle rect = _imageRect;
            if (rect.Width <= 0 || rect.Height <= 0) return false;
            if (!clamp && !rect.Contains(location)) return false;

            double fx = (location.X - rect.X) / (double)rect.Width;
            double fy = (location.Y - rect.Y) / (double)rect.Height;
            if (fx < 0) fx = 0;
            if (fx > 1) fx = 1;
            if (fy < 0) fy = 0;
            if (fy > 1) fy = 1;

            int width;
            int height;
            lock (_frameLock)
            {
                if (_frame == null) return false;
                width = _frame.Width;
                height = _frame.Height;
            }

            x = (int)Math.Round(fx * Math.Max(1, width - 1));
            y = (int)Math.Round(fy * Math.Max(1, height - 1));
            return true;
        }

        private static Cursor BlankCursor()
        {
            if (_blankCursor == null)
            {
                using (Bitmap blank = new Bitmap(1, 1))
                {
                    _blankCursor = new Cursor(blank.GetHicon());
                }
            }
            return _blankCursor;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool overImage = _imageRect.Contains(e.Location) && !_imageRect.IsEmpty;
            if (overImage != _cursorHidden)
            {
                _cursorHidden = overImage;
                Cursor = overImage ? BlankCursor() : Cursors.Default;
            }

            int x;
            int y;
            if (TryMapPoint(e.Location, false, out x, out y))
                Send(new InputEvent(InputKind.MouseMove, x, y, 0));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            int button;
            switch (e.Button)
            {
                case MouseButtons.Left: button = 0; break;
                case MouseButtons.Right: button = 1; break;
                case MouseButtons.Middle: button = 2; break;
                default: return;
            }

            Capture = true;

            int x;
            int y;
            if (TryMapPoint(e.Location, true, out x, out y))
                Send(new InputEvent(InputKind.MouseMove, x, y, 0));

            Send(new InputEvent(InputKind.MouseButton, button, 0, InputEvent.Down));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            int button;
            switch (e.Button)
            {
                case MouseButtons.Left: button = 0; break;
                case MouseButtons.Right: button = 1; break;
                case MouseButtons.Middle: button = 2; break;
                default: return;
            }

            Capture = false;

            int x;
            int y;
            if (TryMapPoint(e.Location, true, out x, out y))
                Send(new InputEvent(InputKind.MouseMove, x, y, 0));

            Send(new InputEvent(InputKind.MouseButton, button, 0, 0));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Send(new InputEvent(InputKind.Wheel, e.Delta, 0, 0));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape)
            {
                Close();
                return;
            }

            if (e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;

            e.Handled = true;
            e.SuppressKeyPress = true;
            Send(new InputEvent(InputKind.Key, e.KeyValue, 0, InputEvent.Down));
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);

            if (e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;

            e.Handled = true;
            e.SuppressKeyPress = true;
            Send(new InputEvent(InputKind.Key, e.KeyValue, 0, 0));
        }

        private async void Send(InputEvent input)
        {
            if (_cts.IsCancellationRequested) return;

            try
            {
                await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await Protocol.WriteAsync(_stream, PacketType.Input, input.ToBytes(), _cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }

        private void SafeInvalidate()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate { Invalidate(); }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void SafeStatus(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate { _statusLabel.Text = text; }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _statusTimer.Stop();
            _cts.Cancel();
            _stream.Dispose();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_frameLock)
                {
                    if (_frame != null)
                    {
                        _frame.Dispose();
                        _frame = null;
                    }
                }
                _cts.Dispose();
                _sendLock.Dispose();
                _statusTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
