# Cloud device notes

Applies to: the MELCloud, myUplink, Themo, Miele and DEFA Power devices of RIoT2.Net.Devices: how they sign in, what
they keep in `Data/`, and their limits. Their parameters, reports and commands are in the [device catalog](devices.md).

MELCloud has no public API; the device uses the apps' APIs, like the pymelcloud library and the
MELCloud Home integration for Home Assistant, so a MELCloud change can break it. It keeps its login in
`Data/melcloud-<cloud>-<hash of the e-mail>.json` (classic: the context key; Home: the OAuth tokens,
whose refresh token changes on every refresh), because MELCloud limits logins. A wrong password puts
the device into the error state. While MELCloud is unavailable or limits requests (429), the device
keeps the last state and polls again at the next refresh. A command's change is reported at once;
the unit takes it from MELCloud within about a minute. Use one MELCloud Home device per account:
two would rotate each other's refresh token and log in again on every refresh. The Matter thermostat
has no Auto mode, so a unit in `auto` shows its setpoints but not its mode there. Air-to-water
(Ecodan) units are skipped.

myUplink uses the official API with the OAuth client credentials of your own app: register one at
[dev.myuplink.com](https://dev.myuplink.com) with the account that owns the heat pump and enter its
client id and secret. The device reads the heat pump's parameter list once at start, then polls only
the parameters it reports. The parameter ids are NIBE's and depend on the model and firmware; a
curated id the heat pump doesn't have is skipped. With debug logging, the device logs every parameter
it finds with its id, name and value; add the ones you want to `extraParameters`. Changing settings
through the API needs a myUplink Premium Manage subscription: without it myUplink refuses changes,
and the command fails with an error that says so (`premiumManage` reports whether the subscription is
active). Without a room sensor, the F750 has no target temperature, only the heating curve's offset.
`setTemperature` is a virtual setpoint that a Command Plan or the heat pump workflow templates can
drive: the offset becomes `round((target - baseTemperature) / degreesPerStep)`, and the reported
`setTemperature` is the offset turned back into °C. Tune `degreesPerStep` to how much one offset step
changes your room temperature. The Matter thermostat shows the exhaust air temperature (BT20), which
follows the room temperature with a delay, as its current temperature. `energyHeating` and
`energyHotWater` are heat meters (the heat the pump produced, including additional heat), not the
electricity it used; `energyToday` is their increase since the day's first poll, so it misses what was
produced between midnight and that poll. The day's starting point is kept in
`Data/myuplink-<hash of the device id>.json`, so a restart keeps it. While myUplink is unavailable or
limits requests (429), the device keeps the last state and polls again at the next refresh.

Themo uses the Themo Public API (connect.themo.io) with the Themo app's e-mail and password; the
login's token is kept in memory only. The device asks for API version 2.1, the last one with hourly
device data. Only manual mode follows a target temperature, so `setTemperature` first switches a
thermostat in `sls` (Themo's schedule and price optimization) or `off` mode to `manual`; send `mode`
`sls` or a `schedule` to hand control back to Themo. A Matter setpoint change does the same. Commands
are reported at once; the thermostat applies them with a delay. `energyHour` reports every finished
hour once, at the hour's own time; the last reported hour is kept in
`Data/themo-<hash of the thermostat id>.json`, so after a restart or an outage the device reports the
hours it missed, at most 48. `energyToday` sums the finished hours since local midnight, and
`heatingShareToday` divides it by the thermostat's rated power over those hours. Themo doesn't
document the rated power's unit; the device reads a value up to 1 as MW, up to 100 as kW and a larger
one as W. Without the hourly data, the state and commands still work and the energy reports wait.
The `boost` command needs thermostat software 4.2 or later, and the `boost` lock 4.4.0. The Matter
thermostat has no Auto mode, so a thermostat in `sls` shows its setpoints but not its mode there.

Miele uses the Miele 3rd Party API with your own app: register one at
[developer.miele.com](https://www.miele.com/f/com/en/register_api.aspx) and enter its client id and
secret. The device signs in with the account's e-mail, password and country through Miele's login
form, as several open-source gateways do; if that fails or isn't configured, open the login address
the device's error shows (`https://api.mcs3.miele.com/thirdparty/login?...`, with the app's redirect
URI as `redirectUri`), log in, and paste the code (or the whole address you are sent to) into
`authorizationCode`. Each code is used once. The tokens are kept in
`Data/miele-auth-<hash of the client id>.json` and refreshed before they expire, so a login lasts
months. Miele pushes changes on an event stream that the device keeps open, reconnecting after 5 s
up to 5 minutes; the hourly poll reads everything again. Remote start, a delayed start
(`startDelay`, or `startAt` with a time, e.g. a cheap price slot's start) and choosing a program need
remote start (MobileStart) switched on at the appliance, and a washing machine needs its program
chosen on the machine; a command Miele doesn't allow now fails with the reason. Washing machines
usually don't let their program be chosen remotely, so they get no `program` command. A run starts
when the appliance starts running and ends when the program ends, fails or is interrupted;
`programEvent`, `lastRun`, `energyLastRun` and `waterLastRun` are sent then, time-stamped at the
run's end. The energy and water come from Miele's eco feedback, which only newer appliances have.
Day totals and an open run are kept in `Data/miele-<hash of the fabrication number>.json`, so a
restart in the middle of a run still reports it. Core has no appliance device types for Matter, so
only oven temperatures are bridged, as temperature sensors.

DEFA Power uses the CloudCharge API (prod.cloudcharge.se) that the DEFA Power and CloudCharge apps
use. It isn't public or supported by DEFA; the device follows the
[ha-defa-power](https://github.com/Bebbssos/ha-defa-power) Home Assistant integration. To log in, set
`phoneNumber` with the country code and without the national number's
leading zero (`+358401234567`, not `+358 040...`), add the device from its template and start it, send
`requestSmsCode`, then send `login` with the code from the SMS; send both while the device runs, as
the code is only valid on the CloudCharge server that sent it. Then load the template again to get the
connectors' reports and commands. The login is kept in `Data/defapower-auth-<hash of the phone
number>.json` and lasts until CloudCharge refuses it; then `loginState` becomes `loginRequired` and
the device waits for a new SMS login. The device logs in as the CloudCharge app (the API allows one
login per app), so the DEFA Power app on your phone stays logged in, but the CloudCharge app, if you
use it, is logged out. CloudCharge limits requests per login, so the device waits a second between
requests. There is no charging history in the API, so the device works out the energy from the meter:
the increase between two readings is shared by time between the quarter-hours they span, and every
quarter-hour's reading is taken a few seconds after it ends. `energyQuarter`, `energyHour` and
`costQuarter` report each finished period once, at its own start; the state is kept in
`Data/defapower-<hash of the phone number>.json`, so after a restart the device shares the energy used
while it was down and reports the periods it missed, at most 48 hours back. The first period after the
first start, which the device saw only in part, isn't reported. A session starts when a car is plugged
in and ends when it is unplugged; its energy is the meter's increase. For the costs, forward
ElectricityPrice's `prices` report to the `prices` command with a workflow (when the report changes);
the device keeps three days of prices. Prices in c/kWh (ElectricityPrice's unit, with its `vat`) give
costs in euros. They are spot prices: the transfer fee and margins aren't included. Quarter-hours that
ended before the prices came get their cost when they come. Core has no EV charger device type for
Matter, so the chargers aren't bridged.

