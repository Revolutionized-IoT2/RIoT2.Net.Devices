using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DownloadController : ControllerBase
    {
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
            var img = _memoryStorageService.Get(filename);

            if (img == null && _fileService.IsConfigured())
                img = await _fileService.Get(filename);

            if (img == null) 
            {
                return Results.NotFound();
            }

            return Results.File(img.Data, "image/jpeg");
        }

        [HttpGet]
        [Route("list/files")]
        public IResult ListFiles()
        {
            return Results.Ok(_memoryStorageService.GetAllDocuments());
        }
    }
}