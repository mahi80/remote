# RemoteDesktop

A small, readable remote-desktop tool for Windows: a **host** that shares its screen and accepts remote mouse/keyboard input, and a **viewer** that connects to it.

It is intentionally minimal — two WinForms apps, no installer, no service, no dependencies. Everything builds with the C# compiler that ships with Windows.

Sessions are **consent-gated and fully visible**: the host window is always on screen, and every connection must be approved by a person at the host machine within 30 seconds.

## What it does

- Screen capture over GDI+, JPEG-compressed at up to 1600 px wide, sent only when the screen changes
- Mouse move, click, wheel, and keyboard input injected on the host
- TLS 1.2 on every connection, self-signed certificate, SHA-256 fingerprint pinning (TOFU)
- Six-digit one-time passcode, regenerable from the host window
- One active session at a time
- Tray icon, so the host keeps running while its window is closed

## What it does not do

- No clipboard, audio, file transfer, or multi-monitor selection (all monitors are captured as one virtual desktop)
- No Windows-key, Alt+Tab, or Ctrl+Alt+Del (the injected input cannot reach the secure desktop)
- No IME or non-ASCII text input
- No reconnect, no session recording, no unattended access
- The host is never hidden from the screen, from screen capture, or from the process list

## Requirements

- Windows 10 or 11
- .NET Framework 4.8 (present by default on current Windows)
- Bash (Git Bash is fine) to run `build.sh`

Nothing else to install — no .NET SDK, no Visual Studio, no NuGet.

## Build

```bash
bash build.sh
```

Output goes to `bin/`:

| File | Purpose |
| --- | --- |
| `bin/RemoteDesktop.Host.exe` | The machine being controlled |
| `bin/RemoteDesktop.Viewer.exe` | The machine doing the controlling |
| `bin/Smoke.exe` | Headless end-to-end self-test |

The script calls `csc.exe` from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319` and compiles with `-langversion:5`, so the source stays compatible with the in-box compiler.

## Usage

### On the machine to be controlled

```bash
bin/RemoteDesktop.Host.exe
```

The window shows:

- a **passcode** (six digits, changes when you press *New passcode*)
- the **listening address and port**
- the certificate **fingerprint**

Options:

```
--bind <address>     bind address            (default 0.0.0.0)
--port <number>      listen port             (default 5055)
--passcode <digits>  fixed passcode          (default: random per launch)
--cert <path>        certificate file        (default: host.pfx next to the exe)
--log <path>         write a log file
--inject-test        run the input-injection self-test and exit
```

The certificate (`host.pfx`, including its private key) is created on first run and reused afterward. Keep it local — it is in `.gitignore`.

Allow inbound TCP on the chosen port if Windows Firewall prompts, or add the rule yourself. For same-machine testing, use `--bind 127.0.0.1`.

Closing the window keeps the host listening from the notification area. Use the **Stop** button (it becomes **Start** again) or the tray menu's *Stop listening* to stop accepting connections, and *Exit* to quit.

### On the controlling machine

```bash
bin/RemoteDesktop.Viewer.exe
```

Enter the host address, port (default `5055`), and the passcode shown on the host, then press **Connect**.

Or connect directly:

```bash
bin/RemoteDesktop.Viewer.exe --connect 192.168.1.20:5055 --passcode 123456
```

```
--connect <host:port>  connect without opening the dialog
--passcode <digits>    passcode for that host
--trust-new            accept an unknown host fingerprint without prompting
--log <path>           write a log file
```

The viewer opens a window sized to 80% of your screen and mirrors the host's desktop inside it. Mouse movement, clicks, wheel, and typing are forwarded. Press **Esc** to disconnect.

### What happens on connect

1. The viewer opens a TLS connection and checks the host certificate against `known_hosts.txt`. An unknown fingerprint prompts for confirmation and is remembered on acceptance (`--trust-new` skips the prompt).
2. The viewer sends the passcode. A wrong passcode closes the connection.
3. The host shows a modal **Allow … to control this computer?** dialog naming the remote address. It auto-denies after 30 seconds. Nothing is shared or accepted until someone clicks **Allow**.
4. Video frames stream to the viewer, and input is accepted from that point on.

The host window shows a red banner with the connected address while a session is active, and logs connect/disconnect events.

## Test

```bash
bin/Smoke.exe
```

Runs the whole protocol in-process over loopback: starts a host on a free port with auto-approved consent, checks that a wrong passcode is rejected, receives and decodes three frames, verifies a mouse move round-trips to the correct coordinates, and expects exactly one consent request. Prints `PASS` and exits 0, or `FAIL` and exits 1.

## Limitations

- **Frame rate** is roughly 4–10 fps depending on screen size and change rate; this is a JPEG-over-TCP prototype, not a low-latency codec.
- **Input coverage** is limited to ordinary desktop applications (see *What it does not do*).
- **Single host, single session.** No relay, no NAT traversal, no discovery. Use a VPN or port forwarding for access across networks.
- **Self-signed certificate.** Identity is established by fingerprint pinning on first use; there is no CA.
- **Unsigned binaries.** Windows SmartScreen and endpoint protection may warn or block; the executables are built locally, not signed.

## Security notes

- Every session requires both the passcode and an explicit **Allow** click on the host.
- The host window is visible at all times and appears normally in the taskbar and process list. There is no hidden mode.
- Passcodes are compared in constant time and never written to the log.
- `host.pfx` and `known_hosts.txt` are deliberately excluded from version control. Do not commit them.

## Layout

```
build.sh              in-box csc build
src/Shared/           protocol framing, TLS, CLI, logging
src/Host/             screen capture, input injection, session server, host UI
src/Viewer/           client, known-hosts store, connect + remote UI
src/Smoke/            headless end-to-end test
```
