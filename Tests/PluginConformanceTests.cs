using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Devices.Testing;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class PluginConformanceTests
{
    [TestMethod]
    public void PluginConforms() => PluginConformance.Verify(typeof(Plugin));
}
