using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Controllers;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class DownloadControllerTests
{
    [DataTestMethod]
    [DataRow("..%2Fsecret.jpg")]
    [DataRow("..%5Csecret.jpg")]
    [DataRow("C:%5CWindows%5Cwin.ini")]
    [DataRow("%252e%252e%252fsecret.jpg")]
    public async Task DownloadRejectsTraversalAndAbsoluteFilenames(string filename)
    {
        var storage = new RecordingMemoryStorage();
        var controller = new DownloadController(new NullStorage(), storage);

        var result = await controller.GetFileAsync(filename);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.AreEqual(0, storage.Requested.Count);
    }

    [TestMethod]
    public async Task DownloadUsesNormalizedSafeFilename()
    {
        var storage = new RecordingMemoryStorage();
        var controller = new DownloadController(new NullStorage(), storage);

        var result = await controller.GetFileAsync("camera.jpg");

        Assert.AreEqual(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
        CollectionAssert.AreEqual(new[] { "camera.jpg" }, storage.Requested);
    }

    [TestMethod]
    public void WebhookHasRequestSizeLimit()
    {
        var attribute = typeof(WebhookController).GetMethod(nameof(WebhookController.SaveAsync))
            .GetCustomAttributes(typeof(RequestSizeLimitAttribute), inherit: false)
            .Cast<RequestSizeLimitAttribute>()
            .Single();

        var constructorArgument = typeof(WebhookController).GetMethod(nameof(WebhookController.SaveAsync))
            .GetCustomAttributesData()
            .Single(attributeData => attributeData.AttributeType == typeof(RequestSizeLimitAttribute))
            .ConstructorArguments
            .Single();

        Assert.AreEqual(65536L, constructorArgument.Value);
    }

    private sealed class RecordingMemoryStorage : IMemoryStorageService
    {
        public List<string> Requested { get; } = [];
        public string Save(Document document, string address) => "";
        public Document Get(string filename, string address = "") { Requested.Add(filename); return null; }
        public Document GetLatest(string address) => null;
        public List<DocumentMetadata> List(string address) => [];
        public void DeleteAddress(string address) { }
        public List<MemoryStorageAddess> GetAllDocuments() => [];
    }

    private sealed class NullStorage : IStorageService
    {
        public Task Save(string filename, byte[] data) => Task.CompletedTask;
        public Task<Document> Get(string filename) => Task.FromResult<Document>(null);
        public Task<List<DocumentMetadata>> List() => Task.FromResult(new List<DocumentMetadata>());
        public Task Delete(string filename) => Task.CompletedTask;
        public void Configure(string username, string password, string rootFolder, string ipAddress) { }
        public bool IsConfigured() => false;
    }
}
