using Azure.Core;
using Azure.Identity;
using Geonorge.Download.Services.Interfaces;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Threading;

namespace Geonorge.Download.Services
{
    public sealed class EmailService : IEmailService
    {
        private readonly GraphServiceClient _graphClient;
        private readonly string _senderMailbox;

        public EmailService(GraphMailOptions options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.TenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientId);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientSecret);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.SenderMailbox);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);

            _senderMailbox = options.SenderMailbox;

            // For app-only auth, Graph SDK uses Azure.Identity credentials.
            var credential = new ClientSecretCredential(
                options.TenantId,
                options.ClientId,
                options.ClientSecret);

            // ".default" tells Entra to issue the app permissions already consented for Graph.
            _graphClient = new GraphServiceClient(credential, [options.BaseUrl]);
        }

        public async Task Send(MailMessage mailMessage)
        {

            string to = string.Join(";", mailMessage.To.Select(m => m.Address));
            string subject = mailMessage.Subject;
            string plainTextBody = mailMessage.Body;
            IEnumerable<string>? bcc = mailMessage.Bcc?.Select(m => m.Address);
            CancellationToken cancellationToken = default;

            ArgumentException.ThrowIfNullOrWhiteSpace(to);
            ArgumentException.ThrowIfNullOrWhiteSpace(subject);
            ArgumentException.ThrowIfNullOrWhiteSpace(plainTextBody);

            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody
                {
                    ContentType = BodyType.Text,
                    Content = plainTextBody
                },
                ToRecipients = ParseRecipients(to),
                BccRecipients = ParseRecipients(bcc)
            };

            var body = new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody
            {
                Message = message,
                SaveToSentItems = true
            };

            try
            {
                // App-only sending is done through the target mailbox.
                await _graphClient
                    .Users[_senderMailbox]
                    .SendMail
                    .PostAsync(body, cancellationToken: cancellationToken);
            }
            catch (ApiException ex)
            {
                // Very useful when Graph returns auth / permission / mailbox scope errors.
                throw new InvalidOperationException(
                    $"Graph sendMail failed. StatusCode={ex.ResponseStatusCode}, Message={ex.Message}",
                    ex);
            }
        }

        private static List<Recipient> ParseRecipients(string recipients)
        {
            return recipients
                .Split(';', ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(email => new Recipient
                {
                    EmailAddress = new EmailAddress { Address = email }
                })
                .ToList();
        }

        private static List<Recipient> ParseRecipients(IEnumerable<string>? recipients)
        {
            return recipients?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(email => new Recipient
                {
                    EmailAddress = new EmailAddress { Address = email.Trim() }
                })
                .ToList()
                ?? new List<Recipient>();
        }
    }
}
