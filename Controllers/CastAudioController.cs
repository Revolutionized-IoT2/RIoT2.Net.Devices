using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Net.Devices.Services.GoogleCast;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Controllers
{
    /// <summary>
    /// Serves the speech a GoogleCast device made to the speaker that plays it: <c>GET /api/cast/audio/{token}</c>. A
    /// speaker can't sign in, so the route is anonymous; the random token, valid for an hour, is the credential.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/cast")]
    public class CastAudioController(ICastAudioCache cache) : ControllerBase
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
                : Results.File(audio, "audio/mpeg", enableRangeProcessing: true);
        }
    }
}
