using RIoT2.Core.Models;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    public interface IMemoryStorageService
    {
        void Save(Document document, string address);
        Document Get(string filename, string address = "");
        Document GetLatest(string address);
        List<DocumentMetadata> List(string address);
        void Format(string address);

    }
}