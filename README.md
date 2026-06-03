# WindowsGSM.DontStarveTogether

A [WindowsGSM](https://windowsgsm.com/) plugin for hosting a **Don't Starve Together** dedicated server with full sharded world support (Overworld + Caves).

## Features

- Installs and updates the DST dedicated server via SteamCMD (App ID `343050`)
- Launches **two shards**: Master (Overworld) and Caves — both managed together
- Auto-generates a working `cluster.ini`, `server.ini` (for each shard), and `worldgenoverride.lua` on first install
- Uses the 64-bit server executable (`bin64/dontstarve_dedicated_server_nullrenderer_x64.exe`)
- Graceful shutdown via `c_shutdown(true)` for both shards (saves the world before stopping)

## Requirements

- [WindowsGSM](https://github.com/WindowsGSM/WindowsGSM) >= 1.21.0
- A Klei cluster token (free — see below)

## Installation

1. Download or clone this repository.
2. Copy the `DST.cs/` folder into your WindowsGSM `plugins/` directory:
   ```
   WindowsGSM/plugins/DST.cs/
   ├── DST.cs
   ├── DST.png
   └── author.png
   ```
3. Restart WindowsGSM — the plugin will appear in the game server list.
4. Install a new server instance using the plugin.

## Getting Your Cluster Token

You **must** provide a cluster token before the server will start.

1. Go to https://accounts.klei.com/account/game/servers?game=DontStarveTogether
2. Log in with your Klei / Steam account.
3. Enter any name and click **Add New Server** to generate a token.
4. Copy the token string.
5. Open the file:
   ```
   <WindowsGSM>/servers/<ID>/serverfiles/bin64/serverdatafolder/<ClusterName>/cluster_token.txt
   ```
6. Replace the placeholder text with your token (single line, no extra whitespace).

The server will refuse to start if the placeholder is still present.

## Port Usage

| Port  | Purpose                         |
|-------|---------------------------------|
| 10999 | Master shard (Overworld) — UDP  |
| 10998 | Caves shard — UDP               |
| 27016 | Steam master server port — UDP  |
| 10888 | Internal shard-to-shard — UDP   |

All ports are configurable via the generated `server.ini` and `cluster.ini` files.

## Configuration Files

After installation the following files are created automatically:

```
serverfiles/bin64/serverdatafolder/<ClusterName>/
├── cluster.ini           # Cluster-wide settings (name, password, max players…)
├── cluster_token.txt     # YOUR TOKEN GOES HERE
├── Master/
│   ├── server.ini        # Overworld shard network settings
│   └── modoverrides.lua
└── Caves/
    ├── server.ini        # Caves shard network settings
    ├── worldgenoverride.lua   # Marks this shard as a cave world
    └── modoverrides.lua
```

Edit `cluster.ini` to change the server name, password, game mode, or max players before first start.

## Mods

To add Steam Workshop mods, edit:
```
serverfiles/mods/dedicated_server_mods_setup.lua
```
Add lines like:
```lua
ServerModSetup("123456789")  -- replace with the Workshop mod ID
```

Then enable them in `Master/modoverrides.lua` and `Caves/modoverrides.lua`.

## Troubleshooting

- Check `serverfiles/startupCommandsUsed.log` for the exact launch arguments used.
- Ensure both UDP ports (10999 and 10998) are open in your firewall/router.
- If the caves shard fails to connect, wait a few seconds after master starts — the 3-second startup delay usually handles this.
