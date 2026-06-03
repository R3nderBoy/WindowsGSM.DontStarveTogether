using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using WindowsGSM.Functions;
using WindowsGSM.GameServer.Engine;
using WindowsGSM.GameServer.Query;

namespace WindowsGSM.Plugins
{
    public class DontStarveTogether : SteamCMDAgent
    {
        // Plugin metadata
        public Plugin Plugin = new Plugin
        {
            name = "WindowsGSM.DontStarveTogether",
            author = "R3nderBoy",
            description = "🧩 WindowsGSM plugin for Don't Starve Together Dedicated Server with sharded world support (Overworld + Caves)",
            version = "1.0",
            url = "https://github.com/R3nderBoy/WindowsGSM.DontStarveTogether",
            color = "#b07d48"
        };

        // Tracks the caves shard process per server instance
        private static readonly Dictionary<string, Process> _cavesProcesses = new Dictionary<string, Process>();

        public DontStarveTogether(ServerConfig serverData) : base(serverData) => base.serverData = _serverData = serverData;
        private readonly ServerConfig _serverData;

        // SteamCMD settings
        public override bool loginAnonymous => true;
        public override string AppId => "343050";

        // Server fixed properties
        // Uses 64-bit executable for better performance on modern systems
        public override string StartPath => @"bin64/dontstarve_dedicated_server_nullrenderer_x64.exe";
        public string FullName = "Don't Starve Together";
        public bool AllowsEmbedConsole = true;
        public int PortIncrements = 3;
        public object QueryMethod = new A2S();

        // Server defaults
        public string Port = "10999";       // Master shard port
        public string QueryPort = "27016";  // Steam master server port
        public string Defaultmap = "MyDediServer";
        public string Maxplayers = "6";
        public string Additional = "";

        // Derived port for caves shard (master port - 1)
        private int CavesPort => int.TryParse(_serverData.ServerPort, out int p) ? p - 1 : 10998;

        // Auto-generate server configuration files on first install
        public async void CreateServerCFG()
        {
            string serverFiles = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID);
            string clusterName = string.IsNullOrWhiteSpace(_serverData.ServerMap) ? Defaultmap : _serverData.ServerMap;
            string clusterPath = Path.Combine(serverFiles, "bin64", "serverdatafolder", clusterName);

            string masterPath = Path.Combine(clusterPath, "Master");
            string cavesPath = Path.Combine(clusterPath, "Caves");

            Directory.CreateDirectory(masterPath);
            Directory.CreateDirectory(cavesPath);

            // cluster.ini — shared cluster settings
            string clusterIni = $@"[GAMEPLAY]
max_players = {_serverData.ServerMaxPlayer ?? Maxplayers}
pvp = false
game_mode = survival
pause_when_empty = true

[NETWORK]
cluster_name = {clusterName}
cluster_description = A Don't Starve Together dedicated server
cluster_password =
cluster_intention = cooperative
lan_only_cluster = false

[MISC]
console_enabled = true

[SHARD]
shard_enabled = true
bind_ip = 127.0.0.1
master_ip = 127.0.0.1
master_port = 10888
cluster_key = defaultSuperSecret
";
            await File.WriteAllTextAsync(Path.Combine(clusterPath, "cluster.ini"), clusterIni);

            // cluster_token.txt — placeholder; user MUST replace with their Klei token
            string tokenPath = Path.Combine(clusterPath, "cluster_token.txt");
            if (!File.Exists(tokenPath))
            {
                await File.WriteAllTextAsync(tokenPath,
                    "PASTE_YOUR_CLUSTER_TOKEN_HERE\n" +
                    "Get your token at: https://accounts.klei.com/account/game/servers?game=DontStarveTogether");
            }

            // Master shard server.ini (Overworld)
            string masterServerIni = $@"[NETWORK]
server_port = {_serverData.ServerPort ?? Port}

[STYPE]
is_master = true

[STEAM]
master_server_port = {_serverData.ServerQueryPort ?? QueryPort}
authentication_port = 8766
";
            await File.WriteAllTextAsync(Path.Combine(masterPath, "server.ini"), masterServerIni);

            // Caves shard server.ini
            string cavesServerIni = $@"[NETWORK]
server_port = {CavesPort}

[STYPE]
is_master = false
is_secondary = true

[STEAM]
master_server_port = 27017
authentication_port = 8767
";
            await File.WriteAllTextAsync(Path.Combine(cavesPath, "server.ini"), cavesServerIni);

            // worldgenoverride.lua for Caves shard (required to mark it as caves)
            string cavesWorldgen = @"return {
    override_enabled = true,
    preset = ""DST_CAVE"",
}
";
            await File.WriteAllTextAsync(Path.Combine(cavesPath, "worldgenoverride.lua"), cavesWorldgen);

