using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechChallenge.Lambda;

// Local convenience entrypoint. AWS invokes Function.FunctionHandler directly.
if (args.Contains("--bootstrap"))
{
    Directory.CreateDirectory(".local");
    if (!File.Exists(".local/private.pem"))
    {
        using var key = RSA.Create(2048);
        File.WriteAllText(".local/private.pem", key.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(".local/private.pem", UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    if (!File.Exists(".env"))
    {
        File.WriteAllText(".env", File.ReadAllText(".env.example").Replace("REPLACE_WITH_GENERATED_SECRET", Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(".env", UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    Console.WriteLine("Configuração local criada. Nenhum segredo exibido.");
    return;
}
using var listener = new HttpListener();
listener.Prefixes.Add(Environment.GetEnvironmentVariable("LOCAL_PREFIX") ?? "http://localhost:8080/");
listener.Start();
Console.WriteLine("{\"service\":\"local-lambda-adapter\",\"port\":8080}");
var function = new Function();
while (listener.IsListening)
{
    var context = await listener.GetContextAsync();
    try
    {
        using var buffer = new MemoryStream();
        var block = new byte[1024];
        int count;
        while ((count = await context.Request.InputStream.ReadAsync(block)) > 0)
        {
            buffer.Write(block, 0, count);
            if (buffer.Length > 8192) break;
        }
        if (buffer.Length > 8192) { context.Response.StatusCode = 413; context.Response.Close(); continue; }
        var input = JsonSerializer.SerializeToElement(new
        {
            rawPath = context.Request.Url!.AbsolutePath,
            headers = context.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => context.Request.Headers[k]!),
            body = Encoding.UTF8.GetString(buffer.ToArray()),
            requestContext = new { http = new { method = context.Request.HttpMethod, sourceIp = context.Request.RemoteEndPoint.Address.ToString() } }
        });
        var response = await function.FunctionHandler(input, null);
        context.Response.StatusCode = response.StatusCode;
        foreach (var (key, value) in response.Headers) context.Response.Headers[key] = value;
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response.Body));
    }
    catch (Exception) { context.Response.StatusCode = 500; }
    finally { context.Response.Close(); }
}
