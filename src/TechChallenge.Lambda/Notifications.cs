using System.Net.Mail;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;

namespace TechChallenge.Lambda;

public interface INotifier
{
    Task SendAsync(string destination, string code, CancellationToken cancellation);
}

public sealed class Notifications(Settings settings) : INotifier
{
    public async Task SendAsync(string destination, string code, CancellationToken cancellation)
    {
        var body = $"Seu código de acesso é {code}. Expira em 5 minutos.";
        if (settings.IsCloud)
        {
            using var ses = new AmazonSimpleEmailServiceV2Client(new AmazonSimpleEmailServiceV2Config
            { Timeout = TimeSpan.FromSeconds(3), MaxErrorRetry = 1 });
            await ses.SendEmailAsync(new SendEmailRequest
            {
                FromEmailAddress = settings.EmailSender,
                Destination = new Destination { ToAddresses = [destination] },
                Content = new EmailContent
                {
                    Simple = new Message
                    {
                        Subject = new Content { Data = "Código de acesso — Tech Challenge", Charset = "UTF-8" },
                        Body = new Body { Text = new Content { Data = body, Charset = "UTF-8" } }
                    }
                }
            }, cancellation);
        }
        else
        {
            // Local Mailpit only. Production always uses authenticated AWS SES.
            using var smtp = new SmtpClient(settings.SmtpHost, settings.SmtpPort);
            using var message = new MailMessage(settings.EmailSender, destination, "Código de acesso", body);
            await smtp.SendMailAsync(message, cancellation);
        }
    }
}
