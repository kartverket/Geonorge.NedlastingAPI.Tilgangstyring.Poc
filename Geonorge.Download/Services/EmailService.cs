using Azure.Core;
using Geonorge.Download.Services.Interfaces;
using System.Net.Http.Headers;
using System.Net.Mail;

namespace Geonorge.Download.Services
{
    public class EmailService(HttpClient httpClient, IConfiguration config) : IEmailService
    {
        // TODO: Replace with Graph API for Exchange mail
        public void Send(MailMessage message)
        {
            //using (var smtpClient = new SmtpClient())
            //{
            //    smtpClient.Host = config["SmtpHost"];
            //    smtpClient.Send(message); // TODO: Send async (?)
            //}

            httpClient.BaseAddress = new Uri(config["EmailRequestRelay"]!);
            var mailReq = new MailRequest
            {
                Subject = message.Subject,
                From = config["WebmasterEmail"]!,
                To = string.Join(";", message.To.Select(m => m.Address)),
                Body = message.Body
            };

            if (message.Bcc != null)
            {
                mailReq.Bcc = string.Join(";", message.Bcc.Select(m => m.Address));
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "/mail/send")
            {
                Content = JsonContent.Create(mailReq)
            };

            var base64Token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(config["EmailRequestRelayBasicAuth"]!));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Token);
            request.Headers.Host = config["EmailRequestRelayHostHeader"];

            var response = httpClient.Send(request);
            response.EnsureSuccessStatusCode();
        }

        private class MailRequest
        {
            public string Subject { get; set; } = string.Empty;
            public string From { get; set; } = string.Empty;
            public string To { get; set; } = string.Empty;
            public string? Bcc { get; set; }
            public string Body { get; set; } = string.Empty;
        }
    }
}
