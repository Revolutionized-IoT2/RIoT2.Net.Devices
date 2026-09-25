using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DownloadController : ControllerBase
    {
        private static readonly string DownloadBaseDirectory =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data", "downloads"));
        private IStorageService _fileService;
        private IMemoryStorageService _memoryStorageService;

        public DownloadController(IStorageService fileService, IMemoryStorageService memoryStorageService)
        {
            _fileService = fileService;
            _memoryStorageService = memoryStorageService;
        }

        [HttpGet]
        [Route("{filename}")]
        public async Task<IResult> GetFileAsync(string filename)
        {
            if (!TryNormalizeFilename(filename, out var safeFilename))
                return Results.BadRequest("Invalid filename.");

            var img = _memoryStorageService.Get(safeFilename);

            if (img == null && _fileService.IsConfigured())
                img = await _fileService.Get(safeFilename);

            if (img == null) 
            {
                return Results.NotFound();
            }

            return Results.File(img.Data, "image/jpeg");
        }

        internal static bool TryNormalizeFilename(string filename, out string safeFilename)
        {
            safeFilename = null;
            if (string.IsNullOrWhiteSpace(filename))
                return false;

            string decoded;
            try { decoded = DecodeRepeatedly(filename); }
            catch { return false; }

            var baseDirectory = EnsureTrailingSeparator(DownloadBaseDirectory);
            var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, decoded));
            if (!fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(Path.GetFileName(fullPath), decoded, StringComparison.Ordinal))
                return false;

            safeFilename = decoded;
            return true;
        }

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

        private static string DecodeRepeatedly(string value)
        {
            for (var i = 0; i < 3; i++)
            {
                var decoded = Uri.UnescapeDataString(value);
                if (decoded == value)
                    return decoded;
                value = decoded;
            }
            return value;
        }

        [HttpGet]
        [Route("list/files")]
        public IResult ListFiles()
        {
            return Results.Ok(_memoryStorageService.GetAllDocuments());
        }
    }
}