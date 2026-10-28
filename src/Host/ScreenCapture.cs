using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Shared;

namespace Host
{
    public sealed class ScreenCapture : IDisposable
    {
        private const int MaxWidth = 1600;
        private const long JpegQuality = 60L;
        private const int ThumbSize = 48;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        private readonly ImageCodecInfo _jpegCodec;
        private readonly EncoderParameters _encoderParams;
        private ulong _lastHash;
        private bool _hasLastHash;

        public ScreenCapture()
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                {
                    _jpegCodec = codec;
                    break;
                }
            }

            if (_jpegCodec == null) throw new InvalidOperationException("No JPEG encoder available.");

            _encoderParams = new EncoderParameters(1);
            _encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        }

        public VideoFrame? CaptureChanged()
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            int width;
            int height;
            Bitmap scaled;

            using (Bitmap full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(full))
                {
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }

                width = Math.Min(full.Width, MaxWidth);
                height = (int)Math.Round(full.Height * (width / (double)full.Width));
                scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);

                using (Graphics graphics = Graphics.FromImage(scaled))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(full, new Rectangle(0, 0, width, height));
                }
            }

            POINT cursor;
            if (!GetCursorPos(out cursor))
            {
                cursor.X = 0;
                cursor.Y = 0;
            }

            ulong hash = Hash(scaled, cursor);
            if (_hasLastHash && hash == _lastHash)
            {
                scaled.Dispose();
                return null;
            }

            _lastHash = hash;
            _hasLastHash = true;

            DrawCursor(scaled, bounds, cursor);

            byte[] jpeg;
            using (MemoryStream buffer = new MemoryStream())
            {
                scaled.Save(buffer, _jpegCodec, _encoderParams);
                jpeg = buffer.ToArray();
            }
            scaled.Dispose();

            return new VideoFrame(width, height, jpeg);
        }

        private static ulong Hash(Bitmap frame, POINT cursor)
        {
            const ulong prime = 1099511628211UL;
            ulong hash = 14695981039346656037UL;

            using (Bitmap thumb = new Bitmap(ThumbSize, ThumbSize, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(thumb))
                {
                    graphics.InterpolationMode = InterpolationMode.Bilinear;
                    graphics.DrawImage(frame, new Rectangle(0, 0, ThumbSize, ThumbSize));
                }

                hash = (hash ^ (ulong)thumb.Width) * prime;
                hash = (hash ^ (ulong)thumb.Height) * prime;
                for (int y = 0; y < ThumbSize; y++)
                {
                    for (int x = 0; x < ThumbSize; x++)
                    {
                        hash = (hash ^ (ulong)thumb.GetPixel(x, y).ToArgb()) * prime;
                    }
                }
            }

            hash = (hash ^ (ulong)cursor.X) * prime;
            hash = (hash ^ (ulong)cursor.Y) * prime;
            return hash;
        }

        private static void DrawCursor(Bitmap frame, Rectangle bounds, POINT cursor)
        {
            double scaleX = frame.Width / (double)bounds.Width;
            double scaleY = frame.Height / (double)bounds.Height;
            float x = (float)((cursor.X - bounds.Left) * scaleX);
            float y = (float)((cursor.Y - bounds.Top) * scaleY);

            if (x < -24 || y < -24 || x > frame.Width + 24 || y > frame.Height + 24) return;

            using (Graphics graphics = Graphics.FromImage(frame))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                PointF[] points = new PointF[]
                {
                    new PointF(x, y),
                    new PointF(x, y + 17f),
                    new PointF(x + 4.5f, y + 12.5f),
                    new PointF(x + 11f, y + 11.5f)
                };

                using (SolidBrush fill = new SolidBrush(Color.White))
                using (Pen outline = new Pen(Color.Black, 1.6f))
                {
                    graphics.FillPolygon(fill, points);
                    graphics.DrawPolygon(outline, points);
                }
            }
        }

        public void Dispose()
        {
            _encoderParams.Dispose();
        }
    }
}
