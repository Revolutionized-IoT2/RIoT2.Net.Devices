using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.Push;

namespace RIoT2.Net.Devices.Tests.Eufy.Push;

/// <summary>
/// DataMessageStanza → push event (ported from upstream's envelope-identity, thumbnail-candidate and
/// availability specs): nested payload levels, identity claims and the thumbnail candidate.
/// </summary>
[TestClass]
public class PushNormalizerTests
{
    /// <summary>
    /// protobufjs encoding of a DataMessageStanza whose app_data carries <c>station_sn</c> and a base64
    /// <c>payload</c> of <c>{"device_sn":"T8000P0000000001","payload":{"event_type":3102,"pic_url":"https://example.test/t.jpg"}}\0</c>.
    /// </summary>
    internal const string GoldenDataMessage =
        "12026d311a0c3334383830343331343830322a19636f6d2e6f6365616e77696e672e626174746572792e63616d3a1e0a0a73746174696f6e5f736e1210543830"
        + "303050303030303030303030303a94010a077061796c6f616412880165794a6b5a585a7059325666633234694f694a554f4441774d4641774d4441774d444177"
        + "4d4441784969776963474635624739685a43493665794a6c646d5675644639306558426c496a6f7a4d5441794c434a7761574e6664584a73496a6f6961485230"
        + "63484d364c79396c654746746347786c4c6e526c633351766443357163476369665830414a13303a3137303030303030303030303025616263880180d4930190"
        + "0180d095ffbc31";

    private const string Station = "T8000P0000000000";
    private const string Device = "T8000P0000000001";

