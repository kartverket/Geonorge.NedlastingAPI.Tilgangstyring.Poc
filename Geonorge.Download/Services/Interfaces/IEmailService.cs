using System.Net.Mail;

namespace Geonorge.Download.Services.Interfaces
{
    public interface IEmailService
    {
        Task Send(MailMessage message);
    }
}
