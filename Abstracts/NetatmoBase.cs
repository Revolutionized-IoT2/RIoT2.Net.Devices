using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.Interfaces;
using RIoT2.Net.Devices.Services.Netatmo;

namespace RIoT2.Net.Devices.Abstracts
{
    /// <summary>
    /// The Netatmo app every Netatmo device signs in with. Devices of one app share its tokens. The tokens come from
    /// connecting the device in the UI; the token parameters are the fallback for setups without it.
    /// </summary>
    public class NetatmoOptions
    {
        [DeviceParameter("clientId", Required = true, Description = "Client id of your Netatmo app (dev.netatmo.com)")]
        public string ClientId { get; set; } = "";

        [DeviceParameter("clientSecret", Required = true, Secret = true, Description = "Client secret of your Netatmo app")]
        public string ClientSecret { get; set; } = "";

        [DeviceParameter("token", Secret = true, Description = "Optional: an access token from Netatmo's token generator. Not needed when the device is connected to Netatmo")]
        public string Token { get; set; } = "";

        [DeviceParameter("refresh_token", Secret = true, Description = "Optional: a refresh token from Netatmo's token generator. Not needed when the device is connected to Netatmo")]
        public string RefreshToken { get; set; } = "";
    }

    /// <summary>
    /// A device that reads the Netatmo Connect API. It signs in with an OAuth authorization code from the UI
    /// (<see cref="IDeviceWithAuthorization"/>), calls Netatmo through <see cref="NetatmoApiClient"/>, and keeps
    /// running while Netatmo can't be used: the state message says why, and the next refresh tries again.
    /// </summary>
    public abstract class NetatmoBase<TOptions> : AsyncDeviceBase<TOptions>, IDeviceWithAuthorization where TOptions : NetatmoOptions, new()
    {
        internal const string SetupUrl = "https://dev.netatmo.com/apps/";

        /// <summary>How long a device waits after Netatmo limited its calls.</summary>
        internal static readonly TimeSpan ThrottleWait = TimeSpan.FromMinutes(15);

        private readonly INetatmoAuthClientFactory _authClients;
        private NetatmoAuthClient? _auth;
        private NetatmoApiClient? _api;
        private DateTimeOffset _backoffUntil;

        protected NetatmoBase(ILogger logger, INetatmoAuthClientFactory authClients) : base(logger)
        {
            _authClients = authClients ?? throw new ArgumentNullException(nameof(authClients));
        }

        /// <summary>The OAuth scopes the device needs; read after its options are bound.</summary>
        protected abstract IReadOnlyCollection<string> Scopes { get; }

        /// <summary>The clock; the tests replace it.</summary>
        internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

        internal NetatmoApiClient Api => _api ?? throw new DeviceNotReadyException("The Netatmo device isn't configured yet.");

        internal string ClientId => _auth?.ClientId ?? "";

        /// <summary>Whether a refresh should wait because Netatmo limited the calls.</summary>
        internal bool IsBackingOff => Now() < _backoffUntil;

        /// <summary>Uses the shared token client of the options' client id; devices of the same Netatmo app share tokens.</summary>
        internal void ConfigureNetatmo()
        {
            var auth = _authClients.Get(Options.ClientId);
            auth.Configure(Options.Token, Options.RefreshToken, Options.ClientSecret);
            auth.RequireScopes(Scopes);
            _auth = auth;
            _api = new NetatmoApiClient(auth, SendAsync, Logger);
            _backoffUntil = default;
        }

        // The HTTP call; the lifecycle tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Web.SendAsync(request, cancellationToken: cancellationToken);

        public DeviceAuthorization Authorization => new()
        {
            Provider = "Netatmo",
            SetupUrl = SetupUrl,
            Instructions = "Once per Netatmo account: create an app at dev.netatmo.com, add the redirect URI shown here to it, " +
                "and enter the app's client id and client secret. Save the device, then connect it. Devices that use the same " +
                "app share its connection."
        };

        public DeviceAuthorizationStatus GetAuthorizationStatus()
        {
            var auth = _auth;
            if (auth == null || string.IsNullOrWhiteSpace(Options.ClientId) || !auth.HasClientSecret)
                return new DeviceAuthorizationStatus
                {
                    State = DeviceAuthorizationState.NotConfigured,
                    Message = "Enter the client id and client secret of your Netatmo app, and save the device."
                };
            if (!auth.HasTokens)
                return new DeviceAuthorizationStatus { State = DeviceAuthorizationState.NotConnected, Message = "Connect the device to Netatmo." };
            if (auth.Problem != null)
                return new DeviceAuthorizationStatus { State = DeviceAuthorizationState.Expired, Account = auth.Account, Message = auth.Problem };
            return new DeviceAuthorizationStatus { State = DeviceAuthorizationState.Connected, Account = auth.Account };
        }

