using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

using PRoCon.Core;
using PRoCon.Core.Plugin;


namespace PRoConEvents
{
    public class BattlefieldAgency : PRoConPluginAPI, IPRoConPluginInterface
    {

        /* Inherited:
            this.PunkbusterPlayerInfoList = new Dictionary<string, CPunkbusterInfo>();
            this.FrostbitePlayerInfoList = new Dictionary<string, CPlayerInfo>();
        */
        public string version = "1.0.3";
        #region globalVars
        private bool enabled;
        private bool announceKicks;
        private bool vpnKicks;
        private bool toxicityBans;
        private bool crashingBans;
        private bool glitchingBans;
        private bool stolenaccountBans;
        private bool kickLog;
        private string kickLogPath;
        private bool debug;
        private string apiKey;
        private string serverIp;
        private int serverPort;
        private List<Guid> whitelist;

        private int currentPlayerCount;
        private bool firstServerInfoAfterEnable;

        private readonly ConcurrentDictionary<string, int> currentlyKicking;
        private readonly ConcurrentQueue<Tuple<string, Dictionary<string, object>>> offlineMessagesQueue;
        private readonly Assembly apiDll;
        private readonly object api;
        private object ws;
        private DateTime lastConnectionAttempt;

        private Regex pbGuidComputedRegex;

        public enum LogEventLevel
        {
            Verbose = 0,
            Debug = 1,
            Information = 2,
            Warning = 3,
            Error = 4,
            Fatal = 5
        }
        #endregion

