using System;
using System.Collections.Generic;
using OneIdentity.SafeguardDotNet;
using OneIdentity.SafeguardDotNet.Event;
using OneIdentity.SafeguardDotNet.A2A;
using System.Configuration;
using System.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace SampleA2aService
{
    internal class SampleService
    {
        private readonly string _safeguardAddress;
        private readonly string _safeguardClientCertificateThumbprint;
        private readonly int _safeguardApiVersion;
        private readonly bool _safeguardIgnoreSsl;

        private ISafeguardConnection _connection;
        private ISafeguardA2AContext _a2AContext;
        private readonly List<ISafeguardEventListener> _listeners = new List<ISafeguardEventListener>();

        private class MonitoredPassword
        {
            public SecureString ApiKey {get; set;}
            public string AssetName { get; set;}
            public string AccountName { get; set;}
            public override string ToString() => $"{AssetName}/{AccountName}";
        }
        private readonly List<MonitoredPassword> _monitoredPasswords = new List<MonitoredPassword>();

        public SampleService()
        {
            _safeguardAddress =
                ConfigUtils.ReadRequiredSettingFromAppConfig("SafeguardAddress", "Safeguard appliance network address");
            _safeguardClientCertificateThumbprint =
                ConfigUtils.ReadRequiredSettingFromAppConfig("SafeguardClientCertificateThumbprint",
                    "Safeguard client certificate thumbprint").ToUpper();
            _safeguardApiVersion =
                int.Parse(ConfigUtils.ReadRequiredSettingFromAppConfig("SafeguardApiVersion", "Safeguard API version"));
            _safeguardIgnoreSsl = bool.Parse(ConfigurationManager.AppSettings["SafeguardIgnoreSsl"]);
        }

        private void GetApiKeysFromA2ARegistrations()
        {
            // optionally you can have Safeguard look up all A2A registrations for a given certificate user thumbprint
            // currently this requires auditor permission, but we will enhance A2A to include read ability without it
            try
            {
                var a2AJson = _connection.InvokeMethod(Service.Core, Method.Get, "A2ARegistrations", parameters: new Dictionary<string, string> {
                    {"filter", $"CertificateUserThumbprint ieq '{_safeguardClientCertificateThumbprint}'"}
                });
                var a2AArray = JArray.Parse(a2AJson);
                foreach(dynamic a2A in a2AArray)
                {
                    var credsJson = _connection.InvokeMethod(Service.Core, Method.Get, $"A2ARegistrations/{a2A.Id}/RetrievableAccounts");
                    var credsArray = JArray.Parse(credsJson);
                    foreach (dynamic cred in credsArray)
                        _monitoredPasswords.Add(new MonitoredPassword 
                        {
                            ApiKey = ExtensionMethods.ToSecureString(cred.ApiKey.ToString()),
                            AssetName = cred.SystemName,
                            AccountName = cred.AccountName
                        });
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Unable to get API keys using certificate user, did you grant auditor permissions?", ex);
            }
        }

        private void PasswordChangeHandler(string eventName, string eventBody)
        {
            var eventInfo = JsonConvert.DeserializeObject<MonitoredPassword>(eventBody);
            Log.Information("Password changed for {MonitoredPassword}", eventInfo);

            // NOTE: eventInfo won't have the API key field filled out because that isn't in the eventBody Json
            //       You can look up in the list of _monitoredPasswords to find the API key

            // TODO: Add useful code here to fetch the new password and do something with it

        }

        private void StartListener(MonitoredPassword monitored)
        {
            Log.Information("Startling listener for {MonitoredPassword}", monitored);
            var listener = _a2AContext.GetPersistentA2AEventListener(monitored.ApiKey, PasswordChangeHandler);
            listener.Start();
            _listeners.Add(listener);
        }

        public void Start()
        {
            // connect to Safeguard
            _connection = Safeguard.Connect(_safeguardAddress, _safeguardClientCertificateThumbprint,
                _safeguardApiVersion, _safeguardIgnoreSsl);
            _a2AContext = Safeguard.A2A.GetContext(_safeguardAddress, _safeguardClientCertificateThumbprint,
                _safeguardApiVersion, _safeguardIgnoreSsl);

            // figure out what API keys to monitor
            GetApiKeysFromA2ARegistrations();
            if (_monitoredPasswords.Count == 0)
                throw new Exception("No API keys found in A2A registrations.  Nothing to do.");
            Log.Information("Found {MonitoredPasswordCount} API keys to monitor for password changes", _monitoredPasswords.Count);

            // start the listeners
            foreach(var monitored in _monitoredPasswords)
                StartListener(monitored);
        }

        public void Stop()
        {
            // shut everything down
            foreach (var listener in _listeners)
            {
                listener?.Stop();
                listener?.Dispose();
            }
            _connection?.Dispose();
            _a2AContext?.Dispose();
        }
    }
}
