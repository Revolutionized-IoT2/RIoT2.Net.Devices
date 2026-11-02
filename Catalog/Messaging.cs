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

    public class Messaging : DeviceBase<MessagingOptions>, ICommandDevice, IDeviceWithConfiguration
    {
        private GoogleOAuth2 _authentication;
        private SmtpClient _smtpClient;
        private const string _serviceAccountFile = "Data/service_account.json";

        public Messaging(ILogger logger) : base(logger) 
        {
    
        }

        public void ExecuteCommand(string commandId, string value)
        {
            Logger.LogInformation("Executed command: {commandId}", commandId);

            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            if (command.Address.ToLower() == "fb")
            {
                var msg = Json.Deserialize<FirebaseMsg>(value);
                sendFBMessage(msg.GetServiceMessage()).Wait();
            }
            else if (command.Address.ToLower() == "mail") 
            {
                var msg = Json.Deserialize<MailMsg>(value);
                sendMail(msg.GetMailMessage(Options.SmtpUser));
            }
        }

        public override void ConfigureDevice()
        {
            _smtpClient = null;
            configureSmtpClient();

            var serviceAccountJson = readServiceAccount().Result;

            if (!String.IsNullOrEmpty(serviceAccountJson))
                _authentication = new GoogleOAuth2(serviceAccountJson);
        }

        public override void StartDevice()
        {
            //var temp = _authentication.GetToken().Result;
            //no actions needed...
        }

        public override void StopDevice()
        {
            //no actions needed...
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Messaging";
            // The node adds the parameters from MessagingOptions.

            deviceConfiguration.ClassFullName = this.GetType().FullName;
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

            deviceConfiguration.ReportTemplates = null;
            deviceConfiguration.CommandTemplates = commandConfigurations;

            return deviceConfiguration;
        }

        private void sendMail(MailMessage msg)
        {
            try
            {
                if (_smtpClient != null)
                    _smtpClient.Send(msg);
            }
            catch (Exception x)
            {
                Logger.LogError(x, "Error while sending mail");
            }
        }
        private async Task<string> sendFBMessage(FirebaseServiceMessage msg)
        {
            string retVal = null;
            if (_authentication == null || String.IsNullOrEmpty(Options.FirebaseProjectName))
                return null;

            try
            {
                var data = Json.Serialize(msg);
                var headers = new Dictionary<string, string>();
                headers.Add("Authorization", "Bearer " + await _authentication.GetToken());

                var httpResult = await Core.Utils.Web.PostAsync($"https://fcm.googleapis.com/v1/projects/{Options.FirebaseProjectName}/messages:send", data, headers);
                if (httpResult.StatusCode == System.Net.HttpStatusCode.OK)
                    retVal = data;
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
        private async Task<string> readServiceAccount() 
        {
            try
            {
                byte[] result;
                if(!File.Exists(_serviceAccountFile))
                    return null;
                
                using (FileStream SourceStream = File.Open(_serviceAccountFile, FileMode.Open))
                {
                    result = new byte[SourceStream.Length];
                    await SourceStream.ReadExactlyAsync(result);
                }

                return System.Text.Encoding.UTF8.GetString(result);
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Could not load service_account.json");
                throw new Exception("Could not load service_account.json");
            }
        }
    }
}
