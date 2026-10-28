using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Shared;

namespace Host
{
    public static class InputInjector
    {
        private const uint InputMouse = 0;
        private const uint InputKeyboard = 1;

        private const uint MouseMove = 0x0001;
        private const uint LeftDown = 0x0002;
        private const uint LeftUp = 0x0004;
        private const uint RightDown = 0x0008;
        private const uint RightUp = 0x0010;
        private const uint MiddleDown = 0x0020;
        private const uint MiddleUp = 0x0040;
        private const uint WheelFlag = 0x0800;
        private const uint VirtualDesk = 0x4000;
        private const uint Absolute = 0x8000;

        private const uint KeyUp = 0x0002;
        private const uint Scancode = 0x0008;
        private const uint ExtendedKey = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        public static void Apply(InputEvent input, int streamWidth, int streamHeight)
        {
            if (streamWidth < 1 || streamHeight < 1) return;

            switch (input.Kind)
            {
                case InputKind.MouseMove:
                    SendMouseMove(input.A, input.B, streamWidth, streamHeight);
                    break;
                case InputKind.MouseButton:
                    SendMouseButton(input.A, (input.Flags & InputEvent.Down) != 0);
                    break;
                case InputKind.Wheel:
                    SendWheel(input.A);
                    break;
                case InputKind.Key:
                    SendKey((ushort)input.A, (input.Flags & InputEvent.Down) != 0);
                    break;
            }
        }

        private static void SendMouseMove(int x, int y, int streamWidth, int streamHeight)
        {
            INPUT input = new INPUT();
            input.type = InputMouse;
            input.u.mi.dx = (int)Normalize(x, streamWidth);
            input.u.mi.dy = (int)Normalize(y, streamHeight);
            input.u.mi.dwFlags = MouseMove | Absolute | VirtualDesk;
            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendMouseButton(int button, bool down)
        {
            uint flags;
            switch (button)
            {
                case 0: flags = down ? LeftDown : LeftUp; break;
                case 1: flags = down ? RightDown : RightUp; break;
                case 2: flags = down ? MiddleDown : MiddleUp; break;
                default: return;
            }

            INPUT input = new INPUT();
            input.type = InputMouse;
            input.u.mi.dwFlags = flags;
            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendWheel(int delta)
        {
            INPUT input = new INPUT();
            input.type = InputMouse;
            input.u.mi.mouseData = (uint)delta;
            input.u.mi.dwFlags = WheelFlag;
            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendKey(ushort virtualKey, bool down)
        {
            uint scan = MapVirtualKey(virtualKey, 0);
            uint flags = 0;
            if (scan != 0)
            {
                flags |= Scancode;
                if (IsExtended(virtualKey)) flags |= ExtendedKey;
            }
            if (!down) flags |= KeyUp;

            INPUT input = new INPUT();
            input.type = InputKeyboard;
            input.u.ki.wVk = scan == 0 ? virtualKey : (ushort)0;
            input.u.ki.wScan = (ushort)scan;
            input.u.ki.dwFlags = flags;
            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static bool IsExtended(ushort virtualKey)
        {
            switch (virtualKey)
            {
                case 0x21: case 0x22: case 0x23: case 0x24:
                case 0x25: case 0x26: case 0x27: case 0x28:
                case 0x2D: case 0x2E: case 0x5B: case 0x5C:
                case 0x5D: case 0x6F: case 0x90: case 0xA3:
                case 0xA5:
                    return true;
                default:
                    return false;
            }
        }

        private static uint Normalize(int value, int size)
        {
            double fraction = value / (double)Math.Max(1, size - 1);
            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;
            return (uint)Math.Round(fraction * 65535.0);
        }

        public static bool SelfTest()
        {
            POINT before;
            if (!GetCursorPos(out before)) return false;

            Rectangle bounds = SystemInformation.VirtualScreen;
            InputEvent move = new InputEvent(
                InputKind.MouseMove, before.X - bounds.Left, before.Y - bounds.Top, 0);
            Apply(move, bounds.Width, bounds.Height);

            POINT after;
            if (!GetCursorPos(out after)) return false;
            return Math.Abs(after.X - before.X) <= 2 && Math.Abs(after.Y - before.Y) <= 2;
        }
    }
}
