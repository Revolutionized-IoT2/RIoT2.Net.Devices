using FluentFTP;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Models;
using RIoT2.Core;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services
{
    public class FTPStorageService : IStorageService
    {
        private FtpClient _client;
        private string _rootFolder;
        private bool _configured = false;
        private ILogger<FTPStorageService> _logger;

        public FTPStorageService(ILogger<FTPStorageService> logger)
        {
            _logger = logger;
        }

        public void Configure(string username, string password, string rootFolder, string ipAddress)
        {
            _client = new FtpClient(ipAddress, username, password);
            _rootFolder = rootFolder;
            _configured = true;
        }

        public async Task Delete(string filename)
        {
            await Task.Factory.StartNew(() =>
            {
                try
                {
                    _client.Connect();

                    var file = $"_rootFolder/{filename}";
                    if (_client.FileExists(file))
                    {
                        _client.DeleteFile(file);
                    }

                    _client.Disconnect();
                }
                catch (Exception x) 
                {
                    _logger.LogError(x, "Could not delete file from FTP storage");
                }
            });
        }

        public async Task<Document> Get(string filename)
        {
            Document doc = null;

            await Task.Factory.StartNew(() =>
            {
                try
                {
                    _client.Connect();

                    var file = $"_rootFolder/{filename}";
                    if (_client.FileExists(file))
                    {

                        byte[] bytes;
                        _client.DownloadBytes(out bytes, file);
                        var info = _client.GetObjectInfo(file);

                        doc = new Document()
                        {
                            Data = bytes,
                            Filename = info.Name,
                            Isfolder = FileOrFolder.File,
                            Filesize = info.Size.ToString(),
                            Epochmt = info.Created.ToEpoch(),
                            Filetype = DocumentType.Undefined
                        };
                    }
                }
                catch (Exception x)
                {
                    _logger.LogError(x, "Could not download file from FTP storage");
                }
                finally 
                {
                    if (_client.IsConnected)
                        _client.Disconnect();
                }
            });

            return doc;
        }

        public bool IsConfigured()
        {
            return _configured;
        }

        public async Task<List<DocumentMetadata>> List()
        {
            List<DocumentMetadata> metadata = new List<DocumentMetadata>();
            try
            {
                await Task.Factory.StartNew(() =>
                {
                    _client.Connect();

                    foreach (FtpListItem item in _client.GetListing(_rootFolder))
                    {
                        if (item.Type == FtpObjectType.File)
                        {
                            var info = _client.GetObjectInfo(item.FullName);
                            metadata.Add(new DocumentMetadata()
                            {
                                Filename = info.Name,
                                Isfolder = FileOrFolder.File,
                                Filesize = info.Size.ToString(),
                                Epochmt = info.Created.ToEpoch(),
                                Filetype = DocumentType.Undefined
                            });
                        }
                    }
                });
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not list files from FTP storage");
            }
            finally
            {
                if (_client.IsConnected)
                    _client.Disconnect();
            }
            return metadata;
        }

        public async Task Save(string filename, byte[] data)
        {
            await Task.Factory.StartNew(() =>
            {
                try
                {
                    var file = $"_rootFolder/{filename}";
                    _client.Connect();
                    _client.UploadBytes(data, file);
                   
                }
                catch (Exception x)
                {
                    _logger.LogError(x, "Could not save file to FTP storage");
                }
                finally
                {
                    if (_client.IsConnected)
                        _client.Disconnect();
                }
            });
        }
    }
}