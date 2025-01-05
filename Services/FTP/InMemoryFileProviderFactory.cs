using Zhaobang.FtpServer.File;

namespace RIoT2.Net.Devices.Services.FTP
{
    internal class InMemoryFileProviderFactory : IFileProviderFactory
    {
        public IFileProvider GetProvider(string username)
        {
            return new InMemoryFileProvider(username);
        }
    }
}