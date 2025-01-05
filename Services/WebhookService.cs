using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services
{
    public class WebhookService : IWebhookService
    {
        public event WebhookHandler WebhookReceived;

        public async Task<string> SendMessageAsync(string address, string content)
        {
            HttpResponseMessage result;
            if (string.IsNullOrEmpty(content))
            {
                result = await Web.GetAsync(address);
            }
            else 
            {
                result = await Web.PostAsync(address, content);
            }
            return await result.Content.ReadAsStringAsync();
        }

        public void SetWebhook(string address, string content)
        {
            WebhookReceived(address, content);
        }
    }
}