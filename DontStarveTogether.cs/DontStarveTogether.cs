using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using WindowsGSM.Functions;
using WindowsGSM.GameServer.Engine;
using WindowsGSM.GameServer.Query;
using WindowsGSM.Installer;

namespace WindowsGSM.Plugins
{
    public class DontStarveTogether : SteamCMDAgent
    {
        public Plugin Plugin = new Plugin
        {
            name = "WindowsGSM.DontStarveTogether",
            author = "R3nderBoy",
            description = "🧩 WindowsGSM plugin for Don't Starve Together Dedicated Server with sharded world support (Overworld + Caves)",
            version = "1.0",
            url = "https://github.com/R3nderBoy/WindowsGSM.DontStarveTogether",
            color = "#b07d48"
        };

        private readonly ServerConfig _serverData;
        private static readonly Dictionary<string, Process> _cavesProcesses = new Dictionary<string, Process>();

        public new string Error;
        public new string Notice;

        public string FullName = "Don't Starve Together";
        public new string StartPath = @"bin64\dontstarve_dedicated_server_nullrenderer_x64.exe";
        public bool AllowsEmbedConsole = true;
        public int PortIncrements = 3;
        public object QueryMethod = new A2S();

        public string Port = "10999";
        public string QueryPort = "27016";
        public string Defaultmap = "MyDediServer";
        public string Maxplayers = "6";
        public string Additional = "";

        public new string AppId = "343050";

        public DontStarveTogether(ServerConfig serverData) : base(serverData) => base.serverData = _serverData = serverData;

        private int CavesPort => int.TryParse(_serverData.ServerPort, out int p) ? p - 1 : 10998;

        public void CreateServerCFG()
        {
            string serverFiles = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID);
            string clusterName = string.IsNullOrWhiteSpace(_serverData.ServerMap) ? Defaultmap : _serverData.ServerMap;
            string clusterPath = Path.Combine(serverFiles, "bin64", "serverdatafolder", clusterName);
            string masterPath = Path.Combine(clusterPath, "Master");
            string cavesPath = Path.Combine(clusterPath, "Caves");

            Directory.CreateDirectory(masterPath);
            Directory.CreateDirectory(cavesPath);

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
            File.WriteAllText(Path.Combine(clusterPath, "cluster.ini"), clusterIni);

            string tokenPath = Path.Combine(clusterPath, "cluster_token.txt");
            if (!File.Exists(tokenPath))
            {
                File.WriteAllText(tokenPath,
                    "PASTE_YOUR_CLUSTER_TOKEN_HERE\n" +
                    "Get your token at: https://accounts.klei.com/account/game/servers?game=DontStarveTogether");
            }

            string masterServerIni = $@"[NETWORK]
server_port = {_serverData.ServerPort ?? Port}

[STYPE]
is_master = true

[STEAM]
master_server_port = {_serverData.ServerQueryPort ?? QueryPort}
authentication_port = 8766
";
            File.WriteAllText(Path.Combine(masterPath, "server.ini"), masterServerIni);

            string cavesServerIni = $@"[NETWORK]
server_port = {CavesPort}

[STYPE]
is_master = false
is_secondary = true

[STEAM]
master_server_port = 27017
authentication_port = 8767
";
            File.WriteAllText(Path.Combine(cavesPath, "server.ini"), cavesServerIni);

            File.WriteAllText(Path.Combine(cavesPath, "worldgenoverride.lua"),
                "return {\n    override_enabled = true,\n    preset = \"DST_CAVE\",\n}\n");

            string modOverrides = "return {}\n";
            File.WriteAllText(Path.Combine(masterPath, "modoverrides.lua"), modOverrides);
            File.WriteAllText(Path.Combine(cavesPath, "modoverrides.lua"), modOverrides);
        }

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

            string tokenPath = Path.Combine(serverFiles, "bin64", "serverdatafolder", clusterName, "cluster_token.txt");
            if (!File.Exists(tokenPath))
            {
                Error = $"cluster_token.txt not found at: {tokenPath}";
                return null;
            }

            string tokenContent = File.ReadAllText(tokenPath);
            if (tokenContent.Contains("PASTE_YOUR_CLUSTER_TOKEN_HERE"))
            {
                Error = "cluster_token.txt contains placeholder. Replace it with your Klei cluster token from https://accounts.klei.com";
                return null;
            }

            var cavesProcess = await LaunchShard(exePath, bin64Path, clusterName, "Caves", CavesPort.ToString(), false);
            if (cavesProcess != null)
            {
                lock (_cavesProcesses)
                    _cavesProcesses[_serverData.ServerID] = cavesProcess;
            }

            await Task.Delay(3000);

            var masterProcess = await LaunchShard(exePath, bin64Path, clusterName, "Master",
                _serverData.ServerPort ?? Port, true);

            string logPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "startupCommandsUsed.log");
            File.WriteAllText(logPath,
                $"Master: -persistent_storage_root \"{bin64Path}\" -conf_dir serverdatafolder -cluster \"{clusterName}\" -shard Master -port {_serverData.ServerPort ?? Port} -steam_master_server_port {_serverData.ServerQueryPort ?? QueryPort} -players {_serverData.ServerMaxPlayer ?? Maxplayers}\n" +
                $"Caves:  -persistent_storage_root \"{bin64Path}\" -conf_dir serverdatafolder -cluster \"{clusterName}\" -shard Caves -port {CavesPort}");

            return masterProcess;
        }

        public async Task Stop(Process p)
        {
            lock (_cavesProcesses)
            {
                if (_cavesProcesses.TryGetValue(_serverData.ServerID, out Process caves) && caves != null && !caves.HasExited)
                {
                    ShutdownProcess(caves);
                    _cavesProcesses.Remove(_serverData.ServerID);
                }
            }

            await Task.Run(() => ShutdownProcess(p));
        }

        public new async Task<Process> Install()
        {
            var steamCMD = new Installer.SteamCMD();
            Process p = await steamCMD.Install(_serverData.ServerID, string.Empty, AppId);
            Error = steamCMD.Error;
            return p;
        }

        public new async Task<Process> Update(bool validate = false, string custom = null)
        {
            var (p, error) = await Installer.SteamCMD.UpdateEx(_serverData.ServerID, AppId, validate, custom: custom);
            Error = error;
            return p;
        }

        public new bool IsInstallValid()
        {
            string exePath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            return File.Exists(exePath);
        }

        public new bool IsImportValid(string path)
        {
            string exePath = Path.Combine(path, StartPath);
            Error = $"Invalid Path! Fail to find {StartPath}";
            return File.Exists(exePath);
        }

        public new string GetLocalBuild()
        {
            var steamCMD = new Installer.SteamCMD();
            return steamCMD.GetLocalBuild(_serverData.ServerID, AppId);
        }

        public new async Task<string> GetRemoteBuild()
        {
            var steamCMD = new Installer.SteamCMD();
            return await steamCMD.GetRemoteBuild(AppId);
        }

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
            catch { }
        }

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

                var serverConsole = new Functions.ServerConsole(_serverData.ServerID);
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
