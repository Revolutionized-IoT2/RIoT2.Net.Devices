using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.FTP;
using RIoT2.Net.Devices.Services.Interfaces;


namespace RIoT2.Net.Devices.Catalog
{

    /// <summary>
    /// This device is used to receive files from FTP (WebCameras forinstance)
    /// </summary>
    public class FTP : DeviceBase, IDevice
    {
        private List<FtpUser> _ftpUsers;
        private int _ftpPort = 0;
        private IFtpService _ftpService;
        private IDownloadService _downloadService;
        private IMemoryStorageService _memoryStorageService;

        public FTP(ILogger logger, IFtpService ftpService, IDownloadService downloadService, IMemoryStorageService memoryStorageService) : base(logger) 
        {
            _ftpService = ftpService;
            _downloadService = downloadService;
            _memoryStorageService = memoryStorageService;
        }
        public override void ConfigureDevice()
        {
            _ftpUsers = new List<FtpUser>();
            var ftpUsers = GetConfiguration<string>("ftpUsers");

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
            _ftpPort = GetConfiguration<int> ("ftpPort");

            /* we dont 
            if (!_fileService.IsConfigured())
            {
                var ip = GetConfiguration<string>("StorageIp");
                var user = GetConfiguration<string>("StorageUser");
                var pw = GetConfiguration<string>("StoragePassword");
                var folder = GetConfiguration<string>("StorageFolder");

                _fileService.Configure(user, pw, folder, ip);
            }*/
        }

        public override void StartDevice()
        {
            _ftpService.StartAsync(_ftpUsers, _ftpPort);
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
            var report = ReportTemplates.FirstOrDefault(x => x.Address.ToLower() == inMemoryStream.Username.ToLower());
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

            _memoryStorageService.Save(d, inMemoryStream.Username.ToLower());

            //Image Url -> full url for d

            var securityReport = new SecurityReport()
            {
                Source = "ftp",
                ImageUrl = _downloadService.GetDownloadUrl(fileGuid),
                SecurityEvent = SecurityEventType.Movement,
                Subject = inMemoryStream.Filename,
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
