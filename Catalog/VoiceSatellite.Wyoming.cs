using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Services.Voice;
using RIoT2.Net.Devices.Services.Wyoming;

namespace RIoT2.Net.Devices.Catalog
{
    public partial class VoiceSatellite
    {
        /// <summary>
        /// The Wyoming protocol, as hassmic speaks it with <c>PROTO=wyoming</c>: the node is the client, sends
        /// <c>describe</c> and <c>run-satellite</c>, gets <c>run-pipeline</c> and the microphone's audio, and answers with
        /// <c>voice-stopped</c>, <c>transcript</c> and the speech as <c>audio-start</c>, <c>audio-chunk</c>s and
        /// <c>audio-stop</c>; hassmic says <c>played</c>. <c>error</c> ends a request.
        /// </summary>
        private sealed class WyomingSession(VoiceSatellite device, WyomingConnection connection, CancellationToken stop) : Session(stop)
        {
            private TaskCompletionSource? _played;
            private bool _answered;

            public override async Task StartAsync()
            {
                await connection.WriteAsync("describe", null, Token).ConfigureAwait(false);
                await connection.WriteAsync("run-satellite", null, Token).ConfigureAwait(false);
            }

            public override async Task<bool> ReceiveAsync(CancellationToken cancellationToken)
            {
                var message = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (message == null)
                    return false;
                if (!_answered)
                {
                    // The satellite's first event (its answer to describe) shows the connection works.
                    _answered = true;
                    OnAnswered?.Invoke();
                    device.Connected(this, "Wyoming");
                }
                await HandleAsync(message).ConfigureAwait(false);
                return true;
            }

            private async Task HandleAsync(WyomingEvent message)
            {
                switch (message.Type)
                {
                    case "run-pipeline":
                        var stage = message.GetString("start_stage") ?? "";
                        if (stage == "asr")
                        {
                            device.BeginRun(this);
                            break;
                        }
                        lock (device._gate)
                        {
                            Run?.End();
                            Run = null;
                        }
                        device.WarnWakeWordOnServer($"start_stage {stage}");
                        break;
                    case "audio-chunk":
                        if (message.GetInt("width") is int width && width != 2 || message.GetInt("channels") is int channels && channels != 1)
                        {
                            Run? run;
                            lock (device._gate)
                            {
                                run = Run;
                                if (run == null || run.Phase != RunPhase.Listening)
                                    break;
                                run.Phase = RunPhase.Done;
                            }
                            device.Logger.LogWarning("The voice satellite sends audio that isn't 16-bit mono; the request is dropped.");
                            await device.EndQuietlyAsync(this, run, "Nothing was said.").ConfigureAwait(false);
                            break;
                        }
                        await device.AudioAsync(this, message.GetInt("rate") ?? 16000, message.Payload).ConfigureAwait(false);
                        break;
                    case "played":
                        _played?.TrySetResult();
                        break;
                    case "ping":
                        await connection.WriteAsync("pong", null, Token).ConfigureAwait(false);
                        break;
                    case "info":
                        if (message.Data["satellite"] is JsonObject satellite && satellite["name"] is JsonValue name)
                            device.Logger.LogInformation("The voice satellite at {Host} is {Name}.", device._host, name.ToString());
                        break;
                }
            }

            public override Task PingAsync() => connection.WriteAsync("ping", null, Token);

            public override Task SpeechEndedAsync() => connection.WriteAsync("voice-stopped", null, Token);

            public override Task TranscriptAsync(string text) =>
                connection.WriteAsync("transcript", new JsonObject { ["text"] = text }, Token);

            public override Task EndRunAsync(string code, string text) =>
                connection.WriteAsync("error", new JsonObject { ["code"] = code, ["text"] = text }, Token);

            // A reply and an announcement are played alike: whether hassmic plays one while idle is its to decide.
            public override async Task PlayAsync(PcmAudio audio, string text, bool reply, CancellationToken cancellationToken)
            {
                var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _played = played;
                try
                {
                    // Written on the connection's token: a cancelled command doesn't cut the speech off mid-event.
                    var format = WyomingEvent.AudioFormat(audio.Rate, audio.Channels);
                    await connection.WriteAsync("audio-start", format, Token).ConfigureAwait(false);
                    foreach (var chunk in SpeechEngines.Chunks(audio))
                        await connection.WriteAsync(new WyomingEvent("audio-chunk", WyomingEvent.AudioFormat(audio.Rate, audio.Channels), chunk), Token).ConfigureAwait(false);
                    await connection.WriteAsync("audio-stop", null, Token).ConfigureAwait(false);
                    await device.WaitPlayedAsync(this, played.Task, audio, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.CompareExchange(ref _played, null, played);
                }
            }

            public override ValueTask CloseAsync() => connection.DisposeAsync();
        }
    }
}
