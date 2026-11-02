using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.FTP;
using RIoT2.Net.Devices.Services.Interfaces;


namespace RIoT2.Net.Devices.Catalog
{

    /// <summary>The <see cref="FTP"/> device's parameters.</summary>
    public sealed class FtpOptions
    {
        [DeviceParameter("ftpUsers", Secret = true, Description = "Accounts the cameras log in with: user:password pairs separated by '|'; the user name is a report template's address")]
        public string Users { get; set; } = "";

        [DeviceParameter("ftpPort", Default = "21", Description = "Port the FTP server listens on")]
        public int Port { get; set; }
    }

    /// <summary>
    /// This device is used to receive files from FTP (WebCameras forinstance)
    /// </summary>
    [RequiresHardware("Listens on an FTP port")]
    public class FTP : DeviceBase<FtpOptions>, IDeviceWithConfiguration
    {
        private List<FtpUser> _ftpUsers;
        private IFtpService _ftpService;
        private IMemoryStorageService _memoryStorageService;

        public FTP(ILogger logger, IFtpService ftpService, IMemoryStorageService memoryStorageService) : base(logger) 
        {
            _ftpService = ftpService;
            _memoryStorageService = memoryStorageService;
        }
        public DeviceConfiguration GetConfigurationTemplate() => new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = "FTP",
            ClassFullName = GetType().FullName,
            // The node adds the parameters from FtpOptions.
            ReportTemplates = []
        };

        public override void ConfigureDevice()
        {
            _ftpUsers = new List<FtpUser>();
            var ftpUsers = Options.Users;

            if (!String.IsNullOrEmpty(ftpUsers))
            {
                var users = ftpUsers.Split("|");
                foreach (string s in users)
                {
                    var usr = s.Split(":");
                    if (usr.Length == 2)
                    {
                        _ftpUsers.Add(new FtpUser()
                        {
                            UserName = usr[0],
                            Password = usr[1]
                        });
                    }
                }
            }
        }

        public override void StartDevice()
        {
            _ftpService.StartAsync(_ftpUsers, Options.Port);
            _ftpService.FileReceived += _ftpService_FileReceived;
        }

        public override void StopDevice()
        {
            _ftpService.Stop();
            _ftpService.FileReceived -= _ftpService_FileReceived;
        }

        private void _ftpService_FileReceived(InMemoryStream inMemoryStream)
        {
            //each camera has own username...
            var report = ReportTemplates?.FirstOrDefault(x => x.Address.ToLower() == inMemoryStream.Username.ToLower());
            if (report == null)
                return;

            var fileGuid = Guid.NewGuid().ToString(); //TODO use file name extension?
                                                      //TODO process image -> classification

            //TODO -> TO memeory service -> only rule will save to actual fileservice if needed.
            //_fileService.Save(fileGuid, inMemoryStream.ToArray());

            Document d = new Document() {
                Data = inMemoryStream.ToArray(),
                Epochmt = DateTime.UtcNow.ToEpoch(),
                Filename = fileGuid,
                Isfolder = FileOrFolder.File,
                Filetype = DocumentType.Photo,
                Filesize = inMemoryStream.Length.ToString(),
                Properties = new Dictionary<string, string> {
                    { "original_filename", inMemoryStream.Filename },
                    { "username", inMemoryStream.Username }
                }
            };

            var url = _memoryStorageService.Save(d, inMemoryStream.Username.ToLower());

            //Image Url -> full url for d

            var securityReport = new SecurityReport()
            {
                Source = "ftp",
                ImageUrl = url,
                SecurityEvent = SecurityEventType.motionDetected,
                EventValue = inMemoryStream.Filename,
                Message = ""
            };

            SendReport(this, new Report()
            {
                Id = report.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(securityReport),
                Filter = "image"
            });

            inMemoryStream.Dispose();
        }
    }
}
