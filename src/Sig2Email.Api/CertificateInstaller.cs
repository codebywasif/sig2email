using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sig2Email.Data;

namespace Sig2Email.Api;

/// <summary>
/// <c>dotnet Sig2Email.Api.dll install-certificate &lt; certificate.pfx</c>: stores a PKCS#12 file (no password) as the
/// SMTP certificate, exactly as an upload under Settings → System does. Meant for a certificate-renewal hook; the mail
/// processor picks the new certificate up within seconds, without a restart.
/// </summary>
public static class CertificateInstaller
{
    public const string Command = "install-certificate";
    private const int MaxBytes = 256 * 1024;

    public static async Task<int> RunAsync(Stream input, TextWriter output, TextWriter error)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSig2EmailData(builder.Configuration, builder.Environment);
        using var host = builder.Build(); // never started: only the settings store is needed

        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        return await InstallAsync(host.Services.GetRequiredService<SettingsProvider>(), bytes, output, error);
    }

    public static async Task<int> InstallAsync(SettingsProvider settings, byte[] bytes, TextWriter output, TextWriter error)
    {
        if (bytes.Length is 0 or > MaxBytes)
        {
            await error.WriteLineAsync("Pipe a PKCS#12 (.pfx) file of at most 256 KB to standard input.");
            return 2;
        }

        string summary;
        try
        {
            var (leaf, chain) = Pfx.Load(bytes, null);
            try
            {
                if (leaf.NotAfter < DateTime.Now)
                {
                    await error.WriteLineAsync("That certificate has expired; the certificate in use was left alone.");
                    return 1;
                }
                summary = $"{leaf.GetNameInfo(X509NameType.SimpleName, false)}, valid until {leaf.NotAfter.ToUniversalTime():yyyy-MM-dd}, {chain.Count} intermediate(s)";
            }
            finally
            {
                leaf.Dispose();
                foreach (var c in chain) c.Dispose();
            }
        }
        catch (CryptographicException)
        {
            await error.WriteLineAsync("The input could not be opened. It must be a PKCS#12 file without a password that contains the private key.");
            return 1;
        }

        await settings.UpdateAsync(s =>
        {
            s.SmtpCertificatePfx = Convert.ToBase64String(bytes);
            s.SmtpCertificatePassword = null;
        });
        await output.WriteLineAsync($"SMTP certificate installed: {summary}");
        return 0;
    }
}