        public Task<string> BeginAuthorizationAsync(string redirectUri, string state, CancellationToken cancellationToken)
        {
            var auth = ConfiguredAuth();
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new ArgumentException("The redirect URI must be an absolute http or https URL.", nameof(redirectUri));
            if (string.IsNullOrEmpty(state))
                throw new ArgumentException("A state is required.", nameof(state));
            return Task.FromResult(auth.BuildAuthorizeUrl(redirectUri, state));
        }

        public async Task CompleteAuthorizationAsync(string code, string redirectUri, CancellationToken cancellationToken)
        {
            var auth = ConfiguredAuth();
            if (string.IsNullOrEmpty(code))
                throw new ArgumentException("A code is required.", nameof(code));
            await auth.ExchangeCodeAsync(code, redirectUri, cancellationToken).ConfigureAwait(false);
            _backoffUntil = default;
            if (State == DeviceState.Running)
                await OnConnectedAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Called when a running device got new tokens: read what it shows.</summary>
        protected virtual Task OnConnectedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>A note for the state message while Netatmo works, for example about the webhook.</summary>
        protected virtual string? StatusNote => null;

        private NetatmoAuthClient ConfiguredAuth()
        {
            var auth = _auth;
            if (auth == null || string.IsNullOrWhiteSpace(Options.ClientId) || !auth.HasClientSecret)
                throw new DeviceNotReadyException("Enter the client id and client secret of your Netatmo app, and save the device first.");
            return auth;
        }

        /// <summary>
        /// Runs <paramref name="call"/>. When Netatmo can't be used (not connected, limited, refused or unavailable) the
        /// device keeps running with the reason as its state message, and false is returned; the next refresh tries
        /// again. A cancellation and any other error pass through.
        /// </summary>
        internal async Task<bool> CallNetatmoAsync(Func<CancellationToken, Task> call, CancellationToken cancellationToken)
        {
            try
            {
                await call(cancellationToken).ConfigureAwait(false);
                SetRunningMessage(StatusNote ?? "");
                return true;
            }
            catch (NetatmoNotConnectedException)
            {
                SetRunningMessage("Connect the device to Netatmo in its settings.");
            }
            catch (NetatmoApiException error) when (error.IsThrottled)
            {
                _backoffUntil = Now() + ThrottleWait;
                Logger.LogWarning("Netatmo limits the calls of {Device}: waiting {Minutes} minutes. {Error}", Name, ThrottleWait.TotalMinutes, error.Message);
                SetRunningMessage($"Netatmo limits the calls; trying again after {_backoffUntil.ToLocalTime():HH:mm}.");
            }
            catch (NetatmoApiException error) when (error.IsMissingScope || error.IsRejectedToken)
            {
                Logger.LogWarning("Netatmo refused the tokens of {Device}: {Error}", Name, error.Message);
                SetRunningMessage(_auth?.Problem ?? error.Message);
            }
            catch (NetatmoApiException error)
            {
                Logger.LogWarning("Netatmo is unavailable for {Device}: {Error}", Name, error.Message);
                SetRunningMessage(error.Message);
            }
            return false;
        }

        /// <summary>Shows <paramref name="message"/> while the device runs; never revives a stopped device.</summary>
        internal void SetRunningMessage(string message)
        {
            if (State == DeviceState.Running && StateMessage != message)
                SetState(DeviceState.Running, message);
        }

        /// <summary>The report template at <paramref name="address"/>, matched case-insensitively.</summary>
        internal ReportTemplate? ReportAt(string address) =>
            ReportTemplates?.FirstOrDefault(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));

        /// <summary>Sends a value when it changed; numbers within the template's <c>Threshold</c> parameter count as unchanged.</summary>
        internal void SendState(string address, object? value, long? time = null)
        {
            if (value == null)
                return;
            var template = ReportAt(address);
            if (template == null)
                return;

            double threshold = 0;
            if (template.Parameters != null && template.Parameters.TryGetValue("Threshold", out var configured))
                double.TryParse(configured, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out threshold);

            SendReportIfValueChanged(this, new Report
            {
                Id = template.Id,
                TimeStamp = time ?? Now().ToUnixTimeSeconds(),
                Value = new ValueModel(value),
                Filter = ""
            }, threshold);
        }
    }
}
