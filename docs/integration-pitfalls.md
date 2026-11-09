# Integration pitfalls

Applies to: the device integrations of RIoT2.Net.Devices that talk to a vendor's cloud or protocol: what their APIs
require, what they keep in `Data/`, and what breaks them. Read it before changing one of them. The general rules and
pitfalls are in [AGENTS.md](../AGENTS.md); the user-facing notes are in [cloud-devices.md](cloud-devices.md).

## Netatmo

- Netatmo tokens live in `NetatmoAuthClient`, one per client id, from the singleton
  `NetatmoAuthClientFactory`. Netatmo rotates the refresh token, so two clients for the same app
  would invalidate each other: always get the client from the factory. Call Netatmo through
  `NetatmoApiClient` (refresh-and-retry, typed `NetatmoApiException`) inside `CallNetatmoAsync`, which keeps the
  device running with the reason as its state message; only configuration errors may put it into the error state.
- The Netatmo devices are connected from the UI (`IDeviceWithAuthorization`, Core 2.4.0). Devices of one app share
  its tokens, so a sign-in asks for the union of their scopes (`RequireScopes`); a new device type adds its scopes
  there.
- Netatmo allows **one webhook URL per app**, and the URL (`/api/netatmo/webhook/{secret}`) is the only credential:
  never log it. `NetatmoWebhookHub` registers it through `webhooks/v1`, never replaces another URL unless
  `webhookTakeover` is set, and doesn't unregister on stop. Answer pushes at once and handle them off the request:
  Netatmo bans an app's webhook for 24 hours after 5 failed deliveries. Pushed and polled events share one
  de-duplication key (`NetatmoSecurityEvent.DeduplicationKey`).
- Netatmo token files are `Data/netatmoAuth-<clientId>.json`; the first account keeps the older
  `Data/netatmoAuth.json` and its client id is written into it. A valid file takes precedence over
  configured tokens so refreshed credentials survive restarts.

## MELCloud

- MELCloud's APIs are not public. Classic MELCloud follows pymelcloud; MELCloud Home follows the
  melcloudhome Home Assistant integration, including its OAuth PKCE login through AWS Cognito's form.
  Its requests go through `DirectHttp`, which follows no redirects and keeps no cookies (the Home login
  handles both) and checks the outbound URL policy itself. Logins are kept in `Data/melcloud-<cloud>-<hash>.json`; MELCloud
  limits logins and polling, so keep the one-minute poll floor. Only `MelCloudAuthenticationException`
  is a configuration error; `MelCloudUnavailableException` (429, 5xx, timeouts) keeps the last state.

## myUplink

- myUplink uses the official API (swagger at api.myuplink.com/swagger) with the client credentials
  grant. The curated F750 parameter ids in `MyUplinkCatalog` are NIBE register ids, not checked
  against every model or firmware; add fallback ids rather than replacing them. Min and max are
  multiplied by `scaleValue`, the value and step are not. Writes need Premium Manage: a 403 is
  reported as that, not as a configuration error. `MyUplinkRefusedException` (400, 403, 404, 409)
  isn't retried; `MyUplinkUnavailableException` (429, 5xx, timeouts) keeps the last state.

## Themo

- Themo uses the public API (swagger at connect.themo.io/swagger) pinned to `api-version=2.1`:
  v2.2 dropped the hourly `/data` endpoint. Its "Price data" series' "Consumption" column is in MWh.
  A command message carries one field (`CMT`, `CMode`, ...), so `setTemperature` outside manual
  mode sends `CMode` and then `CMT`. `energyHour` uses `SendReport` with the hour's time stamp, not
  `SendReportIfValueChanged`, so equal hours are all kept.

## Govee

- Govee uses the official Open API (developer.govee.com, beta) with the key in the `Govee-API-Key` header; never log
  the key or put it into an error. Govee also returns errors as HTTP 200 with a `code` in the body, so `GoveeClient`
  checks both. Limits: 30 device list requests a minute per account, 30 state requests a minute per device; keep the
  one-minute poll floor and the daily device list. `sensorTemperature` is °F (`temperatureUnit` turns it off), and a
  value of `""` means "can't be read". When `online` is false the values are history: report only `online`. The
  battery instance isn't documented, so any property whose name contains `battery` is used. `temperatureHourly` uses
  `SendReport` with the hour's time stamp; the samples and the last reported hour are in `Data/govee-<hash>.json`.

## Miele

- Miele's API (api.mcs3.miele.com/v1) is official, but its login without a browser posts Miele's login
  form (`/oauth/auth`, a 302 whose `Location` has the code), as miele-to-mqtt-gw does; it must go
  through `DirectHttp`, which follows no redirects. The pasted-code flow is the fallback, and each code
  is remembered in the token file so it isn't sent twice. Temperatures are 0.01 °C with -32768 for
  none, times are `[hours, minutes]`. The event stream listener runs from start to stop with its own
  cancellation and back-off; every state change, from it or a poll, goes through `ApplyDevice`, which
  follows runs (`MieleRuns`) under one lock.

## DEFA Power

