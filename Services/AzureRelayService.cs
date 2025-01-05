using Microsoft.Azure.Relay;
using System.Net;
using System.Web;
using RIoT2.Core;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services
{

    /*
        1. Go to Azure portal
        2. Create Relay - Create namespace

        3. On the Relay page, select Shared access policies on the left menu.
        4. On the Shared access policies page, select RootManageSharedAccessKey
        5. Under SAS Policy: RootManageSharedAccessKey, select the Copy button next to Primary Connection String. This action copies the connection string to your clipboard for later use. Paste this value into Notepad or some other temporary location.

        6. On the left menu, Under Entities, select Hybrid Connections, and then select + Hybrid Connection.
        7. On the Create Hybrid Connection page, enter a name for the hybrid connection, and select Create.

     */

    public class AzureRelayService : IAzureRelayService
    {
        private string _relayNamespace = "{RelayNamespace}.servicebus.windows.net";
        private string _connectionName = "{HybridConnectionName}";
        private string _keyName = "{SASKeyName}";
        private string _key = "{SASKey}";
        private string _status = "";

        private HybridConnectionListener _listener;
        private CancellationTokenSource _cts;

        public event WebMessageHandler MessageReceived;

        public AzureRelayService() 
        {
        }

        public void Configure(string relayNamespace, string connectionName, string keyName, string key) 
        {
            _relayNamespace = _relayNamespace.Replace("{RelayNamespace}", relayNamespace);
            _connectionName = connectionName;
            _keyName = keyName;
            _key = key;
            _status = "";
        }

        public async Task StartAsync()
        {
            if (String.IsNullOrEmpty(_relayNamespace) ||
                String.IsNullOrEmpty(_connectionName) ||
                String.IsNullOrEmpty(_keyName) ||
                String.IsNullOrEmpty(_key))
                    throw new Exception("AzureRelayService is not configured.");

            _cts = new CancellationTokenSource();

            var tokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider(_keyName, _key);
            _listener = new HybridConnectionListener(new Uri(string.Format("sb://{0}/{1}", _relayNamespace, _connectionName)), tokenProvider);

            // Subscribe to the status events.
            _listener.Connecting += (o, e) => { _status = "connecting"; };
            _listener.Offline += (o, e) => { _status = "offline"; };
            _listener.Online += (o, e) => { _status = "online"; };

            // Provide an HTTP request handler
            _listener.RequestHandler = (context) =>
            {
                // Do something with context.Request.Url, HttpMethod, Headers, InputStream...
                context.Response.StatusCode = HttpStatusCode.OK;
                context.Response.StatusDescription = "OK";
                
                
                //read incoming headers
                var headers = new Dictionary<string, string>();
                foreach (var k in context.Request.Headers.AllKeys)
                    headers.Add(k, context.Request.Headers[k]);

                //read incoming method
                var method = context.Request.HttpMethod;

                //read incoming message
                var body = new StreamReader(context.Request.InputStream).ReadToEnd();

                //read query string params
                var querystrings = new Dictionary<string, string>();
                var nvc = HttpUtility.ParseQueryString(context.Request.Url.ToString());
                foreach (var k in nvc.AllKeys) 
                {
                    if (String.IsNullOrEmpty(k))
                        continue;

                    querystrings.Add(k, nvc[k]);
                }
                    

                /*
                using (var sw = new StreamWriter(context.Response.OutputStream))
                {
                    sw.WriteLine("received");
                }*/

                MessageReceived(method, body, querystrings, headers);

                // The context MUST be closed here
                context.Response.Close();
            };

            // Opening the listener establishes the control channel to
            // the Azure Relay service. The control channel is continuously 
            // maintained, and is reestablished when connectivity is disrupted.
            await _listener.OpenAsync(_cts.Token);
        }

        public async Task StopAsync() 
        {
            _cts.Cancel();
            await _listener.CloseAsync();
        }
    }
}
