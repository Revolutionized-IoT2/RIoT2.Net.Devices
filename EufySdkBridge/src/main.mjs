// Entry point of the bundled bridge (see bridge.mjs for the protocol).
import { createInterface } from "node:readline";
import { createBridge } from "./bridge.mjs";

const MIN_NODE = [24, 5];
const debug = process.env.EUFY_BRIDGE_LOG === "debug";

function log(level, message) {
  if (level === "debug" && !debug) return;
  process.stderr.write(`${level} ${String(message).replace(/\r?\n/g, " ")}\n`);
}

function send(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

// stdout is the protocol channel: anything printed elsewhere goes to the log.
console.log = console.info = (...args) => log("info", args.join(" "));
console.debug = (...args) => log("debug", args.join(" "));
console.warn = (...args) => log("warn", args.join(" "));
console.error = (...args) => log("error", args.join(" "));

const [major, minor] = process.versions.node.split(".").map(Number);
if (major < MIN_NODE[0] || (major === MIN_NODE[0] && minor < MIN_NODE[1])) {
  send({ event: "fatal", data: { message: `eufy-sdk needs Node.js ${MIN_NODE.join(".")} or newer; this is ${process.versions.node}` } });
  process.exit(1);
}

const sdk = await import("@mega-yfue/eufy-sdk");
// eslint-disable-next-line no-undef
const sdkVersion = typeof EUFY_SDK_VERSION === "string" ? EUFY_SDK_VERSION : "unbundled";
const bridge = createBridge({ sdk, send, log, sdkVersion });

process.on("unhandledRejection", (e) => log("error", `unhandled: ${e instanceof Error ? e.message : String(e)}`));

const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
// Requests run concurrently: a slow snapshot doesn't hold the next request.
lines.on("line", (line) => {
  if (line.trim()) void bridge.handle(line);
});
// The device closes stdin to stop the bridge, and so does a parent that died.
lines.on("close", async () => {
  try {
    await bridge.methods.stop();
  } catch (e) {
    log("warn", `stopping eufy-sdk: ${e instanceof Error ? e.message : String(e)}`);
  }
  process.exit(0);
});

send({ event: "ready", data: { node: process.versions.node, sdk: sdkVersion } });