        #region Constructor
        public BattlefieldAgency()
        {
            this.enabled = false;
            this.apiKey = "";
            this.announceKicks = true;
            this.vpnKicks = true;
            this.crashingBans = true;
            this.toxicityBans = true;
            this.glitchingBans = true;
            this.stolenaccountBans = true;
            this.kickLog = false;
            this.debug = false;
            this.whitelist = new List<Guid>();

            this.currentPlayerCount = 0;
            this.firstServerInfoAfterEnable = false;

            // There is no ConcurrentList<T> for some reason so this will have to do.
            this.currentlyKicking = new ConcurrentDictionary<string, int>();
            this.offlineMessagesQueue = new ConcurrentQueue<Tuple<string, Dictionary<string, object>>>();

            this.kickLogPath = "Logs/BAKicks.log";
            this.apiDll = Assembly.LoadFrom(Path.GetFullPath("Plugins/BattlefieldAgencyAPI.ext"));
            this.api = this.apiDll.CreateInstance("BattlefieldAgencyAPI.BattlefieldAgency_API");
            this.ws = null;
            this.lastConnectionAttempt = default;

            // Copied from PRoConApplication.cs since there is no way to access it directly
            this.pbGuidComputedRegex = new Regex(@":[ ]+?Player Guid Computed[ ]+?(?<guid>[A-Fa-f0-9]+)\(.*?\)[ ]+?\(slot #(?<slotid>[0-9]+)\)[ ]+?(?<ip>[0-9\.:]+)[ ]+?(?<name>.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        #endregion

        #region API integration
        private bool APIIsAvailable()
        {
            return this.api != null && this.ws != null;
        }
        private bool APIIsAlive()
        {
            return this.APIIsAvailable() && (bool)this.api.GetType().GetMethod("WebsocketAlive").Invoke(this.api, new object[] { this.ws });
        }

        private bool APIRunConditionsMet()
        {
            return this.enabled && this.serverIp != null && this.serverPort != 0 && !this.apiKey.Equals("") && this.currentPlayerCount > 0;
        }

        private void APIRestoreState()
        {
            if (this.APIRunConditionsMet() && this.ws == null)
            {
                if (this.lastConnectionAttempt > DateTime.Now.Subtract(new TimeSpan(0, 0, 0, 30)))
                {
                    DebugWrite("Last connection attempt less than 30 seconds ago, not trying to restore state");
                    return;
                }
                ConsoleWrite("API connecting...");
                this.APIConnect();
            } else if (!APIRunConditionsMet() && this.ws != null)
            {
                ConsoleWrite("API stopping...");
                this.APIClose();
            }
        }

        private void APIConnect()
        {
            if (this.APIIsAlive())
            {
                ConsoleWrite("API already connected, aborting connect");
                return;
            }
            this.lastConnectionAttempt = DateTime.Now;
            string username = String.Format("{0}_{1}", this.serverIp, this.serverPort);
            DebugWrite(String.Format("[APIConnect] Starting websocket with {0}:{1}", username, this.apiKey));
            this.ws = this.api.GetType().GetMethod("GetWebsocket").Invoke(
                this.api,
                new object[] {
                    username,
                    this.apiKey,
                    (Action)(() => APIConnected()),
                    (Action<ushort, string>)((ushort code, string reason) => APIClosed(code, reason)),
                    (Action<string, Exception>)((string error, Exception ex) => APIError(error, ex)),
                    (Action<List<Object>>)((List<Object> msg) => APIMessageReceived(msg)),
                }
            );
            this.api.GetType().GetMethod("ConnectWebsocket").Invoke(this.api, new object[] { this.ws });
        }

        private void APIClose()
        {
            this.api.GetType().GetMethod("CloseWebsocket").Invoke(this.api, new object[] { this.ws });
        }

        private void APISendMessage(string method, Dictionary<string, object> args)
        {
            DebugWrite(String.Format("[APISendMessage] {0}", method));
            List<object> message = new List<object> { method, args };
            this.api.GetType().GetMethod("SendMessage").Invoke(this.api, new Object[] { message, this.ws });
        }

        private void APISendMessageQueued(string method, Dictionary<string, object> args)
        {
            if (this.APIIsAlive())
            {
                this.APISendMessage(method, args);
            } else
            {
                DebugWrite(String.Format("[APISendMessageQueued] Queuing message: {0}", method));
                // Cheap "ringbuffer"
                int toRemove = System.Math.Abs(9 - this.offlineMessagesQueue.Count);
                for (int i = 0; i < toRemove; i++) this.offlineMessagesQueue.TryDequeue(out _);
                this.offlineMessagesQueue.Enqueue(new Tuple<string, Dictionary<string, object>>(method, args));
            }
        }

        private Dictionary<string, object> APIBuildConfig()
        {
            List<string> enabledBanReasons = new List<string>{ "Cheating" };
            if (this.crashingBans)
            {
                enabledBanReasons.Add("Crashing");
            }
            if (this.toxicityBans)
            {
                enabledBanReasons.Add("Toxicity");
            }
            if (this.glitchingBans)
            {
                enabledBanReasons.Add("Glitching");
            }
            if (this.stolenaccountBans)
            {
                enabledBanReasons.Add("Stolen account");
            }
            Dictionary<string, object> config = new Dictionary<string, object>
            {
                { "plugin_version", this.version },
                { "enable_ban_reasons", enabledBanReasons },
                { "enable_vpn_kicks", this.vpnKicks },
                { "whitelist", this.whitelist.ConvertAll(guid => guid.ToString()) }
            };
            return config;
        }

        private void APIPushConfig()
        {
            Dictionary<string, object> args = new Dictionary<string, object>
            {
                { "config", this.APIBuildConfig() }
            };
            this.APISendMessage("push_config", args);
        }

        private void APISubmitEAGUID(string soldierName, string guid)
        {
            Dictionary<string, object> args = new Dictionary<string, object>
            {
                { "name", soldierName },
                { "ea_guid", guid }
            };
            this.APISendMessageQueued("submit_ea_guid", args);
        }

        private void APISubmitPB(string soldierName, string pbGuid, string ip)
        {
            Dictionary<string, object> args = new Dictionary<string, object>
            {
                { "name", soldierName },
                { "pb_guid", pbGuid },
                { "ip", ip.Split(':')[0] }
            };
            this.APISendMessageQueued("submit_pb", args);
        }

        private void APIConnected()
        {
            this.ConsoleWrite("Connected to API");
            this.lastConnectionAttempt = default;
            this.APIPushConfig();
            Tuple<string, Dictionary<string, object>> msgTuple;
            while (this.offlineMessagesQueue.TryDequeue(out msgTuple))
            {
                DebugWrite(String.Format("[APIConnected] Sending queued message: {0}", msgTuple.Item1));
                this.APISendMessage(msgTuple.Item1, msgTuple.Item2);
            }
        }

        private void APIClosed(ushort code, string reason)
        {
            this.DebugWrite(String.Format("[APIClosed] Thread ID: {0}", Thread.CurrentThread.ManagedThreadId));
            this.ConsoleWrite(String.Format("Connection to API closed: {0} ({1})", code, reason));
            this.ws = null;
            if (this.APIRunConditionsMet())
            {
                this.ConsoleWrite("Trying to reconnect in 30s");
            }
        }

        private void APIError(string error, Exception ex)
        {
            this.DebugWrite(String.Format("[APIError] Thread ID: {0}", Thread.CurrentThread.ManagedThreadId));
            this.ConsoleWrite(String.Format("API error: {0}, Exception: {1}", error, ex), LogEventLevel.Error);
        }

        private void APIMessageReceived(List<object> message)
        {
            this.DebugWrite(String.Format("[APIMessageReceived] Thread ID: {0}", Thread.CurrentThread.ManagedThreadId));
            string method = (string)message[0];
            this.DebugWrite(String.Format("Received method call: {0}", method));
            Dictionary<object, object> args = (Dictionary<object, object>)message[1];
            if (method.Equals("kick"))
            {
                string soldierName = (string)args["name"];
                string guid = (string)args["guid"];
                this.currentlyKicking.TryRemove(soldierName, out int _);
                if ((bool)args["log"])
                {
                    string logReason = (string)args["log_reason"];
                    ConsoleWrite(String.Format("[Kick] {0}", logReason));
                    if (this.announceKicks)
                    {
                        this.ExecuteCommand("procon.protected.send", "admin.say", String.Format("[Battlefield Agency] [Kick] {0}", logReason), "all");
                    }
                    if (this.kickLog)
                    {
                        this.WriteKickLog(logReason);
                    }
                }
                this.ExecuteKick(soldierName, guid, (string)args["kick_reason"]);
                
            } else if (method.Equals("plugin_log"))
            {
                string logMsg = (string)args["message"];
                this.ConsoleWrite(logMsg);
            }
        }

        #endregion

        #region Helper functions
        public String FormatMessage(String msg, LogEventLevel level)
        {
            return String.Format("[BA] {0}: {1}", level, msg);
        }

        private void ConsoleWrite(string msg, LogEventLevel level)
        {
            // Log if debug enabled or the message log level is at least "info"
            if (this.debug || level.CompareTo(LogEventLevel.Information) >= 0)
            {
                this.ExecuteCommand("procon.protected.pluginconsole.write", FormatMessage(msg, level));
            }
        }

        private void ConsoleWrite(string msg)
        {
            ConsoleWrite(msg, LogEventLevel.Information);
        }

        private void DebugWrite(string msg)
        {
            ConsoleWrite(msg, LogEventLevel.Debug);
        }

        private void WriteKickLog(string logReason)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.kickLogPath));
            using (System.IO.StreamWriter file = new System.IO.StreamWriter(this.kickLogPath, true))
            {
                DateTime date = DateTime.UtcNow;
                CultureInfo culture = new CultureInfo("en-GB");
                file.WriteLine(String.Format("[{0}] [{1}:{2}] {3}", date.ToString(culture), this.serverIp, this.serverPort, logReason));
            }
        }

