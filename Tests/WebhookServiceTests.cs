using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class WebhookServiceTests
{
    [TestMethod]
    public void SetWebhookWithoutSubscribersDoesNotThrow()
    {
        var service = new WebhookService();

        service.SetWebhook("unmapped", "payload");
    }
}
