namespace RIoT2.Net.Devices.Models
{
    internal class Notification
    {
        public string title { get; set; }
        public string body { get; set; }
        public string image { get; set; }
    }

    internal class Message
    {
        public string topic { get; set; }
        public Notification notification { get; set; }
    }

    internal class FirebaseServiceMessage
    {
        public Message message { get; set; }
    }

    public class FirebaseMsg 
    {
        public string Title { get; set; }
        public string Body { get; set; }
        public string Topic { get; set; }
        public string ImgUrl { get; set; }

        internal FirebaseServiceMessage GetServiceMessage() 
        {
            return new FirebaseServiceMessage()
            {
                message = new Message() 
                {
                    topic = Topic,
                    notification = new Notification() 
                    {
                        body = Body,
                        image = ImgUrl,
                        title = Title
                    }
                }
            };
        }
    }
}