        private void WriteAPIKeyWarning()
        {
            this.ConsoleWrite("API key not set, please acquire one at https://battlefield.agency and paste it into the API key setting", LogEventLevel.Error);
        }
        #endregion

        #region Plugin info

        public string GetPluginName()
        {
            return "Battlefield Agency";
        }

        public string GetPluginVersion()
        {
            return this.version;
        }

        public string GetPluginAuthor()
        {
            return "Battlefield Agency";
        }

        public string GetPluginWebsite()
        {
            return "battlefield.agency";
        }

        public string GetPluginDescription()
        {
            return @"
    <img src='https://battlefield.agency/assets/banner-procon.png'>
	<h2>Description</h2><br/>
	<p>
        This plugin lets you enforce the Battlefield Agency global ban list and kick VPN users while contributing to the database.
    </p>
	<h2>Settings</h2><br/>
	<h4>API Key - </h4>
	<p>Visit <a href='https://battlefield.agency'>battlefield.agency</a>, log in using your Discord account and search for your server using the search bar. Then click <code>Verify</code> at the top and follow the instructions.</p>
	<br/>
    <h3>Enforcement</h3><br/>
	<h4>VPN kicks - </h4>
	<p>Specifies if the plugin should kick VPN users.</p>
	<br/>
    <h4>Enable toxicity ban list - </h4>
    <p>Specifies if the optional ban list for extremely toxic players should should be enforced. Read the ban policy for more information on this. Cheating bans are always enforced.</p>
    <br/>
    <h4>Enable crasher ban list - </h4>
    <p>Specifies if the optional ban list for crashing accounts should should be enforced. Read the ban policy for more information on this. Cheating bans are always enforced.</p>
    <br/>
    <h4>Enable glitching ban list - </h4>
    <p>Specifies if the optional ban list for glitching accounts should should be enforced. Read the ban policy for more information on this. Cheating bans are always enforced.</p>
    <br/>
    <h4>Enable stolen account ban list - </h4>
    <p>Specifies if the optional ban list for stolen accounts should should be enforced. Read the ban policy for more information on this. Cheating bans are always enforced.</p>
    <br/>
	<h4>Player Whitelist - </h4>
	<p>Place any battlefield.agency player GUIDs (visible in the URL, like <code>https://battlefield.agency/player/&#60;id&#62;</code>) here to be excluded from being kicked.</p>
	<br/>
    <h3>Logging</h3><br/>
	<h4>Announce enforced bans - </h4>
	<p>Specifies if the plugin should announce enforced bans to global server chat.</p>
	<br/>
	<h4>Log kicks to file - </h4>
	<p>Specifies whether to log players kicked by the plugin to an external log file.</p>
	<br/>
	<h4>Kick log file path - </h4>
	<p>Specifies the file name that the plugin should write the kick log to. It defaults to <code>Logs/BAKicks.log</code>.</p>
	<br/>
	<h4>Debug - </h4>
	<p>Specifies whether to output verbose/debug information to the console or not.</p>
	";
        }

        #endregion

        #region Plugin variables
        public List<CPluginVariable> GetDisplayPluginVariables()
        {
            return new List<CPluginVariable>
            {
                new CPluginVariable("API Key", this.apiKey.GetType(), this.apiKey),
                new CPluginVariable("Enforcement|VPN kicks", this.vpnKicks.GetType(), this.vpnKicks),
                new CPluginVariable("Enforcement|Enable toxicity ban list", this.toxicityBans.GetType(), this.toxicityBans),
                new CPluginVariable("Enforcement|Enable crasher ban list", this.crashingBans.GetType(), this.crashingBans),
                new CPluginVariable("Enforcement|Enable glitching ban list", this.glitchingBans.GetType(), this.glitchingBans),
                new CPluginVariable("Enforcement|Enable stolen account ban list", this.stolenaccountBans.GetType(), this.stolenaccountBans),
                new CPluginVariable("Enforcement|Whitelist", typeof(string[]), this.whitelist.ConvertAll(guid => guid.ToString()).ToArray()),
                new CPluginVariable("Logging|Announce enforced bans", this.announceKicks.GetType(), this.announceKicks),
                new CPluginVariable("Logging|Log kicks to file", this.kickLog.GetType(), this.kickLog),
                new CPluginVariable("Logging|Kick log file path", this.kickLogPath.GetType(), this.kickLogPath),
                new CPluginVariable("Logging|Debug", this.debug.GetType(), this.debug)
            };
        }

        public List<CPluginVariable> GetPluginVariables()
        {
            List<CPluginVariable> pluginVars = new List<CPluginVariable>();
            foreach (CPluginVariable var in this.GetDisplayPluginVariables())
            {
                string[] nameSplit = var.Name.Split('|');
                pluginVars.Add(new CPluginVariable(nameSplit[nameSplit.Length-1], var.Type, var.Value));                
            }
            return pluginVars;
        }

        public void SetPluginVariable(string strVariable, string strValue)
        {
            bool remoteConfigChanged = false;
            DebugWrite(String.Format("[SetPluginVariable] {0}: {1}", strVariable, strValue));
            if (strVariable.Equals("API Key"))
            {
                if (this.enabled)
                {
                    if (strValue.Equals(""))
                    {
                        this.WriteAPIKeyWarning();
                    }
                    else if (!this.apiKey.Equals(strValue) && this.ws != null)
                    {
                        this.APIClose();
                    }
                }
                this.apiKey = strValue;
                this.APIRestoreState();
            }
            if (strVariable.Equals("Announce enforced bans"))
            {
                this.announceKicks = bool.Parse(strValue);
            }
            if (strVariable.Equals("VPN kicks"))
            {
                this.vpnKicks = bool.Parse(strValue);
                remoteConfigChanged = true;
            }
            if (strVariable.Equals("Enable toxicity ban list"))
            {
                this.toxicityBans = bool.Parse(strValue);
                remoteConfigChanged = true;
            }
            if (strVariable.Equals("Enable crasher ban list"))
            {
                this.crashingBans = bool.Parse(strValue);
                remoteConfigChanged = true;
            }
            if (strVariable.Equals("Enable glitching ban list"))
                        {
                            this.glitchingBans = bool.Parse(strValue);
                            remoteConfigChanged = true;
                        }
            if (strVariable.Equals("Enable stolen account ban list"))
                        {
                            this.stolenaccountBans = bool.Parse(strValue);
                            remoteConfigChanged = true;
                        }
            if (strVariable.Equals("Whitelist"))
            {
                List<Guid> whitelist = new List<Guid>();
                if (strValue.Equals(""))
                {
                    if (this.whitelist.Count > 0)
                    {
                        remoteConfigChanged = true;
                    }
                    this.whitelist = whitelist;
                } else
                {
                    try
                    {
                        foreach (string guidStr in CPluginVariable.DecodeStringArray(strValue))
                        {
                            whitelist.Add(Guid.Parse(guidStr));
                        }
                        this.whitelist = whitelist;
                        remoteConfigChanged = true;
                    }
                    catch
                    {
                        ConsoleWrite("Invalid GUID in whitelist", LogEventLevel.Error);
                    }
                }
            }
            if (strVariable.Equals("Log kicks to file"))
            {
                this.kickLog = bool.Parse(strValue);

            }
            if (strVariable.Equals("Kick log file path"))
            {
                this.kickLogPath = strValue;
            }
            if (strVariable.Equals("Debug"))
            {
                this.debug = bool.Parse(strValue);
            }
            if (remoteConfigChanged && this.APIIsAlive()) this.APIPushConfig();
        }
        #endregion

        #region PRoCon events
        public void OnPluginLoaded(string strHostName, string strPort, string strPRoConVersion)
        {
            var events = new[]
            {
                "OnServerInfo",
                "OnServerType",
                "OnPlayerJoin",
                "OnPlayerAuthenticated",
                "OnPlayerDisconnected",
		        "OnPunkbusterMessage",
	        };

            this.RegisterEvents(this.GetType().Name, events);
        }

        public void OnPluginEnable()
        {
            this.enabled = true;
            this.firstServerInfoAfterEnable = true;
            ConsoleWrite("Plugin Enabled");
            this.ExecuteCommand("procon.protected.send", "serverInfo");
            this.ExecuteCommand("procon.protected.send", "vars.serverType");
            this.APIRestoreState();
        }

        public void OnPluginDisable()
        {
            this.currentlyKicking.Clear();
            this.firstServerInfoAfterEnable = false;
            this.lastConnectionAttempt = default;
            this.enabled = false;
            this.APIRestoreState();
            ConsoleWrite("Plugin Disabled");
        }

        private async void ExecuteKick(string soldierName, string guid, string reason)
        {
            // While we still use the soldier name for tracking, it doesn't really matter since the actual enforcement is done by GUID
            DebugWrite(String.Format("[ExecuteKick] soldierName: '{0}', guid: '{1}', reason: '{2}'", soldierName, guid, reason));
            this.currentlyKicking.TryAdd(soldierName, 0);
            for (int i = 1; i <= 20 && this.currentlyKicking.ContainsKey(soldierName); i++)
            {
                DebugWrite(String.Format("[ExecuteKick] soldierName: '{0}', guid: '{1}': attempt {2}", soldierName, guid, i));
                this.ExecuteCommand("procon.protected.send", "banList.add", "guid", guid, "seconds", "1", reason);
                await System.Threading.Tasks.Task.Delay(5000);
            }
            this.currentlyKicking.TryRemove(soldierName, out int _);
        }
        public override void OnPlayerDisconnected(string soldierName, string reason)
        {
            DebugWrite(String.Format("[OnPlayerDisconnected] Trying to remove from kick attempt list '{0}'", soldierName));
            this.currentlyKicking.TryRemove(soldierName, out int _);
        }

        public override void OnServerType(string value)
        {
            DebugWrite(String.Format("[OnServerType] Server type: {0}", value));
            if (value.Equals("OFFICIAL"))
            {
                ConsoleWrite("This is an official server. VPN kicks and ban enforcement won't work through the plugin.", LogEventLevel.Warning);
            }
        }

        public override void OnServerInfo(CServerInfo serverInfo)
        {
            this.DebugWrite(String.Format("[OnServerInfo] Thread ID: {0}", Thread.CurrentThread.ManagedThreadId));
            if (this.firstServerInfoAfterEnable)
            {
                if (this.apiKey.Equals("")) this.WriteAPIKeyWarning();
                this.firstServerInfoAfterEnable = false;
            }
            string[] components = serverInfo.ExternalGameIpandPort.Split(':');
            this.serverIp = components[0];
            this.serverPort = int.Parse(components[1]);
            this.currentPlayerCount = serverInfo.PlayerCount;
            DebugWrite(String.Format("[OnServerInfo] IP: {0}, Port: {1}, Player count: {2}", this.serverIp, this.serverPort, this.currentPlayerCount));
            this.APIRestoreState();
        }

        public override void OnPlayerAuthenticated(string soldierName, string guid)
        {
            DebugWrite(String.Format("[OnPlayerAuthenticated] soldierName: {0}, guid: {1}", soldierName, guid));
            if (this.currentPlayerCount == 0) this.ExecuteCommand("procon.protected.send", "serverInfo");
            this.APISubmitEAGUID(soldierName, guid);
        }

        // Using this instead of OnPunkbusterPlayerInfo since there is no destinction between PB player guid computed and list events in the PRoCon event...
        public override void OnPunkbusterMessage(string punkbusterMessage)
        {
            Match match = this.pbGuidComputedRegex.Match(punkbusterMessage);
            if (match.Success)
            {
                string soldierName = match.Groups["name"].Value;
                string pbGuid = match.Groups["guid"].Value;
                string ip = match.Groups["ip"].Value;
                DebugWrite(String.Format("[OnPunkbusterMessage] soldierName: {0}, pbGuid: {1}, ip: {2}", soldierName, pbGuid, ip));
                this.APISubmitPB(soldierName, pbGuid, ip);
            }
        }
        #endregion

    } // end BattlefieldAgency

} // end namespace PRoConEvents

// Please fix PRoCon. Either allow loading .dll plugins directly, or have a folder with plugin dependencies that are loaded before reflective compilation.
// And `await` plugin callbacks so we can write efficient async code (.NET 4.7 greets you :) )
// And fix the OnPlayerJoin/OnPlayerAuthenticated events :3
// Thanks <3 from the Battlefield Agency team striving for better performance & code quality
