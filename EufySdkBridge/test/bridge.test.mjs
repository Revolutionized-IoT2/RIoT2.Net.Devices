import { test } from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { createBridge } from "../src/bridge.mjs";

const LoginStatus = { Ok: "ok", Captcha: "captcha", TwoFactor: "2fa" };

function fakeSdk({ login = [{ status: "ok" }], devices = [], images = {} } = {}) {
  const created = [];
  class EufyMega extends EventEmitter {
    constructor(options) {
      super();
      this.options = options;
      this.logins = [...login];
      this.calls = [];
      this.disconnected = false;
      this.api = {
        downloadImage: async (url, p2pDid) => {
          this.calls.push(["downloadImage", url, p2pDid]);
          if (!(url in images)) throw new Error("404");
          return Buffer.from(images[url]);
        },
      };
      this.held = new Map();
      created.push(this);
    }
    async login() {
      return this.logins.shift();
    }
    async solveCaptcha(answer) {
      this.calls.push(["solveCaptcha", answer]);
      return this.logins.shift();
    }
    async submitVerifyCode(code) {
      this.calls.push(["submitVerifyCode", code]);
      return this.logins.shift();
    }
    async getDevices() {
      return devices;
    }
    async getDevice(sn) {
      const dev = {
        arming: () => ({ setMode: async (m) => this.calls.push(["setMode", sn, m]) }),
        camera: () => ({ snapshotLive: async () => ({ jpeg: Buffer.from([0xff, 0xd8]), width: 1, height: 1 }) }),
      };
      this.held.set(sn, dev);
      return dev;
    }
    async waitForRealtime() {
      return { state: "ready" };
    }
    async disconnect() {
      this.disconnected = true;
    }
  }
  class FileSessionStore {
    constructor(path) {
      this.path = path;
    }
  }
  class FileFcmStore {
    constructor(path) {
      this.path = path;
    }
  }
  return { sdk: { EufyMega, FileSessionStore, FileFcmStore, LoginStatus }, created };
}

function harness(options) {
  const { sdk, created } = fakeSdk(options);
  const sent = [];
  const logs = [];
  let clock = 1000;
  const bridge = createBridge({ sdk, send: (m) => sent.push(m), log: (l, m) => logs.push([l, m]), sdkVersion: "9.9.9", now: () => clock });
  let id = 0;
  const call = async (method, params) => {
    const requestId = ++id;
    await bridge.handle(JSON.stringify({ id: requestId, method, params }));
    return sent.find((m) => m.id === requestId);
  };
  return { bridge, sent, logs, created, call, tick: (ms) => (clock += ms) };
}

const START = {
  email: "user@example.com",
  password: "secret",
  countryCode: "FI",
  openudid: "0123456789abcdef",
  sessionFile: "/data/s.json",
  pushFile: "/data/p.json",
  ffmpegPath: "/usr/bin/ffmpeg",
};

const DEVICES = [
  { sn: "S1", name: "HomeBase", model: "T8010", stationSn: "S1", p2pDid: "P2P-S1", params: { 1224: "1" }, raw: { device_type: 0 }, deviceClass: "homebase" },
  { sn: "T1", name: "", model: "T8113", stationSn: "S1", channel: 0, params: {}, raw: {}, deviceClass: "camera" },
];

test("ping answers the versions; unknown methods and calls before start are errors", async () => {
  const h = harness();
  assert.deepEqual((await h.call("ping")).result, { node: process.versions.node, sdk: "9.9.9" });
  assert.match((await h.call("nope")).error.message, /unknown method/);
  assert.match((await h.call("login")).error.message, /start first/);
  await h.bridge.handle("not json");
  assert.equal(h.logs.at(-1)[0], "error");
});

test("start creates the client with the stores, the install id and ffmpeg", async () => {
  const h = harness();
  await h.call("start", START);
  const o = h.created[0].options;
  assert.equal(o.email, "user@example.com");
  assert.equal(o.countryCode, "FI");
  assert.equal(o.openudid, "0123456789abcdef");
  assert.equal(o.store.path, "/data/s.json");
  assert.equal(o.pushStore.path, "/data/p.json");
  assert.equal(o.ffmpegPath, "/usr/bin/ffmpeg");
  assert.ok(!h.logs.some(([, m]) => m.includes("secret")));
});

test("the login steps map the SDK's results", async () => {
  const h = harness({ login: [{ status: "captcha", image: "data:image/png;base64,AA", retry: false }, { status: "2fa", method: "email" }, { status: "ok" }] });
  await h.call("start", START);
  assert.deepEqual((await h.call("login")).result, { status: "captcha", image: "data:image/png;base64,AA", retry: false });
  assert.deepEqual((await h.call("solveCaptcha", { answer: "ab12" })).result, { status: "2fa", method: "email" });
  assert.deepEqual((await h.call("submitVerifyCode", { code: "123456" })).result, { status: "ok" });
  assert.deepEqual(h.created[0].calls, [["solveCaptcha", "ab12"], ["submitVerifyCode", "123456"]]);
});

