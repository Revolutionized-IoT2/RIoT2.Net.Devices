using Microsoft.Extensions.Logging;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services
{
    public class MemoryStorageService : IMemoryStorageService
    {
        private List<MemoryStorageAddess> _memoryStorageAddresses;
        private INodeConfigurationService _nodeConfiguration;
        private readonly int _maxDocumentCount = 10;
        private string _baseUrl = "";
        private readonly ILogger<MemoryStorageService> _logger;

        public MemoryStorageService(ILogger<MemoryStorageService> logger, INodeConfigurationService nodeConfigurationService) 
        {
            _logger = logger;
            _nodeConfiguration = nodeConfigurationService;
             setBaseUrl(_nodeConfiguration.Configuration.Url);
            _memoryStorageAddresses = [];
        }

        public Document Get(string filename, string address = "")
        {
            if (address != "")
            {
                var add = _memoryStorageAddresses.FirstOrDefault(x => x.Address == address);
                if (add != null)
                {
                    var doc = add.Documents.FirstOrDefault(x => x.Filename == filename);
                    if (doc != null)
                        return doc;
                }
            }
            else 
            {
                foreach (var a in _memoryStorageAddresses)
                {
                    var doc = a.Documents.FirstOrDefault(x => x.Filename == filename);
                    if (doc != null)
                        return doc;

                }
            }
            _logger.LogWarning("Document with filename {Filename} not found in memory storage.", filename);
            return null;
        }

        public Document GetLatest(string address)
        {
            var existingAddress = _memoryStorageAddresses.FirstOrDefault(x => x.Address == address);
            if (existingAddress != default && existingAddress.Documents.Count > 0)
            {
                return existingAddress.Documents.Last();
            }
            return null;
        }

        public List<DocumentMetadata> List(string address)
        {
            var existingAddress = _memoryStorageAddresses.FirstOrDefault(x => x.Address == address);
            if (existingAddress != default && existingAddress.Documents.Count > 0)
            {
                return existingAddress.Documents.Cast<DocumentMetadata>().ToList();
            }
            return null;
        }

        public string Save(Document document, string address)
        {
            var downloadUrl = $"{_baseUrl}/api/download/{document.Filename}";
            var existingAddress = _memoryStorageAddresses.FirstOrDefault(x => x.Address == address);
            if (existingAddress == default)
            {
                _memoryStorageAddresses.Add(new MemoryStorageAddess() 
                {
                    Address = address,
                    Documents = [document]
                });
            }
            else 
            {
                if (existingAddress.Documents.Count > (_maxDocumentCount - 1))
                    existingAddress.Documents.RemoveAt(0);

                existingAddress.Documents.Add(document);
            }
            return downloadUrl;
        }

        public void Format(string address)
        {
            var existingAddress = _memoryStorageAddresses.FirstOrDefault(x => x.Address == address);
            if (existingAddress != default)
            {
                existingAddress.Documents = new List<Document>();
            }
            else 
            {
                _memoryStorageAddresses.Remove(existingAddress);
            }
        }

        private void setBaseUrl(string url)
        {
            _baseUrl = url;
            if (_baseUrl.EndsWith("/"))
                _baseUrl = _baseUrl.TrimEnd('/');
        }

        public List<MemoryStorageAddess> GetAllDocuments()
        {
            return _memoryStorageAddresses;
        }
    }

    public class MemoryStorageAddess 
    {
        public string Address { get; set; }
        public List<Document> Documents { get; set; }
    }
}