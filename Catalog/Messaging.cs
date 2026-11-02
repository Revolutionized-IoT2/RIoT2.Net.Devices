using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;
using System.Net.Mail;
using System.Net;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Utils;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="Messaging"/> device's parameters. Both channels are optional.</summary>
    public sealed class MessagingOptions
    {
        [DeviceParameter("firebaseProjectName", Description = "Firebase project for push messages; needs Data/service_account.json on the node")]
        public string FirebaseProjectName { get; set; } = "";

        [DeviceParameter("smtp_Server", Description = "SMTP server for mail (TLS); leave empty to send no mail")]
        public string SmtpServer { get; set; } = "";

        [DeviceParameter("smtp_Port", Default = "587", Description = "SMTP port")]
        public int SmtpPort { get; set; }

        [DeviceParameter("smtp_User", Description = "SMTP user, also the sender address")]
        public string SmtpUser { get; set; } = "";

        [DeviceParameter("smtp_Password", Secret = true, Description = "SMTP password")]
        public string SmtpPassword { get; set; } = "";
    }

    public class Messaging : AsyncDeviceBase<MessagingOptions>, IAsyncCommandDevice, IDeviceWithConfiguration
    {
        private GoogleOAuth2? _authentication;
        private SmtpClient? _smtpClient;
        private const string _serviceAccountFile = "Data/service_account.json";

        public Messaging(ILogger logger) : base(logger) 
        {
    
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            Logger.LogInformation("Executed command: {commandId}", commandId);

            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            if (command.Address.ToLower() == "fb")
            {
                // An empty or "null" value fails here, as before.
                var msg = Json.Deserialize<FirebaseMsg>(value)!;
                await sendFBMessage(msg.GetServiceMessage(), cancellationToken).ConfigureAwait(false);
            }
            else if (command.Address.ToLower() == "mail") 
            {
                var msg = Json.Deserialize<MailMsg>(value)!;
                await sendMail(msg.GetMailMessage(Options.SmtpUser), cancellationToken).ConfigureAwait(false);
            }
        }

        public override void ConfigureDevice()
        {
            _smtpClient = null;
            configureSmtpClient();

            var serviceAccountJson = readServiceAccount();

            if (!String.IsNullOrEmpty(serviceAccountJson))
                _authentication = new GoogleOAuth2(serviceAccountJson);
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task StopDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) => Task.CompletedTask;

        // The calls to Firebase and the SMTP server; the lifecycle tests replace them.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Core.Utils.Web.SendAsync(request, cancellationToken: cancellationToken);

        internal virtual Task SendMailAsync(SmtpClient client, MailMessage message, CancellationToken cancellationToken) =>
            client.SendMailAsync(message, cancellationToken);

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Messaging";
            // The node adds the parameters from MessagingOptions.

            deviceConfiguration.ClassFullName = this.GetType().FullName!;
            var commandConfigurations = new List<CommandTemplate>();

            commandConfigurations.Add(new CommandTemplate() {
                Id = Guid.NewGuid().ToString(),
                Address = "fb",
                Name = "Send firebase message",
                Type = ValueType.Entity,
                Model = new FirebaseMsg() { 
                    Title = "message title",
                    Body = "message body",
                    ImgUrl = "optional full image url",
                    Topic = "alerts | notifications"
                }
            });

            commandConfigurations.Add(new CommandTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "mail",
                Name = "Send mail message",
                Type = ValueType.Entity,
                Model = new MailMsg()
                {
                    Body = "message body",
                    To = "somebody@somewhare.com",
                    Subject = "message subject"
                }
            });

            // Messaging has no reports; its template has always carried null here.
            deviceConfiguration.ReportTemplates = null!;
            deviceConfiguration.CommandTemplates = commandConfigurations;

            return deviceConfiguration;
        }

        private async Task sendMail(MailMessage msg, CancellationToken cancellationToken)
        {
            try
            {
                if (_smtpClient != null)
                    await SendMailAsync(_smtpClient, msg, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception x)
            {
                Logger.LogError(x, "Error while sending mail");
            }
        }
        private async Task<string?> sendFBMessage(FirebaseServiceMessage msg, CancellationToken cancellationToken)
        {
            string? retVal = null;
            if (_authentication == null || String.IsNullOrEmpty(Options.FirebaseProjectName))
                return null;

            try
            {
                var data = Json.Serialize(msg);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Options.FirebaseProjectName}/messages:send")
                {
                    Content = new StringContent(data, System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await _authentication.GetToken().ConfigureAwait(false));

                using var httpResult = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (httpResult.StatusCode == System.Net.HttpStatusCode.OK)
                    retVal = data;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception x)
            {
                Logger.LogError(x, "Error while sending Firebase message");
            }
            return retVal;
        }
        private bool configureSmtpClient()
        {
            if (String.IsNullOrEmpty(Options.SmtpServer) ||
                String.IsNullOrEmpty(Options.SmtpUser) ||
                String.IsNullOrEmpty(Options.SmtpPassword))
                return false;

            _smtpClient = new SmtpClient();
            _smtpClient.Host = Options.SmtpServer;
            _smtpClient.Port = Options.SmtpPort;
            _smtpClient.UseDefaultCredentials = false;
            _smtpClient.DeliveryMethod = SmtpDeliveryMethod.Network;
            _smtpClient.EnableSsl = true;
            _smtpClient.Credentials = new NetworkCredential(Options.SmtpUser, Options.SmtpPassword);

            return true;
        }
        // ConfigureDevice is synchronous; a small local file is read directly.
        private string? readServiceAccount() 
        {
            try
            {
                if(!File.Exists(_serviceAccountFile))
                    return null;

                return File.ReadAllText(_serviceAccountFile, System.Text.Encoding.UTF8);
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Could not load service_account.json");
                throw new Exception("Could not load service_account.json");
            }
        }
    }
}
