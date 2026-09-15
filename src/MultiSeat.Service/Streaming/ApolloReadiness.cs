using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace MultiSeat.Service.Streaming;

internal static class ApolloReadiness
{
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    // A listening port or an arbitrary HTTP 200 is insufficient: it must be this seat's
    // GameStream API, and the process must still exist when the response arrives.
    internal static async Task WaitAsync(Uri endpoint, Guid expectedId, Func<bool> isAlive,
        CancellationToken ct, HttpClient? client = null, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? StartupTimeout);
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!isAlive())
                    throw new InvalidOperationException("Apollo exited before its serverinfo API became ready.");

                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    using var response = await (client ?? Client).GetAsync(endpoint, attempt.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(attempt.Token);
                        if (IsExpectedServer(body, expectedId) && isAlive()) return;
                    }
                }
                catch (HttpRequestException) { /* not listening yet */ }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                { /* individual request timed out; keep polling within the startup deadline */ }

                await Task.Delay(200, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Apollo did not return this seat's serverinfo within {elapsed.Elapsed.TotalSeconds:F0} seconds.");
        }
    }

    internal static bool IsExpectedServer(string xml, Guid expectedId)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 65536
            });
            var root = XDocument.Load(reader).Root;
            return root?.Name == "root" && (string?)root.Attribute("status_code") == "200"
                && Guid.TryParse((string?)root.Element("uniqueid"), out var actualId)
                && actualId == expectedId;
        }
        catch (XmlException) { return false; }
    }
}