- DEFA Power's CloudCharge API (prod.cloudcharge.se/services/user) isn't public; `CloudChargeClient`
  follows the ha-defa-power Home Assistant integration. The login is SMS-only (`/prelogin`, then `/login`
  with the code), so it is done with the device's `requestSmsCode` and `login` commands; the code is only
  known to the server that sent it, picked by the `INGRESSCOOKIE` cookie, so the client keeps the cookies
  itself over `DirectHttp`. The `devToken` is the CloudCharge app's public id (one login per app: the
  DEFA Power app stays logged in). A 401 is a lost login (`CloudChargeAuthenticationException`): the
  login file is deleted and the device waits for a new SMS login, still running. CloudCharge limits
  requests per token, so keep the one-second `RequestInterval`. The device polls in its own loop
  (`NextDelay`: 15 s while charging, 60 s otherwise, and just after each quarter-hour) and has no
  refresh schedule. There is no history endpoint: `DefaPowerEnergy` shares the meter's increase between
  quarter-hours by time, and the reported periods, the open session and the prices are kept in the
  state file. Units follow ha-defa-power: `meterValue` and `transactionMeterValue` kWh, `powerConsumption` kW.

## Harvia

- Harvia's MyHarvia cloud (Xenio WiFi panels) isn't public; `HarviaClient` follows the ha-harvia-sauna and
  ha-harvia-xenio-wifi Home Assistant integrations. Read the endpoints and the Cognito pool from discovery
  (`prod.myharvia-cloud.net/<service>/endpoint`), never hard-code them. The app client allows only
  `USER_SRP_AUTH` (`USER_PASSWORD_AUTH` is disabled, checked 2026-11-04), so `CognitoSrp` does Amazon's SRP by
  hand; `TheSrpProofMatchesAmazonsHelper` pins it to a pycognito vector, and the fake pool in `HarviaTests` checks
  the proof as Cognito does. Bring `B - k·g^x` into `[0, N)` before `ModPow` (.NET keeps the sign), and pad the
  salt as the string Cognito sent. AppSync takes the ID token as `authorization` without `Bearer`. Pushes are
  partial, so `HarviaSauna` merges them; the push connections are renewed every 5 minutes, as a connection goes
  quiet after 12-14 minutes. `statusCodes` is a bit field (1 door, 5 target reached, 8 heat demand, 11 light,
  17 session, 18 stopped) and `timedStart` 8 bytes (enabled, 15-minute units, °C, 0, ready time as uint32 LE).
  A value built in code (`JsonValue.Create(1)`) doesn't convert with `TryGetValue<double>`; `HarviaJson` reads by
  value kind. The Matter thermostat's 40-110 °C range comes from the setpoint command's schema, which the
  Orchestrator uses as the thermostat's limits.

## Eufy

- `Services/Eufy/` is ported from eufy-sdk (Apache-2.0, `NOTICE`) at commit `5f92bea5`; every ported file names its
  upstream files in its first line. The eufy protocols are reverse-engineered: change wire formats only from a newer
  eufy-sdk, re-porting its spec vectors into `Tests/Eufy/`, and record the new commit here and in `NOTICE`. Keep the
  contracts in `EufyContracts.cs` stable; `EufySecurityService` composes the clients through
  `IEufyComponentFactory`, which the tests replace. Never log the password, tokens or keys. The session and push
  files (`Data/eufy-session-*.json`, `Data/eufy-push-*.json`) are credentials. Repeated logins trigger captchas: the
  cloud client holds re-logins off, and nothing retries a captcha or verification code on its own. Guard mode and
  snapshots go over P2P only; T9000/T8N00 stations and T8223/T8224 doorbells (WebRTC upstream) are refused.
- `EufySecuritySdk` runs the bundle `Services/EufySdk/eufy-sdk-bridge.mjs`, which is generated: change
  `EufySdkBridge/` and run `npm run build`, never edit the bundle. CI (`eufy-sdk-bridge` job) runs the bridge tests,
  rebuilds and fails when the committed bundle differs, so a Renovate bump of eufy-sdk or esbuild needs the rebuilt
  bundle committed. `build.mjs` turns eufy-sdk's `createRequire(...)("protobufjs")` into a bundled require and fails
  when the bundle would load anything but Node built-ins and `node-datachannel` (native, left out: WebRTC stations
  only). The protocol is JSON lines over stdio; stdout carries only protocol lines, so the bridge sends every log to
  stderr, and never logs the password, tokens or thumbnail URLs. The password goes over stdin, never in arguments or
  the environment. Keep `EventKinds` in `EufySdkService` and `SECURITY_EVENTS` in `bridge.mjs` in step with eufy-sdk's
  `DeviceEventMap`. The bridge passes eufy-sdk its own `openudid`, so it doesn't log the C# port's session out.

## Google Cast

- `GoogleCast` talks to the speaker through SharpCaster, whose calls take no cancellation token: `SharpCasterSession`
  awaits each with a timeout and the caller's token, and wraps failures in `CastException`. After a failed connect,
  SharpCaster's `Dispose()` never completes (it waits for a receive loop that never started), so the session doesn't
  await it then. The device creates a session per command through `CreateSession()`, which the tests replace with a
  fake; never let a test reach a real speaker. Set the announcement volume back in a `finally`, with its own timeout,
  so a failed or cancelled announcement doesn't leave the speaker loud. `CastAudioController` is `[AllowAnonymous]`
  (a speaker can't sign in): the 128-bit random token is the credential, and only a 32-hex-digit token is looked up.
  The Cloud API key goes in the `X-Goog-Api-Key` header, never in the URL or a log.
