using System.Net.Mail;

namespace Shop;

public sealed class EmailSender(SmtpClient smtp, string from)
{
    public Task SendAsync(string to, string subject, string body) =>
        smtp.SendMailAsync(new MailMessage(from, to, subject, body));
}
