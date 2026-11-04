namespace RIoT2.Net.Devices.Services.Interfaces
{
    /// <summary>
    /// The eufy account through eufy-sdk itself, which runs in a Node.js child process. The same surface as
    /// <see cref="IEufySecurityService"/> (the C# port), registered separately so both devices can run.
    /// </summary>
    public interface IEufySdkService : IEufySecurityService
    {
    }
}
