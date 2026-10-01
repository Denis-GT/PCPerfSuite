using System.Net;

namespace PCPerfSuite.Core.Tests;

/// <summary>Serveur HTTP simulé : chaque requête reçoit la réponse que le test fabrique, et toutes sont notées. Aucun
/// accès réseau : sert à éprouver redirections, codes d'erreur et contenus sans dépendre d'Internet.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<Uri, HttpResponseMessage> _respond;

    public FakeHttp(Func<Uri, HttpResponseMessage> respond) => _respond = respond;

    public List<Uri> Requests { get; } = new();

    public HttpClient Client() => new(this, disposeHandler: false);

    public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent(Array.Empty<byte>()) };

    public static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(_respond(request.RequestUri!));
    }
}
