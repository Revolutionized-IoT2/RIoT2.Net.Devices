using Zhaobang.FtpServer.File;

namespace RIoT2.Net.Devices.Services.FTP
{
    internal class InMemoryFileProvider : IFileProvider
    {
        private readonly string _username;

        public InMemoryFileProvider(string username)
        {
            _username = username;
        }

        public Task CreateDirectoryAsync(string path)
        {
            return Task.CompletedTask;
            //throw new NotImplementedException();
        }

        public async Task<Stream> CreateFileForWriteAsync(string path)
        {
            InMemoryStream inMemoryStream = new InMemoryStream(_username, path);

            return await Task.FromResult(inMemoryStream);
        }

        public Task DeleteAsync(string path)
        {
            return Task.CompletedTask;
            //throw new NotImplementedException();
        }

        public Task DeleteDirectoryAsync(string path)
        {
            return Task.CompletedTask;
            //throw new NotImplementedException();
        }

        public async Task<IEnumerable<FileSystemEntry>> GetListingAsync(string path)
        {
            return await Task.FromResult(new List<FileSystemEntry>());
        }

        public Task<IEnumerable<string>> GetNameListingAsync(string path)
        {
            throw new NotImplementedException();
        }

        public string GetWorkingDirectory()
        {
            return "/";
        }

        public Task<Stream> OpenFileForReadAsync(string path)
        {
            throw new NotImplementedException();
        }

        public Task<Stream> OpenFileForWriteAsync(string path)
        {
            throw new NotImplementedException();
        }

        public Task RenameAsync(string fromPath, string toPath)
        {
            throw new NotImplementedException();
        }

        public bool SetWorkingDirectory(string path)
        {
            return true;
        }
    }
}