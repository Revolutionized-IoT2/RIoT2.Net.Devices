using System.Net.Mail;

namespace RIoT2.Net.Devices.Models
{
    public class MailMsg
    {
        public string Body { get; set; }
        public string To { get; set; }
        public string Subject { get; set; }

        public MailMessage GetMailMessage(string from)
        {
            return new MailMessage(from, To, Subject, Body);
        }
    }
}
