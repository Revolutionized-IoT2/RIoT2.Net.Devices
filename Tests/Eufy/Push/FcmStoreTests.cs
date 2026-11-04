using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.Push;

namespace RIoT2.Net.Devices.Tests.Eufy.Push;

[TestClass]
public class FcmStoreTests
{
    private string _dir = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _dir = Path.Combine(AppContext.BaseDirectory, "TestData", "eufy-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_dir, true);

    internal static FcmCredentials Creds() => new()
    {
        Fid = "fJ8x0000000000000000aa",
        AndroidId = "5123456789012345678",
        SecurityToken = "1234567890123456789",
        FcmToken = "fcm-token:abc",
        RefreshToken = "refresh",
        CreatedAt = 1700000000000,
    };

    [TestMethod]
    public async Task RoundTripsCredentialsAndPersistentIds()
    {
        var store = FileFcmStore.ForAccount(_dir, "User@Example.com");
        await store.SaveAsync(new PersistedPush { Creds = Creds(), PersistentIds = ["0:1", "0:2"] }, CancellationToken.None);

        var loaded = await FileFcmStore.ForAccount(_dir, "  user@example.COM ").LoadAsync(CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(Creds(), loaded.Creds);
        CollectionAssert.AreEqual(new[] { "0:1", "0:2" }, loaded.PersistentIds.ToArray());
        Assert.HasCount(1, Directory.GetFiles(_dir)); // the temporary file was moved, not left behind
    }

    [TestMethod]
    public void NamesTheFileByAHashOfTheEmailNeverTheEmail()
    {
        var name = FileFcmStore.FileNameFor("user@example.com");

        StringAssert.Matches(name, new System.Text.RegularExpressions.Regex("^eufy-push-[0-9a-f]{16}\\.json$"));
        Assert.DoesNotContain("user", name);
        Assert.AreEqual(name, FileFcmStore.FileNameFor(" USER@example.com "));
        Assert.AreNotEqual(name, FileFcmStore.FileNameFor("other@example.com"));
        // First 16 hex of SHA-256("user@example.com").
        Assert.AreEqual("eufy-push-b4c9a289323b21a0.json", name);
    }

    [TestMethod]
    public async Task WritesTheUpstreamJsonShape()
    {
        var store = FileFcmStore.ForAccount(_dir, "a@b.c");
        await store.SaveAsync(new PersistedPush { Creds = Creds(), PersistentIds = ["0:1"] }, CancellationToken.None);

        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(store.Path))!;
        Assert.AreEqual("5123456789012345678", json["creds"]!["androidId"]!.GetValue<string>());
        Assert.AreEqual("fcm-token:abc", json["creds"]!["fcmToken"]!.GetValue<string>());
        Assert.AreEqual("0:1", json["persistentIds"]![0]!.GetValue<string>());
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("{}")]
    [DataRow("{\"creds\":null,\"persistentIds\":[]}")]
    [DataRow("{\"creds\":{\"fid\":\"f\",\"androidId\":\"x\",\"securityToken\":\"1\",\"fcmToken\":\"t\",\"createdAt\":0},\"persistentIds\":[]}")]
    [DataRow("{\"creds\":{\"fid\":\"f\",\"androidId\":\"1\",\"securityToken\":\"1\",\"fcmToken\":\"\",\"createdAt\":0},\"persistentIds\":[]}")]
    public async Task AMissingOrInvalidFileLoadsAsNothing(string? content)
    {
        var store = FileFcmStore.ForAccount(_dir, "a@b.c");
        if (content is not null) await File.WriteAllTextAsync(store.Path, content);
        Assert.IsNull(await store.LoadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ToleratesAFileWithoutPersistentIds()
    {
        var store = FileFcmStore.ForAccount(_dir, "a@b.c");
        await File.WriteAllTextAsync(store.Path, "{\"creds\":{\"fid\":\"f\",\"androidId\":\"1\",\"securityToken\":\"2\",\"fcmToken\":\"t\",\"createdAt\":0}}");
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.IsNotNull(loaded);
        Assert.IsEmpty(loaded.PersistentIds);
    }

    [TestMethod]
    public async Task ClearRemovesTheFile()
    {
        var store = FileFcmStore.ForAccount(_dir, "a@b.c");
        await store.SaveAsync(new PersistedPush { Creds = Creds() }, CancellationToken.None);
        store.Clear();
        Assert.IsFalse(File.Exists(store.Path));
    }

    [TestMethod]
    public void CredentialsNeverPrintTheirSecrets()
    {
        var text = Creds().ToString();
        Assert.DoesNotContain("1234567890123456789", text);
        Assert.DoesNotContain("fcm-token", text);
    }
}
