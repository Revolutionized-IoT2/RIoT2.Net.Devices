using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Abstracts;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
[DoNotParallelize]
public class NetatmoAuthenticationTests
{
    private string _originalDirectory;
    private string _testDirectory;
    private Dictionary<FieldInfo, object> _originalState;

    [TestInitialize]
    public void Initialize()
    {
        _originalDirectory = Environment.CurrentDirectory;
        _testDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_testDirectory, "Data"));
        Environment.CurrentDirectory = _testDirectory;
        _originalState = typeof(NetatmoBase)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => !field.IsLiteral && !field.IsInitOnly)
            .ToDictionary(field => field, field => field.GetValue(null));
        SetField("_configured", false);
        _ = new TestDevice();
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var entry in _originalState)
            entry.Key.SetValue(null, entry.Value);
        Environment.CurrentDirectory = _originalDirectory;
        Directory.Delete(_testDirectory, true);
    }

    [TestMethod]
    public void RestartUsesPersistedRotatedTokensWithoutOverwritingThem()
    {
        const string persisted = """{"access_token":"synthetic-current-access","refresh_token":"synthetic-current-refresh"}""";
        File.WriteAllText(Path.Combine("Data", "netatmoAuth.json"), persisted);

        NetatmoBase.ConfigureNetatmo("synthetic-old-access", "synthetic-old-refresh", "test-client", "test-secret");

        Assert.AreEqual("synthetic-current-access", GetField("_accessToken"));
        Assert.AreEqual("synthetic-current-refresh", GetField("_refreshToken"));
        Assert.AreEqual(persisted, File.ReadAllText(Path.Combine("Data", "netatmoAuth.json")));

        SetField("_configured", false);
        NetatmoBase.ConfigureNetatmo("synthetic-old-access", "synthetic-old-refresh", "test-client", "test-secret");
        Assert.AreEqual("synthetic-current-refresh", GetField("_refreshToken"));
    }

    [TestMethod]
    public void MissingAuthenticationFileUsesAndPersistsConfiguredTokens()
    {
        NetatmoBase.ConfigureNetatmo("synthetic-initial-access", "synthetic-initial-refresh", "test-client", "test-secret");

        Assert.AreEqual("synthetic-initial-access", GetField("_accessToken"));
        Assert.AreEqual("synthetic-initial-refresh", GetField("_refreshToken"));
        Assert.IsTrue(File.Exists(Path.Combine("Data", "netatmoAuth.json")));
    }

    [TestMethod]
    public void MalformedAuthenticationDoesNotOverwriteTheFile()
    {
        const string malformed = "{invalid-json";
        File.WriteAllText(Path.Combine("Data", "netatmoAuth.json"), malformed);

        Assert.ThrowsException<Exception>(() =>
            NetatmoBase.ConfigureNetatmo("synthetic-access", "synthetic-refresh", "test-client", "test-secret"));

        Assert.AreEqual(malformed, File.ReadAllText(Path.Combine("Data", "netatmoAuth.json")));
    }

    private static object GetField(string name) =>
        typeof(NetatmoBase).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private static void SetField(string name, object value) =>
        typeof(NetatmoBase).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);

    private sealed class TestDevice() : NetatmoBase(NullLogger.Instance)
    {
        public override void ConfigureDevice() { }
        public override void StartDevice() { }
        public override void StopDevice() { }
    }
}
