using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Devices.Testing;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Catalog;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The devices with typed options (I30): their parameter definitions and how they bind.</summary>
[TestClass]
public class DeviceOptionsTests
{
    public static IEnumerable<object[]> Devices =>
    [
        [typeof(AzureRelay), "relayNamespace,connectionName,keyName,key", "key"],
        [typeof(EasyPLC), "ipAddress,port", ""],
        [typeof(FTP), "ftpUsers,ftpPort", "ftpUsers"],
        [typeof(Mqtt), "clientId,serverUrl,userName,password,subscribeTopics", "password"],
        [typeof(Messaging), "firebaseProjectName,smtp_Server,smtp_Port,smtp_User,smtp_Password", "smtp_Password"],
        [typeof(MelCloud), "cloud,email,password", "password"],
        [typeof(MyUplink), "clientId,clientSecret,language,extraParameters,offsetParameter,baseTemperature,degreesPerStep", "clientSecret"],
        [typeof(WeatherForecast), "provider,latitude,longitude,fmiPlace,forecastHours,endpoint", ""],
    ];

    // The keys are the ones stored configurations use, so they load unchanged.
    [TestMethod]
    [DynamicData(nameof(Devices))]
    public void TemplatesDescribeTheStoredKeysAndMaskSecrets(Type deviceType, string keys, string secrets)
    {
        using var host = new PluginTestHost(typeof(Plugin));
        var device = host.Devices.Single(d => d.GetType() == deviceType);
        var template = DeviceTemplates.Create(device);

        CollectionAssert.AreEqual(keys.Split(','), template.ParameterDefinitions!.Select(d => d.Key).ToArray());
        CollectionAssert.AreEquivalent(keys.Split(','), template.DeviceParameters.Keys.ToArray());
        CollectionAssert.AreEqual(secrets.Split(',', StringSplitOptions.RemoveEmptyEntries),
            template.ParameterDefinitions!.Where(d => d.Secret).Select(d => d.Key).ToArray());
        Assert.IsTrue(template.ParameterDefinitions!.All(d => !string.IsNullOrEmpty(d.Description)), "every parameter is described");
    }

    [TestMethod]
    public void NumericParametersAreTypedWithTheirDefaults()
    {
        var easyPlc = DeviceTemplates.Create(new EasyPLC(NullLogger.Instance)).ParameterDefinitions!.Single(d => d.Key == "port");
        using var host = new PluginTestHost(typeof(Plugin));
        var ftp = DeviceTemplates.Create(host.GetDevice<FTP>()).ParameterDefinitions!.Single(d => d.Key == "ftpPort");
        var smtp = DeviceTemplates.Create(new Messaging(NullLogger.Instance)).ParameterDefinitions!.Single(d => d.Key == "smtp_Port");

        Assert.AreEqual((DeviceParameterType.Integer, "10001"), (easyPlc.Type, easyPlc.Default));
        Assert.AreEqual((DeviceParameterType.Integer, "21"), (ftp.Type, ftp.Default));
        Assert.AreEqual((DeviceParameterType.Integer, "587"), (smtp.Type, smtp.Default));
    }

    [TestMethod]
    public void AMissingRequiredParameterIsReportedInTheDeviceState()
    {
        var device = new EasyPLC(NullLogger.Instance);

        Assert.ThrowsExactly<Exception>(() => device.Initialize(Configuration(new() { ["port"] = "x" })));
        Assert.AreEqual(DeviceState.Error, device.State);
        Assert.AreEqual("Invalid device parameters: ipAddress is required; port: 'x' is not a valid whole number", device.StateMessage);
    }

    // Divergence C1: parameter keys bind case-insensitively, whatever case the download gives them.
    [TestMethod]
    public void MessagingBindsCaseInsensitivelyAndKeepsBothChannelsOptional()
    {
        var device = new Messaging(NullLogger.Instance);
        device.Initialize(Configuration(new() { ["smtp_server"] = "mail.example.com", ["SMTP_PORT"] = "465" }));

        Assert.AreEqual(DeviceState.Initialized, device.State);
        Assert.AreEqual(("mail.example.com", 465), (device.Options.SmtpServer, device.Options.SmtpPort));

        var empty = new Messaging(NullLogger.Instance);
        empty.Initialize(Configuration([]));
        Assert.AreEqual((DeviceState.Initialized, 587), (empty.State, empty.Options.SmtpPort));
    }

    private static DeviceConfiguration Configuration(Dictionary<string, string> parameters) =>
        new() { Id = "device", Name = "Device", DeviceParameters = parameters, CommandTemplates = [], ReportTemplates = [] };
}
