# Rplus

<img src="app/shots/icon.png" width="96" align="right" alt="Rplus icon">

Remote desktop for Windows. Share your screen with an ID and a one-time password, or control
another computer running Rplus — on the same network, by direct IP, or through your own
self-hosted ID/relay server.

## What it does

- **Your desktop:** a device ID made from the network card and a one-time password (6, 8 or 10
  characters, optionally digits only) with refresh and copy. Incoming connections can be
  approved by password, by clicking Accept, or both.
- **Control a remote desktop:** enter the remote ID or address and connect; recent sessions
  and favourites are kept on the home screen.
- **Session window:** image quality (best / balanced / low / custom), frame rate, original size
  or fit-to-window, keyboard and clipboard, file transfer, chat, full screen.
- **Own server:** point Rplus at a self-hosted ID server (hbbs, port 21116) and relay (hbbr,
  port 21117) plus its key, and two Rplus computers can find each other by ID across networks.
  Without a server it works over the LAN / direct IP on port 21118.
- One copy at a time; settings are in the Settings page.

Rplus only controls other computers running Rplus. `app/MECHANICS.md` lists the defaults and
the wire protocol.

## Building

Needs the .NET 8 SDK (or newer) on Windows.

```powershell
cd app
dotnet run                     # run from source
dotnet build -c Release        # app\bin\Release\net8.0-windows\Rplus.exe
```
