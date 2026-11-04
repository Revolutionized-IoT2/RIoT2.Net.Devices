// The RIoT2 bridge to eufy-sdk. The EufySecuritySdk device of RIoT2.Net.Devices runs the bundled bridge with
// node and talks to it in JSON lines over stdin/stdout:
//   request  {"id":1,"method":"login","params":{...}}
//   reply    {"id":1,"result":{...}}  or  {"id":1,"error":{"name":"...","message":"..."}}
//   event    {"event":"security","data":{...}}
// stdout carries only protocol lines; logs go to stderr. Never log the password, tokens or URLs.

/** The eufy-sdk semantic events the device reports (the names of eufy-sdk's DeviceEventMap). */
export const SECURITY_EVENTS = new Set([
  "motion",
  "personDetected",
  "strangerDetected",
  "petDetection",
  "dogDetected",
  "soundDetected",
  "cryingDetected",
  "vehicleDetected",
  "doorbellPress",
  "packageDelivered",
  "packageTaken",
  "packageStranded",
  "armingModeChanged",
  "alarm",
]);

/** How long the person names of a push stay attached to the device's next person event. */
const PUSH_NAMES_MS = 10_000;
const REALTIME_WAIT_MS = 30_000;

/**
 * @param {object} deps
 * @param {object} deps.sdk The eufy-sdk module (EufyMega, FileSessionStore, FileFcmStore, LoginStatus).
 * @param {(message: object) => void} deps.send Writes one protocol message.
 * @param {(level: string, message: string) => void} deps.log Writes a log line to stderr.
 * @param {string} [deps.sdkVersion]
 * @param {() => number} [deps.now]
 */