test("connect lists the devices, holds them and waits for realtime", async () => {
  const h = harness({ devices: DEVICES });
  await h.call("start", START);
  const { result } = await h.call("connect");
  assert.equal(result.realtime, "ready");
  assert.deepEqual(result.devices[0], { sn: "S1", name: "HomeBase", model: "T8010", stationSn: "S1", channel: undefined, p2pDid: "P2P-S1", deviceType: 0, deviceClass: "homebase", params: { 1224: "1" } });
  assert.equal(result.devices[1].name, "T1", "a nameless device is named by its serial");
  assert.deepEqual([...h.created[0].held.keys()], ["S1", "T1"]);
});

test("guard mode and snapshot drive the held devices", async () => {
  const h = harness({ devices: DEVICES });
  await h.call("start", START);
  await h.call("connect");
  assert.deepEqual((await h.call("setGuardMode", { stationSn: "S1", mode: 63 })).result, {});
  assert.deepEqual(h.created[0].calls.at(-1), ["setMode", "S1", 63]);
  assert.deepEqual((await h.call("snapshot", { sn: "T1" })).result, { jpeg: "/9g=", retained: false });
});

test("security events are forwarded in order with their thumbnails and person names", async () => {
  const h = harness({ devices: DEVICES, images: { "https://img/1": [1, 2, 3] } });
  await h.call("start", START);
  await h.call("connect");
  const c = h.created[0];
  c.emit("push", { deviceSn: "T1", payload: { objects: { names: ["Alice", " ", "Bob"] } } });
  c.emit("event", { eventName: "personDetected", deviceSn: "T1", thumbnailUrl: "https://img/1", eventType: 3101 });
  c.emit("event", { eventName: "motion", deviceSn: "T1", thumbnailUrl: "https://img/missing" });
  c.emit("event", { eventName: "alarm", stationSn: "S1", phase: "delayed", type: 2 });
  c.emit("event", { eventName: "propertyChanged", deviceSn: "T1" });
  await new Promise((r) => setTimeout(r, 20));

  const events = h.sent.filter((m) => m.event === "security").map((m) => m.data);
  assert.deepEqual(events.map((e) => e.name), ["personDetected", "motion", "alarm"]);
  assert.equal(events[0].personName, "Alice,Bob");
  assert.equal(events[0].picture, Buffer.from([1, 2, 3]).toString("base64"));
  assert.equal(events[0].eventType, 3101);
  assert.equal(events[1].picture, undefined, "a failed download reports the event without a picture");
  assert.deepEqual([events[2].phase, events[2].alarmType, events[2].stationSn], ["delayed", 2, "S1"]);
  assert.deepEqual(c.calls.filter((x) => x[0] === "downloadImage").map((x) => x[2]), ["P2P-S1", "P2P-S1"]);
  assert.ok(!JSON.stringify(h.sent).includes("https://img"), "thumbnail URLs are not forwarded");
});

test("a person name older than the push window is not attached", async () => {
  const h = harness({ devices: DEVICES });
  await h.call("start", START);
  const c = h.created[0];
  c.emit("push", { deviceSn: "T1", payload: { person_name: "Alice" } });
  h.tick(60_000);
  c.emit("event", { eventName: "personDetected", deviceSn: "T1" });
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(h.sent.find((m) => m.event === "security").data.personName, undefined);
});

test("guard mode changes, push state and session expiry become events", async () => {
  const h = harness();
  await h.call("start", START);
  const c = h.created[0];
  c.emit("propertyChanged", { deviceSn: "S1", property: "armingMode", value: 0 });
  c.emit("propertyChanged", { deviceSn: "S1", property: "enabled", value: true });
  c.emit("pushConnect");
  c.emit("sessionExpired", new Error("token expired"));
  c.emit("error", new Error("transient"));
  assert.deepEqual(h.sent.filter((m) => m.event).map((m) => [m.event, m.data]), [
    ["guardMode", { stationSn: "S1", mode: 0 }],
    ["push", { connected: true }],
    ["sessionExpired", { message: "token expired" }],
  ]);
  assert.ok(h.logs.some(([l, m]) => l === "warn" && m.includes("transient")));
});

test("stop disconnects; a second start replaces the client", async () => {
  const h = harness();
  await h.call("start", START);
  await h.call("start", START);
  assert.equal(h.created[0].disconnected, true);
  await h.call("stop");
  assert.equal(h.created[1].disconnected, true);
  assert.match((await h.call("login")).error.message, /start first/);
});
