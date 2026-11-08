# Rplus mechanics sheet

Rplus is a C# Windows remote-desktop app. It talks to other Rplus computers over the LAN or a
direct IP, or through a self-hosted ID/relay server (hbbs + hbbr). Values marked **estimated**
were chosen for Rplus rather than taken from a specification. The machine-readable copy of
these values is `mechanics.json`.

## Program start

- **one copy at a time**: a named mutex. If Rplus is already running, the existing window is
  restored and brought to the front, and the new copy exits.
- the home window opens at 1000 × 660 (minimum 860 × 560), centred on screen.

## Identity & password

| Thing | Value |
|---|---|
| Device ID | `(mac[2..5] as big-endian u32) & 0x1FFFFFFF`, i.e. `(mac[2]&0x1F)<<24 \| mac[3]<<16 \| mac[4]<<8 \| mac[5]`, as decimal, from the first network card's MAC address (GetAdaptersAddresses) |
| ID kept once made | stored in config |
| No usable MAC | random 9-digit number (**estimated**) |
| ID taken on the server (UUID_MISMATCH) | Rplus picks a random new ID |
| Allowed ID text | a-z A-Z 0-9 - _, first a letter, length 6–16 |
| One-time password length | 6; 8 when `temporary-password-length` = "8"; 10 when "10" |
| One-time password letters | `23456789abcdefghijkmnpqrstuvwxyz` (32) |
| Digits-only one-time password | `0-9` when `allow-numeric-one-time-password` = "Y" |

## Approve / verification

| Setting | Values | Default |
|---|---|---|
| `approve-mode` | "password", "click", both | both |
| `verification-method` | "use-temporary-password", "use-permanent-password", both | both |

How approve-mode is applied at login:
- **click**: no password check; the session waits for the local user's Accept.
- **password**: the password is checked (per verification-method); no accept window.
- **both**: either a correct password or the local user's Accept lets the session in.

Settings ▸ "Accept sessions via" has two switches, "Accept window" and "Password". At least one
stays on; both on = either works. If the visitor's password is accepted first, the accept window
closes by itself. A wrong password can be retried while the accept window is still allowed.
`Rplus.exe --login-selftest <out.txt>` checks these rules against a local host.

## Permissions

`enable-keyboard`, `enable-clipboard`, `enable-audio`, `enable-file-transfer`; all on by default.

## Image quality, frame rate and view (option defaults)

| Option | Default | Allowed | Rplus |
|---|---|---|---|
| `view_style` | original | adaptive | "Original size" default; "Adjust window" = adaptive |
| `image_quality` | balanced | best, low, custom | same |
| `custom_image_quality` | 50 | 10 … 4095 | value clamped to 10 … 4095; slider 10 … 2000 (the ratio caps above 2000) |
| `custom-fps` | 30 | 5 … 120 | frame-rate slider, sent to the sharing side |

Quality → bitrate ratio:

| Quality | Ratio |
|---|---|
| best | 1.5 |
| balanced | 0.67 |
| low | 0.5 |
| custom q | clamp(2 × q / 100, 0.2, 40) |

Rplus sends JPEG frames, so the ratio becomes a JPEG quality:
`clamp(round(30 + 40 × ratio), 10, 95)` (**estimated** mapping): best 90, balanced 57, low 50.

## Direct connection

| Thing | Value |
|---|---|
| Direct port | `direct-access-port` if it is a number above 0, else 21118 |
| Address typed without a port | 21118 is added when connecting directly |

## Own ID / relay server (self-hosted hbbs + hbbr)

| Thing | Value |
|---|---|
| ID server without a port | ":21116" is added |
| ID server list | `rendezvous-servers` (Rplus has no public fallback server) |
| Relay choice | `relay-server` setting → relay suggested by the other side → ID server with port + 1 |
| Port + 1 rule | "host:port" → "host:(port+1)" |
| Relay without a port | ":21117" is added |
| Re-register interval | 15000 ms |
| First wait for an answer | 3000 ms |
| Wait growth | +3000 ms per missed answer, at most 30000 ms |
| Missed answers | more than 3 in a row → "Not ready" |
| Wait for relay answer | 10 s — **estimated** |
| Server key | `key` setting, sent as licence_key |
| Status text | "Not ready. Please check your connection" |

