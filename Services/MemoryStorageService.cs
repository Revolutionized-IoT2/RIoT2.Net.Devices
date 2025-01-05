using RIoT2.Core.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services
{
    public class MemoryStorageService : IMemoryStorageService
    {
        private List<MemoryStorageAddess> _memoryStorageAddresses;
        private readonly int _maxDocumentCount = 10;

        public MemoryStorageService() 
        {
            _memoryStorageAddresses = new List<MemoryStorageAddess>();
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

        public void Save(Document document, string address)
        {
            var existingAddress = _memoryStorageAddresses.FirstOrDefault(x => x.Address == address);
            if (existingAddress == default)
            {
                _memoryStorageAddresses.Add(new MemoryStorageAddess() 
                {
                    Address = address,
                    Documents = new List<Document>() { document }
                });
            }
            else 
            {
                if (existingAddress.Documents.Count > (_maxDocumentCount - 1))
                    existingAddress.Documents.RemoveAt(0);

                existingAddress.Documents.Add(document);
            }
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
    }

    public class MemoryStorageAddess 
    {
        public string Address { get; set; }
        public List<Document> Documents { get; set; }
    }
}