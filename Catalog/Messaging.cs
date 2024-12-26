using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;
using System.Net.Mail;
using System.Net;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Utils;
using RIoT2.Core.Models;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    internal class Messaging : DeviceBase, ICommandDevice, IDeviceWithConfiguration
    {
        private GoogleOAuth2 _authentication;
        private SmtpClient _smtpClient;
        //private string _applicationFolder;

        private string firebaseProjectName;
        private string smtp_Server;
        private string smtp_Port;
        private string smtp_User;
        private string smtp_Password;
        private const string _serviceAccountFile = "Data/service_account.json";

        internal Messaging(ILogger logger) : base(logger) 
        {
    
        }

        public void ExecuteCommand(string commandId, string value)
        {
            Logger.LogInformation("Executed command: {commandId}", commandId);

            var command = CommandTemplates.FirstOrDefault(x => x.Id == commandId);
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
                sendMail(msg.GetMailMessage(smtp_User));
            }
        }

        public override void ConfigureDevice()
        {
            //configure device parameters
            firebaseProjectName = "riot-184512";

            firebaseProjectName = GetConfiguration<string>("firebaseProjectName");
            smtp_Server = GetConfiguration<string>("smtp_Server");
            smtp_Password = GetConfiguration<string>("smtp_Password");
            smtp_User = GetConfiguration<string>("smtp_User");
            smtp_Port = GetConfiguration<string>("smtp_Port");

            //configure device
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
            deviceConfiguration.Name = "Messaging device";
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("firebaseProjectName", "riot-184512");
            deviceConfiguration.DeviceParameters.Add("smtp_Server", "riot-184512");
            deviceConfiguration.DeviceParameters.Add("smtp_User", "riot-184512");
            deviceConfiguration.DeviceParameters.Add("smtp_Password", "riot-184512");
            deviceConfiguration.DeviceParameters.Add("smtp_Port", "riot-184512");

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

            deviceConfiguration.ReportTemplates = new List<ReportTemplate>();
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
            if (_authentication == null || String.IsNullOrEmpty(firebaseProjectName))
                return null;

            try
            {
                var data = Json.Serialize(msg);
                var headers = new Dictionary<string, string>();
                headers.Add("Authorization", "Bearer " + await _authentication.GetToken());

                var httpResult = await Core.Utils.Web.PostAsync($"https://fcm.googleapis.com/v1/projects/{firebaseProjectName}/messages:send", data, headers);
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
            if (String.IsNullOrEmpty(smtp_Server) ||
                String.IsNullOrEmpty(smtp_Port) ||
                String.IsNullOrEmpty(smtp_User) ||
                String.IsNullOrEmpty(smtp_Password))
                return false;

            int port;
            if (!Int32.TryParse(smtp_Port, out port))
                return false;

            _smtpClient = new SmtpClient();
            _smtpClient.Host = smtp_Server;
            _smtpClient.Port = port;
            _smtpClient.UseDefaultCredentials = false;
            _smtpClient.DeliveryMethod = SmtpDeliveryMethod.Network;
            _smtpClient.EnableSsl = true;
            _smtpClient.Credentials = new NetworkCredential(smtp_User, smtp_Password);

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
                    await SourceStream.ReadAsync(result, 0, (int)SourceStream.Length);
                }

                return System.Text.Encoding.UTF8.GetString(result);
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Could not load service_account.json");
            }
            return null;
        }
    }
}