            // modoverrides.lua stubs
            string modOverrides = "return {}\n";
            await File.WriteAllTextAsync(Path.Combine(masterPath, "modoverrides.lua"), modOverrides);
            await File.WriteAllTextAsync(Path.Combine(cavesPath, "modoverrides.lua"), modOverrides);
        }

        // Start both shards; returns the master shard process to WindowsGSM
        public async Task<Process> Start()
        {
            string serverFiles = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID);
            string bin64Path = Path.Combine(serverFiles, "bin64").Replace(@"\", "/");
            string exePath = Path.Combine(serverFiles, StartPath);

            if (!Directory.Exists(bin64Path))
            {
                Error = $"Directory not found: {bin64Path}";
                return null;
            }

            if (!File.Exists(exePath))
            {
                Error = $"Executable not found: {exePath}";
                return null;
            }

            string clusterName = string.IsNullOrWhiteSpace(_serverData.ServerMap) ? Defaultmap : _serverData.ServerMap;

            // Check cluster token
            string tokenPath = Path.Combine(serverFiles, "bin64", "serverdatafolder", clusterName, "cluster_token.txt");
            if (!File.Exists(tokenPath))
            {
                Error = $"cluster_token.txt not found at: {tokenPath}";
                return null;
            }

            string tokenContent = await File.ReadAllTextAsync(tokenPath);
            if (tokenContent.Contains("PASTE_YOUR_CLUSTER_TOKEN_HERE"))
            {
                Error = "cluster_token.txt contains placeholder. Replace it with your Klei cluster token from https://accounts.klei.com";
                return null;
            }

            // Start caves shard first (master will connect to it)
            var cavesProcess = await LaunchShard(exePath, bin64Path, clusterName, "Caves", CavesPort.ToString(), false);
            if (cavesProcess != null)
            {
                lock (_cavesProcesses)
                    _cavesProcesses[_serverData.ServerID] = cavesProcess;
            }

            // Brief delay so caves shard can initialize its shard port before master connects
            await Task.Delay(3000);

            // Start master shard (returned to WindowsGSM as the primary tracked process)
            var masterProcess = await LaunchShard(exePath, bin64Path, clusterName, "Master",
                _serverData.ServerPort ?? Port, true);

            // Log startup commands for troubleshooting
            string logPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "startupCommandsUsed.log");
            File.WriteAllText(logPath,
                $"Master: -persistent_storage_root \"{bin64Path}\" -conf_dir serverdatafolder -cluster \"{clusterName}\" -shard Master -port {_serverData.ServerPort ?? Port} -steam_master_server_port {_serverData.ServerQueryPort ?? QueryPort} -players {_serverData.ServerMaxPlayer ?? Maxplayers}\n" +
                $"Caves:  -persistent_storage_root \"{bin64Path}\" -conf_dir serverdatafolder -cluster \"{clusterName}\" -shard Caves -port {CavesPort}");

            return masterProcess;
        }

        // Stop both shards gracefully
        public async Task Stop(Process p)
        {
            // Stop caves shard
            lock (_cavesProcesses)
            {
                if (_cavesProcesses.TryGetValue(_serverData.ServerID, out Process caves) && caves != null && !caves.HasExited)
                {
                    ShutdownProcess(caves);
                    _cavesProcesses.Remove(_serverData.ServerID);
                }
            }

            // Stop master shard
            await Task.Run(() => ShutdownProcess(p));
        }

        // Helper: send graceful shutdown command to a shard process
        private static void ShutdownProcess(Process p)
        {
            try
            {
                if (p == null || p.HasExited) return;

                if (p.StartInfo.RedirectStandardInput)
                    p.StandardInput.WriteLine("c_shutdown(true)");
                else
                    ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "c_shutdown(true)");
            }
            catch { /* process may have already exited */ }
        }

        // Helper: launch a single shard and return its Process
        private async Task<Process> LaunchShard(string exePath, string binPath, string clusterName,
            string shardName, string port, bool isMaster)
        {
            var param = new StringBuilder();
            param.Append($" -persistent_storage_root \"{binPath}\"");
            param.Append($" -conf_dir serverdatafolder");
            param.Append($" -cluster \"{clusterName}\"");
            param.Append($" -shard {shardName}");
            param.Append($" -port {port}");

            if (isMaster)
            {
                param.Append($" -steam_master_server_port {_serverData.ServerQueryPort ?? QueryPort}");
                param.Append($" -players {_serverData.ServerMaxPlayer ?? Maxplayers}");
                if (!string.IsNullOrWhiteSpace(_serverData.ServerParam))
                    param.Append($" {_serverData.ServerParam}");
            }

            var p = new Process
            {
                StartInfo =
                {
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = false,
                    WorkingDirectory = binPath,
                    FileName = exePath,
                    Arguments = param.ToString()
                },
                EnableRaisingEvents = true
            };

            if (AllowsEmbedConsole)
            {
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.RedirectStandardInput = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;

                var serverConsole = new ServerConsole(_serverData.ServerID);
                p.OutputDataReceived += serverConsole.AddOutput;
                p.ErrorDataReceived += serverConsole.AddOutput;

                try
                {
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    return p;
                }
                catch (Exception e)
                {
                    Error = $"[{shardName}] {e.Message}";
                    return null;
                }
            }

            try
            {
                p.Start();
                return p;
            }
            catch (Exception e)
            {
                Error = $"[{shardName}] {e.Message}";
                return null;
            }
        }
    }
}
