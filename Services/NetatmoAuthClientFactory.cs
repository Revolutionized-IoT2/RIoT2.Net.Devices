using System.Text;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services
{
    /// <summary>
    /// Keeps one <see cref="NetatmoAuthClient"/> per client id. Each client stores its tokens in
    /// <c>Data/netatmoAuth-&lt;clientId&gt;.json</c>. The file of earlier versions, <c>Data/netatmoAuth.json</c>, stays
    /// with the account it belongs to: the first client id that uses it is written into it, so the same account
    /// gets it after every restart and no one has to log in again.
    /// </summary>
    public sealed class NetatmoAuthClientFactory : INetatmoAuthClientFactory
    {
        internal const string LegacyTokenFile = "netatmoAuth.json";

        private readonly Dictionary<string, NetatmoAuthClient> _clients = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private readonly string _dataDirectory;
        private readonly ILogger _logger;
        private readonly Func<string, string, Task<HttpResponseMessage>> _postForm;
        private string _legacyOwner;

        public NetatmoAuthClientFactory(ILogger logger)
            : this(logger, "Data", PostFormAsync) { }

        internal NetatmoAuthClientFactory(ILogger logger, string dataDirectory, Func<string, string, Task<HttpResponseMessage>> postForm)
        {
            _logger = logger;
            _dataDirectory = dataDirectory;
            _postForm = postForm;
        }

        public NetatmoAuthClient Get(string clientId)
        {
            clientId ??= "";
            lock (_lock)
            {
                if (!_clients.TryGetValue(clientId, out var client))
                {
                    client = new NetatmoAuthClient(clientId, ResolveTokenFile(clientId), _logger, _postForm);
                    _clients.Add(clientId, client);
                }
                return client;
            }
        }

        private string ResolveTokenFile(string clientId)
        {
            var own = Path.Combine(_dataDirectory, $"netatmoAuth-{SafeFileName(clientId)}.json");
            if (File.Exists(own) || _legacyOwner != null)
                return own;

            var legacy = Path.Combine(_dataDirectory, LegacyTokenFile);
            if (!File.Exists(legacy))
                return own;

            NetatmoAuth stored;
            try
            {
                stored = File.ReadAllText(legacy).ToObj<NetatmoAuth>();
            }
            catch (Exception)
            {
                // Unreadable: the first account takes it and reports the error when it loads the tokens.
                _legacyOwner = clientId;
                return legacy;
            }

            if (!string.IsNullOrEmpty(stored?.client_id) && stored.client_id != clientId)
                return own;

            _legacyOwner = clientId;
            if (stored != null && string.IsNullOrEmpty(stored.client_id))
            {
                stored.client_id = clientId;
                try
                {
                    File.WriteAllText(legacy, stored.ToJson());
                    _logger.LogInformation("Netatmo token file {File} now belongs to client {ClientId}.", legacy, clientId);
                }
                catch (Exception x)
                {
                    _logger.LogWarning(x, "Could not record the client id in Netatmo token file {File}.", legacy);
                }
            }
            return legacy;
        }

        private static string SafeFileName(string clientId)
        {
            var name = new StringBuilder(clientId.Length);
            foreach (var character in clientId)
                name.Append(char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_' ? character : '_');
            return name.Length == 0 ? "default" : name.ToString();
        }

        private static Task<HttpResponseMessage> PostFormAsync(string url, string body) =>
            Core.Utils.Web.PostAsync(url, body, new Dictionary<string, string> { { "content-type", "application/x-www-form-urlencoded" } });
    }
}
