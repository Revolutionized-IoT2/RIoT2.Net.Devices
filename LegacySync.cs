namespace RIoT2.Net.Devices
{
    /// <summary>
    /// The synchronous members the device interfaces still require (<c>ICommandDevice.ExecuteCommand</c>, which
    /// <c>IAsyncCommandDevice</c> extends). The node calls the async members; only an older host would call these.
    /// </summary>
    internal static class LegacySync
    {
#pragma warning disable VSTHRD002 // The one deliberate synchronous wait (plan M11 group A): an old host's sync contract.
        public static void Wait(Task task) => task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }
}
