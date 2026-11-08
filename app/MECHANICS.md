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
| Device ID | `(mac[2] & 0x1F) << 24 \| mac[3] << 16 \| mac[4] << 8 \| mac[5]` as decimal, from the first network card's MAC address (GetAdaptersAddresses) |
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

## Permissions

`enable-keyboard`, `enable-clipboard`, `enable-audio`, `enable-file-transfer`; all on by default.

## Image quality, frame rate and view (option defaults)

| Option | Default | Allowed | Rplus |
|---|---|---|---|
| `view_style` | **original** | adaptive | default "Original size"; "Adjust window" = adaptive |
| `image_quality` | **balanced** | best, low, custom | same |
| `custom_image_quality` | **50** | 10 … 4095 | slider 10 … 2000 (above 2000 the ratio is capped, see below) |
| `custom-fps` | **30** | 5 … 120 | frame-rate slider, sent to the sharing side |

Quality → bitrate ratio:

| Quality | Ratio |
|---|---|
| best | 1.5 |
| balanced | 0.67 |
| low | 0.5 |
| custom q | clamp(2 × q / 100, 0.2, 40) |

Rplus sends JPEG frames, so the ratio becomes a JPEG quality:
`clamp(round(30 + 40 × ratio), 10, 95)`: best 90, balanced 57, low 50, custom 50 → 70.
The default frame period is 1/30 s (30 fps).

## Direct connection

| Thing | Value |
|---|---|
| Direct port | 21118 (`direct-access-port`, **estimated** default) |

## Own ID / relay server (self-hosted hbbs + hbbr)

| Thing | Value |
|---|---|
| ID server without a port | ":21116" is added |
| ID server list | `rendezvous-servers` (Rplus has no public fallback server) |
| Relay choice | `relay-server` setting → relay suggested by the other side → ID server with port + 1 |
| Port + 1 rule | "host:port" → "host:(port+1)" (21116 → 21117) |
| Relay entered without a port | 21117 (ID default + 1) |
| Register interval | 12 s — **estimated** |
| Wait for relay answer | 10 s — **estimated** |
| Server key | `key` setting, sent as licence_key |
| Status text | "Not ready. Please check your connection" |

Message flow (RendezvousMessage protobuf; UDP = raw datagram, TCP = BytesCodec frame whose
1–4 byte little-endian header holds `len<<2 | (headerBytes-1)`), verified live against a
self-hosted server:

- Host: UDP `register_peer`(6){id=1, serial=2} every interval; on `register_peer_response`(7)
  with request_pk(2) → `register_pk`(15){id=1, uuid=2, pk=3}; `register_pk_response`(16)
  result(1): 0 OK, 2 UUID mismatch, 4 too frequent.
- Controller: TCP `request_relay`(18){id=1, uuid=2, relay_server=4, licence_key=6}; waits for
  `relay_response`(19){relay_server=3, refuse_reason=6, version=7}.
- Host answers the forwarded `request_relay` with TCP `relay_response`{socket_addr=1, uuid=2,
  relay_server=3, id=4, version=7 "rplus-1.0"}; both join the relay with `request_relay`{uuid,
  licence_key}; the relay pipes bytes between them.

## Session protocol

Rplus uses its own simple session (JSON control messages + JPEG frames, see `Protocol.cs`),
so it only controls other computers running Rplus. When the relay answer's version isn't
Rplus's, it says so.

## Home screen layout

- "Your Desktop" card: ID, one-time password with refresh/copy, the text "Your desktop can be
  accessed with this ID and password.", approve mode, server status.
- "Control Remote Desktop": "Enter remote ID" field, Connect, Transfer file, recent sessions.
- Session toolbar: display, image quality, view style, keyboard, clipboard, files, chat,
  full screen, disconnect.
- File transfer: "Local computer" / "Remote computer" panes, "Create folder", "Select all".