    internal static string Encoded(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json + "\0"));

    private static PushEvent PushOf(params (string Key, string Value)[] appData)
    {
        var stanza = new McsDataMessageStanza { From = "348804314802", Category = "c" };
        foreach (var (key, value) in appData) stanza.AppData.Add(new McsAppData(key, value));
        return PushNormalizer.Normalize(PushNormalizer.ToRawMessage(stanza));
    }

    private static PushEvent Normalize(string envelopeJson) =>
        PushNormalizer.Normalize(new RawPushMessage { Payload = JsonNode.Parse(envelopeJson)!.AsObject() });

    [TestMethod]
    public void DecodesTheGoldenDataMessage()
    {
        var stanza = McsDataMessageStanza.Decode(Convert.FromHexString(GoldenDataMessage));
        var raw = PushNormalizer.ToRawMessage(stanza);
        var push = PushNormalizer.Normalize(raw);

        Assert.AreEqual("0:1700000000000%abc", raw.PersistentId);
        Assert.AreEqual(Station, raw.Payload["station_sn"]!.GetValue<string>());
        Assert.AreEqual(Device, push.DeviceSn);
        Assert.AreEqual(Station, push.StationSn);
        Assert.AreEqual(3102, push.EventType);
        Assert.AreEqual("https://example.test/t.jpg", push.ThumbnailUrl);
        Assert.AreEqual(new ThumbnailCandidate("https://example.test/t.jpg", new(ThumbnailAttributionKind.Device, DeviceSn: Device)), push.ThumbnailCandidate);
    }

    [TestMethod]
    public void TakesTheSerialsFromTheEnvelopeWhenThePayloadEntryCarriesNone()
    {
        var push = PushOf(("device_sn", Device), ("station_sn", Station), ("payload", Encoded("{\"event_type\":3301,\"channel\":0}")));
        Assert.AreEqual(Device, push.DeviceSn);
        Assert.AreEqual(Station, push.StationSn);
        Assert.AreEqual(3301, push.EventType);
    }

    [TestMethod]
    public void KeepsTheDetailOfAPayloadEntryThatNestsItsOwnPayload()
    {
        var push = PushOf(
            ("station_sn", Station),
            ("payload", Encoded($"{{\"device_sn\":\"{Device}\",\"payload\":{{\"event_type\":3102,\"pic_url\":\"https://example.test/t.jpg\"}}}}")));
        Assert.AreEqual(Device, push.DeviceSn);
        Assert.AreEqual(Station, push.StationSn);
        Assert.AreEqual(3102, push.EventType);
        Assert.AreEqual("https://example.test/t.jpg", push.ThumbnailUrl);
        Assert.AreEqual(new ThumbnailCandidateAttribution(ThumbnailAttributionKind.Device, DeviceSn: Device), push.ThumbnailCandidate!.Attribution);
    }

    [TestMethod]
    public void ParsesANestedPayloadThatArrivesAsAJsonString()
    {
        var inner = "{\"a\":3103,\"s\":\"T8000P0000000000\"}";
        var push = PushOf(("payload", Encoded($"{{\"payload\":{System.Text.Json.JsonSerializer.Serialize(inner)}}}")));
        Assert.AreEqual(3103, push.EventType);
        Assert.AreEqual(Station, push.DeviceSn); // falls back to the short station `s`
        Assert.IsNull(push.StationSn);
    }

    [TestMethod]
    public void KeepsAPayloadEntryThatIsNotJsonAsTextAndYieldsAnEmptyDetail()
    {
        var push = PushOf(("device_sn", Device), ("payload", Convert.ToBase64String(Encoding.UTF8.GetBytes("not json"))));
        Assert.AreEqual("not json", push.Raw.Payload["payload"]!.GetValue<string>());
        Assert.IsEmpty(push.Payload);
        Assert.AreEqual(Device, push.DeviceSn);
        Assert.IsNull(push.EventType);
    }

    [TestMethod]
    public void DecodesUnpaddedUrlSafeBase64AsNodeDoes()
    {
        var json = "{\"event_type\":3101,\"x\":\"??>\"}";
        var urlSafe = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual(3101, PushOf(("payload", urlSafe)).EventType);
    }

    [TestMethod]
    public void ANonNumberEventTypeYieldsNoType()
    {
        Assert.IsNull(Normalize("{\"payload\":{\"event_type\":\"3101\",\"a\":3101}}").EventType);
        Assert.AreEqual(3101, Normalize("{\"payload\":{\"event_type\":null,\"a\":3101}}").EventType);
    }

    [TestMethod]
    public void DoesNotInterpretTheUnverifiedMField()
    {
        var push = Normalize($"{{\"device_sn\":\"{Device}\",\"station_sn\":\"{Station}\",\"m\":0}}");
        Assert.AreEqual(0, push.Payload["m"]!.GetValue<int>());
        Assert.AreEqual(Device, push.DeviceSn);
    }

    [TestMethod]
    public void ReadsTheCipher()
    {
        Assert.AreEqual(4, Normalize("{\"payload\":{\"k\":4}}").Cipher);
        Assert.AreEqual(2, Normalize("{\"payload\":{\"cipher\":2,\"k\":4}}").Cipher);
    }

    // thumbnail-candidate.spec.ts

    [TestMethod]
    public void AttributesAThumbnailToAnExactNestedDeviceClaim()
    {
        var push = Normalize("{\"payload\":{\"device_sn\":\"T8000P0000000000\",\"pic_url\":\"https://example.test/thumbnail.jpg\"}}");
        Assert.AreEqual(
            new ThumbnailCandidate("https://example.test/thumbnail.jpg", new(ThumbnailAttributionKind.Device, DeviceSn: "T8000P0000000000")),
            push.ThumbnailCandidate);
    }

    [TestMethod]
    public void AttributesMatchingNestedAndEnvelopeClaimsToTheDevice()
    {
        var push = Normalize("{\"device_sn\":\"T8000P0000000000\",\"payload\":{\"device_sn\":\"T8000P0000000000\",\"thumbnail\":\"https://example.test/thumbnail.jpg\"}}");
        Assert.AreEqual(new ThumbnailCandidateAttribution(ThumbnailAttributionKind.Device, DeviceSn: "T8000P0000000000"), push.ThumbnailCandidate!.Attribution);
    }

    [TestMethod]
    public void MarksConflictingDeviceClaimsAsAmbiguous()
    {
        var push = Normalize("{\"device_sn\":\"T8000P0000000001\",\"payload\":{\"device_sn\":\"T8000P0000000000\",\"pic_url\":\"https://example.test/thumbnail.jpg\"}}");
        Assert.AreEqual(new ThumbnailCandidateAttribution(ThumbnailAttributionKind.Ambiguous), push.ThumbnailCandidate!.Attribution);
    }

    [TestMethod]
    public void AttributesShortSIdentityOnlyToAStation()
    {
        var push = Normalize("{\"payload\":{\"s\":\"T8000P0000000000\",\"pic_url\":\"https://example.test/thumbnail.jpg\"}}");
        Assert.AreEqual(new ThumbnailCandidateAttribution(ThumbnailAttributionKind.Station, StationSn: "T8000P0000000000"), push.ThumbnailCandidate!.Attribution);
    }

    [TestMethod]
    public void AttributesStationSnIdentityOnlyToAStation()
    {
        var push = Normalize("{\"station_sn\":\"T8000P0000000000\",\"payload\":{\"thumbnail\":\"https://example.test/thumbnail.jpg\"}}");
        Assert.AreEqual(new ThumbnailCandidateAttribution(ThumbnailAttributionKind.Station, StationSn: "T8000P0000000000"), push.ThumbnailCandidate!.Attribution);
    }

    [TestMethod]
    public void MarksAThumbnailWithoutIdentityAsAmbiguous()
    {
        var push = Normalize("{\"payload\":{\"pic_url\":\"https://example.test/thumbnail.jpg\"}}");
        Assert.AreEqual(new ThumbnailCandidateAttribution(ThumbnailAttributionKind.Ambiguous), push.ThumbnailCandidate!.Attribution);
    }

    [TestMethod]
    public void BuildsNoCandidateFromMalformedOrEmptyUrls()
    {
        var push = Normalize("{\"payload\":{\"device_sn\":\"T8000P0000000000\",\"pic_url\":42,\"thumbnail\":\"   \"}}");
        Assert.IsNull(push.ThumbnailCandidate);
    }
}
