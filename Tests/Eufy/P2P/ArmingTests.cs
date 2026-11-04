using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Eufy.P2P;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>
/// The guard-mode intent (port of eufy-sdk <c>arming.spec.ts</c> setMode cases and <c>access.ts</c>' builders) and
/// the family predicates that route a station to WebRTC instead of P2P.
/// </summary>
[TestClass]
public class ArmingTests
{
    private static readonly ArmingContext Ctx = new("T8030", 0, "someone+tag");

    [TestMethod]
    [DataRow(0, "away")]
    [DataRow(63, "disarmed")]
    [DataRow(1, "home")]
    [DataRow(3, "custom1")]
    [DataRow(2, "schedule")]
    [DataRow(4, "custom2")]
    [DataRow(5, "custom3")]
    [DataRow(6, "off")]
    [DataRow(47, "geo")]
    public void EveryModeIsASetPayloadOnCmd1224WithExplicitMValue3Zero(int wire, string name)
    {
        var command = Arming.BuildCommand(wire, Ctx);
        var sp = command as SetPayloadCommand;
        Assert.IsNotNull(sp);
        Assert.AreEqual(1224, sp.Cmd);
        Assert.AreEqual(0, sp.Channel);
        Assert.AreEqual(0, sp.MValue3);
        Assert.AreEqual("auto", sp.Form);
        Assert.AreEqual($"{{\"mode_type\":{wire},\"user_name\":\"someone+tag\"}}", sp.Payload.ToJsonString(P2PSession.WireJson));
        Assert.AreEqual(name, Arming.ModeName(wire));
        Assert.IsTrue(Arming.IsValidWireMode(wire));
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(7)]
    [DataRow(46)]
    [DataRow(48)]
    [DataRow(62)]
    [DataRow(64)]
    [DataRow(255)]
    public void RefusesAValueOutsideTheNineModes(int wire)
    {
        Assert.IsFalse(Arming.IsValidWireMode(wire));
        Assert.IsNull(Arming.ModeName(wire));
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Arming.BuildCommand(wire, Ctx));
        StringAssert.Contains(error.Message, "0/1/2/3/4/5/6/47/63");
    }

    [TestMethod]
    public void RefusesAContextWithoutAnAccountIdentity()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Arming.BuildCommand(1, new ArmingContext("T8030", 0, null, 9, "T8030P0000000001")));
        StringAssert.Contains(error.Message, "missing account identity (user_name)");
        StringAssert.Contains(error.Message, "deviceType=9 model=T8030 serial=T8030P0000000001 channel=0");
        Assert.ThrowsExactly<InvalidOperationException>(() => Arming.BuildCommand(1, Ctx with { AccountName = "" }));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(63)]
    public void AT8410CTakesTheDirectStationScalarFrame(int wire)
    {
        var command = Arming.BuildCommand(wire, Ctx with { Model = "T8410C" });
        Assert.AreEqual(new StationScalarCommand(1224, (uint)wire, 0), command);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(47)]
    public void AT8410CRefusesTheModesUnconfirmedOnTheDirectFrame(int wire)
    {
        var error = Assert.ThrowsExactly<NotSupportedException>(() => Arming.BuildCommand(wire, Ctx with { Model = "T8410C" }));
        StringAssert.Contains(error.Message, "unconfirmed on the direct 1224 frame");
    }

    [TestMethod]
    public void OnlyTheT8410CIsADirectArmingModel()
    {
        Assert.IsInstanceOfType<SetPayloadCommand>(Arming.BuildCommand(0, Ctx with { Model = "T8410" }));
        Assert.IsInstanceOfType<SetPayloadCommand>(Arming.BuildCommand(0, Ctx with { Model = null }));
    }

    [TestMethod]
    public void FamilyPredicatesReadDeviceTypeAndModel()
    {
        Assert.IsTrue(DeviceFamily.IsStation9000(new FamilyContext(27, "T8030")));
        Assert.IsFalse(DeviceFamily.IsStation9000(new FamilyContext(18, "T9000")));
        Assert.IsFalse(DeviceFamily.IsStation9000(new FamilyContext(null, null)));
        Assert.IsTrue(DeviceFamily.IsNvrT8N00(new FamilyContext(null, "T8N00")));
        Assert.IsFalse(DeviceFamily.IsNvrT8N00(new FamilyContext(27, "t8n00")));
        Assert.IsTrue(DeviceFamily.IsCallDoorbell(new FamilyContext(null, "T8223")));
        Assert.IsTrue(DeviceFamily.IsCallDoorbell(new FamilyContext(null, "T8224")));
        Assert.IsFalse(DeviceFamily.IsCallDoorbell(new FamilyContext(null, "T8222")));
    }

    [TestMethod]
    public void RoutesT9000AndT8N00StationsAndTheirDevicesAndOwnCallDoorbellsToWebRtc()
    {
        IReadOnlyList<EufyDeviceRecord> devices =
        [
            P2PDevices.Record("T9000P0000000001", "T8030", deviceType: 27),
            P2PDevices.Record("T8113P0000000001", "T8113", stationSn: "T9000P0000000001", channel: 1),
            P2PDevices.Record("T8N00P0000000001", "T8N00"),
            P2PDevices.Record("T8E00P0000000001", "T8E00", stationSn: "T8N00P0000000001", channel: 0),
            P2PDevices.Record("T8223P0000000001", "T8223"),
            P2PDevices.Record("T8010P0000000001", "T8010", deviceType: 0),
            P2PDevices.Record("T8224P0000000001", "T8224", stationSn: "T8010P0000000001", channel: 2),
            P2PDevices.Record("T8410P0000000001", "T8410C", deviceType: 31),
        ];
        StringAssert.Contains(EufyP2PClient.WebRtcRoute(devices, "T9000P0000000001"), "T9000");
        StringAssert.Contains(EufyP2PClient.WebRtcRoute(devices, "T8113P0000000001"), "T9000");
        StringAssert.Contains(EufyP2PClient.WebRtcRoute(devices, "T8N00P0000000001"), "T8N00");
        StringAssert.Contains(EufyP2PClient.WebRtcRoute(devices, "T8E00P0000000001"), "T8N00");
        StringAssert.Contains(EufyP2PClient.WebRtcRoute(devices, "T8223P0000000001"), "call doorbell");
        Assert.IsNull(EufyP2PClient.WebRtcRoute(devices, "T8010P0000000001"));
        Assert.IsNull(EufyP2PClient.WebRtcRoute(devices, "T8224P0000000001"), "a call-doorbell model attached to a HomeBase rides its P2P session");
        Assert.IsNull(EufyP2PClient.WebRtcRoute(devices, "T8410P0000000001"));
        Assert.IsNull(EufyP2PClient.WebRtcRoute(devices, "unknown"));
    }

    [TestMethod]
    public void ResolvesTheStationAndChannelOfEachDevice()
    {
        IReadOnlyList<EufyDeviceRecord> devices =
        [
            P2PDevices.Record("T8010P0000000001", "T8010"),
            P2PDevices.Record("CAM1", "T8113", stationSn: "T8010P0000000001", channel: 1),
            P2PDevices.Record("CAM2", "T8113", stationSn: "T8010P0000000001", channel: 2),
            P2PDevices.Record("CAM3", "T8113", stationSn: "T8010P0000000001", channel: 2),
            P2PDevices.Record("SENSOR", "T8900", stationSn: "SENSOR", parentSn: "T8010P0000000001"),
            P2PDevices.Record("SOLO", "T8410C", channel: 5),
        ];
        var channels = StationChannels.Resolve(devices);
        Assert.AreEqual(StationChannel.Of(0), channels["T8010P0000000001"]);
        Assert.AreEqual(StationChannel.Of(1), channels["CAM1"]);
        Assert.AreEqual(StationChannel.Shared(2), channels["CAM2"]);
        Assert.AreEqual(StationChannel.Shared(2), channels["CAM3"]);
        Assert.AreEqual(StationChannel.Missing, channels["SENSOR"]);
        Assert.AreEqual(StationChannel.Of(5), channels["SOLO"]);
        Assert.AreEqual("T8010P0000000001", StationChannels.StationOf(devices[4]), "parent_sn wins over station_sn");
        Assert.AreEqual("SOLO", StationChannels.StationOf(devices[5]));

        using var raw = JsonDocument.Parse("{\"parent_sn\":\"\",\"station_sn\":\"\"}");
        Assert.AreEqual("X", StationChannels.ResolvedStationSn(raw.RootElement, "X"));
    }

    [TestMethod]
    public void PicksTheFreshestPrivateLanAddress()
    {
        Assert.IsTrue(LanIp.IsPrivateIpv4("10.0.0.1"));
        Assert.IsTrue(LanIp.IsPrivateIpv4("172.16.0.1"));
        Assert.IsTrue(LanIp.IsPrivateIpv4("172.31.255.255"));
        Assert.IsTrue(LanIp.IsPrivateIpv4("192.168.1.2"));
        Assert.IsFalse(LanIp.IsPrivateIpv4("172.32.0.1"));
        Assert.IsFalse(LanIp.IsPrivateIpv4("8.8.8.8"));
        Assert.IsFalse(LanIp.IsPrivateIpv4("192.168.1.256"));
        Assert.IsFalse(LanIp.IsPrivateIpv4("not-an-ip"));

        using var record = JsonDocument.Parse("""
            {"ip_addr":"192.168.86.20","local_ip":"203.0.113.5","params":[
              {"param_type":1176,"param_value":"192.168.1.30","update_time":100},
              {"param_type":1177,"param_value":"192.168.1.31","update_time":200},
              {"param_type":1178,"param_value":"8.8.8.8","update_time":300}]}
            """);
        Assert.AreEqual("192.168.1.31", LanIp.FreshestLanIp(record.RootElement));
        using var pairing = JsonDocument.Parse("{\"ip_addr\":\"192.168.86.20\",\"local_ip\":\"203.0.113.5\"}");
        Assert.AreEqual("192.168.86.20", LanIp.FreshestLanIp(pairing.RootElement));
        using var none = JsonDocument.Parse("{\"local_ip\":\"\"}");
        Assert.IsNull(LanIp.FreshestLanIp(none.RootElement));
    }
}
