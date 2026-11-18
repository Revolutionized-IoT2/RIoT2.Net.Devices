using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Net.Devices.Services.GoogleCast;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Controllers
{
    /// <summary>
    /// Serves the replies and announcements a VoiceSatellite device made to the satellite that plays them (the ESPHome
    /// protocol): <c>GET /api/voice/audio/{token}</c>. A satellite can't sign in, so the route is anonymous; the random
    /// token, valid for an hour, is the credential.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/voice")]
    public class VoiceAudioController(IVoiceAudioCache cache) : ControllerBase
    {
        [HttpGet]
        [Route("audio/{token}")]
        public IResult GetAudio(string token)
        {
            if (!CastAudioCache.IsToken(token))
                return Results.BadRequest("Invalid token.");

            var audio = cache.Get(token);
            return audio == null
                ? Results.NotFound()
                : Results.File(audio, "audio/wav");
        }
    }
}
