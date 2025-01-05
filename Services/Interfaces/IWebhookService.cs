namespace RIoT2.Net.Devices.Services.Interfaces
{
    public interface IWebhookService
    {
        event WebhookHandler WebhookReceived;
        Task<string> SendMessageAsync(string address, string content);

        void SetWebhook(string id, string content);
    }

    public delegate void WebhookHandler(string address, string content);
}