export function createBridge({ sdk, send, log, sdkVersion = "unknown", now = Date.now }) {
  /** @type {any} */
  let eufy;
  let devices = [];
  // eufy-sdk announces a property change only while its Device object is held.
  const held = new Map();
  const pushNames = new Map();
  let securityQueue = Promise.resolve();

  const logger = {
    debug: (m) => log("debug", String(m)),
    info: (m) => log("info", String(m)),
    warn: (m) => log("warn", String(m)),
    error: (m) => log("error", String(m)),
  };

  function client() {
    if (!eufy) throw new Error("start first");
    return eufy;
  }

  function loginResult(r) {
    switch (r?.status) {
      case sdk.LoginStatus.Ok:
        return { status: "ok" };
      case sdk.LoginStatus.Captcha:
        return { status: "captcha", image: r.image, retry: !!r.retry };
      case sdk.LoginStatus.TwoFactor:
        return { status: "2fa", method: r.method };
      default:
        throw new Error(`unexpected login status ${JSON.stringify(r?.status)}`);
    }
  }

  function record(d) {
    const raw = d.raw ?? {};
    return {
      sn: d.sn,
      name: d.name || d.sn,
      model: d.model,
      stationSn: d.stationSn || d.sn,
      channel: typeof d.channel === "number" ? d.channel : undefined,
      p2pDid: d.p2pDid || undefined,
      deviceType: typeof raw.device_type === "number" ? raw.device_type : undefined,
      deviceClass: d.deviceClass,
      params: d.params ?? {},
    };
  }

  async function refreshDevices() {
    const list = await client().getDevices();
    devices = list.map(record);
    for (const d of list) {
      if (held.has(d.sn)) continue;
      try {
        held.set(d.sn, await client().getDevice(d.sn));
      } catch (e) {
        log("warn", `device ${d.sn}: ${message(e)}`);
      }
    }
    for (const sn of [...held.keys()]) if (!list.some((d) => d.sn === sn)) held.delete(sn);
    return devices;
  }

  async function device(sn) {
    return held.get(sn) ?? (await client().getDevice(sn));
  }

  function p2pDidFor(e) {
    const own = devices.find((d) => d.sn === e.deviceSn);
    const stationSn = e.stationSn ?? own?.stationSn;
    return devices.find((d) => d.sn === stationSn)?.p2pDid ?? own?.p2pDid;
  }

  function rememberPush(ev) {
    const payload = ev?.payload ?? {};
    const names = Array.isArray(payload.objects?.names)
      ? payload.objects.names.filter((n) => typeof n === "string" && n.trim()).join(",")
      : "";
    const name = names || (typeof payload.person_name === "string" && payload.person_name.trim()) || "";
    if (ev?.deviceSn && name) pushNames.set(ev.deviceSn, { name, at: now() });
  }

  function personNameFor(e) {
    const entry = e.deviceSn ? pushNames.get(e.deviceSn) : undefined;
    if (!entry || now() - entry.at > PUSH_NAMES_MS) return undefined;
    pushNames.delete(e.deviceSn);
    return entry.name;
  }

  async function onSecurity(e) {
    const data = {
      name: e.eventName,
      deviceSn: e.deviceSn,
      stationSn: e.stationSn,
      eventType: e.eventType,
      phase: e.phase,
      alarmType: e.type,
    };
    if (e.eventName === "personDetected") data.personName = personNameFor(e);
    if (e.thumbnailUrl) {
      try {
        const jpeg = await client().api.downloadImage(e.thumbnailUrl, p2pDidFor(e));
        data.picture = Buffer.from(jpeg).toString("base64");
      } catch (err) {
        log("warn", `the ${e.eventName} thumbnail could not be downloaded: ${message(err)}`);
      }
    }
    send({ event: "security", data });
  }

  function wire(c) {
    c.on("error", (e) => log("warn", `eufy-sdk: ${message(e)}`));
    c.on("sessionExpired", (e) => send({ event: "sessionExpired", data: { message: message(e) } }));
    c.on("pushConnect", () => send({ event: "push", data: { connected: true } }));
    c.on("pushDisconnect", () => send({ event: "push", data: { connected: false } }));
    c.on("push", (ev) => rememberPush(ev));
    c.on("event", (e) => {
      if (!SECURITY_EVENTS.has(e?.eventName)) return;
      // In arrival order, also when a thumbnail download is slow.
      securityQueue = securityQueue.then(() => onSecurity(e)).catch((err) => log("error", message(err)));
    });
    c.on("propertyChanged", (e) => {
      if (e?.property === "armingMode" && typeof e.value === "number" && e.deviceSn)
        send({ event: "guardMode", data: { stationSn: e.deviceSn, mode: e.value } });
    });
  }

  const methods = {
    ping: () => ({ node: process.versions.node, sdk: sdkVersion }),

    async start(p) {
      if (eufy) await eufy.disconnect().catch(() => undefined);
      held.clear();
      devices = [];
      eufy = new sdk.EufyMega({
        email: p.email,
        password: p.password,
        countryCode: p.countryCode,
        openudid: p.openudid || undefined,
        store: new sdk.FileSessionStore(p.sessionFile),
        pushStore: new sdk.FileFcmStore(p.pushFile),
        ffmpegPath: p.ffmpegPath || undefined,
        logger,
      });
      wire(eufy);
      return {};
    },

    login: async () => loginResult(await client().login()),
    solveCaptcha: async (p) => loginResult(await client().solveCaptcha(String(p.answer))),
    submitVerifyCode: async (p) => loginResult(await client().submitVerifyCode(String(p.code))),

    async connect() {
      const list = await refreshDevices();
      const readiness = await client().waitForRealtime({ timeoutMs: REALTIME_WAIT_MS });
      return { devices: list, realtime: readiness?.state };
    },

    getDevices: async () => ({ devices: await refreshDevices() }),

    async setGuardMode(p) {
      const arming = (await device(p.stationSn)).arming?.();
      if (typeof arming?.setMode !== "function") throw new Error(`${p.stationSn} has no guard mode`);
      await arming.setMode(Number(p.mode));
      return {};
    },

    async snapshot(p) {
      const camera = (await device(p.sn)).camera?.();
      if (typeof camera?.snapshotLive !== "function") throw new Error(`${p.sn} has no live snapshot`);
      const still = await camera.snapshotLive();
      return { jpeg: Buffer.from(still.jpeg).toString("base64"), retained: !!still.retained };
    },

    async stop() {
      const c = eufy;
      eufy = undefined;
      held.clear();
      if (c) await c.disconnect();
      return {};
    },
  };

  /** Handles one request line; the reply is sent, never thrown. */
  async function handle(line) {
    let request;
    try {
      request = JSON.parse(line);
    } catch {
      log("error", "a request line is not JSON");
      return;
    }
    const { id, method, params } = request ?? {};
    const fn = Object.hasOwn(methods, method) ? methods[method] : undefined;
    try {
      if (!fn) throw new Error(`unknown method ${JSON.stringify(method)}`);
      send({ id, result: (await fn(params ?? {})) ?? {} });
    } catch (e) {
      send({ id, error: { name: e?.name || "Error", message: message(e) } });
    }
  }

  return { handle, methods };
}

function message(e) {
  return e instanceof Error ? e.message : String(e);
}
