using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shared
{
    public enum PacketType : byte
    {
        Video = 1,
        Input = 2,
        Text = 3
    }

    public static class Protocol
    {
        public const int HeaderSize = 5;
        public const int MaxPacketSize = 8 * 1024 * 1024;

        public static async Task WriteAsync(Stream stream, PacketType type, byte[] payload, CancellationToken ct)
        {
            byte[] header = new byte[HeaderSize];
            header[0] = (byte)type;
            byte[] length = BitConverter.GetBytes(payload.Length);
            Buffer.BlockCopy(length, 0, header, 1, 4);

            await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            if (payload.Length > 0)
                await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<Packet> ReadAsync(Stream stream, CancellationToken ct)
        {
            byte[] header = new byte[HeaderSize];
            await ReadExactAsync(stream, header, 0, header.Length, ct).ConfigureAwait(false);

            int length = BitConverter.ToInt32(header, 1);
            if (length < 0 || length > MaxPacketSize)
                throw new InvalidDataException("Packet length out of range: " + length);

            byte[] payload = new byte[length];
            if (length > 0)
                await ReadExactAsync(stream, payload, 0, length, ct).ConfigureAwait(false);

            return new Packet((PacketType)header[0], payload);
        }

        public static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            while (count > 0)
            {
                int read = await stream.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
                if (read <= 0) throw new IOException("Connection closed by the peer.");
                offset += read;
                count -= read;
            }
        }

        public static byte[] TextPacket(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        public static string ParseText(byte[] payload)
        {
            return Encoding.UTF8.GetString(payload);
        }
    }

    public struct Packet
    {
        public readonly PacketType Type;
        public readonly byte[] Payload;

        public Packet(PacketType type, byte[] payload)
        {
            Type = type;
            Payload = payload;
        }
    }

    public struct VideoFrame
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Jpeg;

        public VideoFrame(int width, int height, byte[] jpeg)
        {
            Width = width;
            Height = height;
            Jpeg = jpeg;
        }

        public byte[] ToPayload()
        {
            byte[] payload = new byte[4 + Jpeg.Length];
            byte[] width = BitConverter.GetBytes((ushort)Width);
            byte[] height = BitConverter.GetBytes((ushort)Height);
            payload[0] = width[0];
            payload[1] = width[1];
            payload[2] = height[0];
            payload[3] = height[1];
            Buffer.BlockCopy(Jpeg, 0, payload, 4, Jpeg.Length);
            return payload;
        }

        public static VideoFrame FromPayload(byte[] payload)
        {
            if (payload.Length < 5) throw new InvalidDataException("Video frame too short.");
            int width = BitConverter.ToUInt16(payload, 0);
            int height = BitConverter.ToUInt16(payload, 2);
            byte[] jpeg = new byte[payload.Length - 4];
            Buffer.BlockCopy(payload, 4, jpeg, 0, jpeg.Length);
            return new VideoFrame(width, height, jpeg);
        }
    }

    public enum InputKind : byte
    {
        MouseMove = 1,
        MouseButton = 2,
        Wheel = 3,
        Key = 4
    }

    public struct InputEvent
    {
        public const uint Down = 1;
        public const uint Extended = 2;

        public readonly InputKind Kind;
        public readonly int A;
        public readonly int B;
        public readonly uint Flags;

        public InputEvent(InputKind kind, int a, int b, uint flags)
        {
            Kind = kind;
            A = a;
            B = b;
            Flags = flags;
        }

        public byte[] ToBytes()
        {
            byte[] bytes = new byte[13];
            bytes[0] = (byte)Kind;
            BitConverter.GetBytes(A).CopyTo(bytes, 1);
            BitConverter.GetBytes(B).CopyTo(bytes, 5);
            BitConverter.GetBytes(Flags).CopyTo(bytes, 9);
            return bytes;
        }

        public static InputEvent FromBytes(byte[] bytes)
        {
            if (bytes.Length < 13) throw new InvalidDataException("Input event too short.");
            return new InputEvent(
                (InputKind)bytes[0],
                BitConverter.ToInt32(bytes, 1),
                BitConverter.ToInt32(bytes, 5),
                BitConverter.ToUInt32(bytes, 9));
        }
    }
}
