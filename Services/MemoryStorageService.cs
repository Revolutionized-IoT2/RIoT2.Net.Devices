using Microsoft.Extensions.Logging;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services
{
    public class MemoryStorageService : IMemoryStorageService
    {
        private readonly List<MemoryStorageAddess> _memoryStorageAddresses = [];
        private readonly int _maxDocumentCount = 10;
        private readonly string _baseUrl = "";
        private readonly ILogger<MemoryStorageService> _logger;

        public MemoryStorageService(ILogger<MemoryStorageService> logger, INodeConfigurationService nodeConfigurationService) 
        {
            _logger = logger;
            _baseUrl = nodeConfigurationService.Configuration.Url;
            if (_baseUrl.EndsWith("/"))
                _baseUrl = _baseUrl.TrimEnd('/');
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
                _logger.LogInformation("New Address {Address} created in memory storage. With initial document {Filename}", address, document.Filename);
            }
            else 
            {
                if (existingAddress.Documents.Count > (_maxDocumentCount - 1))
                    existingAddress.Documents.RemoveAt(0);

                existingAddress.Documents.Add(document);
                _logger.LogInformation("Document with filename {Filename} saved to memory storage under address {Address}.", document.Filename, address);
            }
            return downloadUrl;
        }

        public void DeleteAddress(string address)
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