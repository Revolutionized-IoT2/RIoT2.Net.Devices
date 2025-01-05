using RIoT2.Core.Models;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    public interface IStorageService
    {
        Task Save(string filename, byte[] data);
        Task<Document> Get(string filename);
        Task<List<DocumentMetadata>> List();
        Task Delete(string filename);
        void Configure(string username, string password, string rootFolder, string ipAddress);
        bool IsConfigured();
    }
}