**Swapped ID/relay fields:** if the ID server doesn't answer within the first 3000 ms wait,
Rplus tries the relay field's address and then the same host on 21116. The one that answers
becomes the ID server, the two fields are swapped back, and the person is told. Settings also
warns when the ID port is 21117 or the relay port is 21116.

Message flow (RendezvousMessage protobuf; UDP = raw datagram, TCP = BytesCodec frame whose
1–4 byte little-endian header holds `len<<2 | (headerBytes-1)`), verified live against a
self-hosted server:

- Host: UDP `register_peer`(6){id=1, serial=2}; on `register_peer_response`(7) with
  request_pk(2) → `register_pk`(15){id=1, uuid=2, pk=3}; `register_pk_response`(16) result(1):
  0 OK, 2 UUID mismatch, 4 too frequent.
- Controller: TCP `request_relay`(18){id=1, uuid=2, relay_server=4, licence_key=6}; waits for
  `relay_response`(19){relay_server=3, refuse_reason=6, version=7}.
- Host answers with TCP `relay_response`{socket_addr=1, uuid=2, relay_server=3, id=4,
  version=7 "rplus-1.0"}; both join the relay with `request_relay`{uuid, licence_key}.

## Session protocol

Rplus uses its own simple session (JSON control messages + JPEG frames, see `Protocol.cs`),
so it only controls other computers running Rplus. When the relay answer's version isn't
Rplus's, it says so.

## File transfer

Files move in 256 KB chunks (see `FileTransfer.cs`), so screen frames keep flowing during a
transfer and there's no size limit beyond the disk. The controller numbers each transfer.

| Message | Direction | Body |
|---|---|---|
| `FileGet` (12) | controller → host | { path, id } — asks for a download |
| `XferStart` (17) | sender → receiver | { id, name, size, path } (path = destination on the host, uploads only) |
| `XferChunk` (18) | sender → receiver | [4-byte id][up to 256 KB] |
| `XferEnd` (19) | sender → receiver | { id, ok, error } — all chunks sent, or stopped |
| `XferResult` (20) | host → controller | { id, ok, error } — upload saved, or why not |
| `XferCancel` (21) | either | { id } |

- The receiver writes to `<name>.part` and renames it only when every byte has arrived, so a
  cancelled, failed or dropped transfer never leaves a partial file under the real name.
- The host keeps each connection's transfers separately and cleans them up when it ends.
- The file view shows one transfer at a time: name, percentage, amount, speed and Cancel;
  progress updates about ten times a second.
- The old one-message `FileData` (13) / `FilePut` (14) are retired, so both computers need
  Rplus 1.0.13 or later to transfer files.
- `Rplus.exe --transfer-selftest <out.txt>` sends files both ways between a host and controller
  on this PC (temp folders only), checks every byte, and checks that cancelled transfers leave
  nothing behind.

## Light and dark

- Rplus follows the Windows app mode (`AppsUseLightTheme` under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`; 0 = dark) and switches
  open windows in place when Windows changes it.
- `--theme dark` or `--theme light` forces one mode for that run (used for screenshots).
- Brand colours (blue accent, green OK, red danger) are the same in both modes; the canvas, cards,
  borders, text and input fields swap palettes (see `Theme.cs`). Title bars, scroll bars and
  drop-downs use Windows' own dark style.

## Home screen layout

- "Your Desktop" card: ID, one-time password with refresh/copy, the text "Your desktop can be
  accessed with this ID and password.", approve mode, server status.
- "Control Remote Desktop": "Enter remote ID" field, Connect, Transfer file, recent sessions.
- Session toolbar: display, image quality, view style, keyboard, clipboard, files, chat,
  full screen, disconnect.
- File transfer: "Local computer" / "Remote computer" panes, "Create folder", "Select all".
