namespace Aggregator.HealthProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 ||
            !Uri.TryCreate(args[0], UriKind.Absolute, out var target) ||
            target.Scheme is not ("http" or "https"))
        {
            Console.Error.WriteLine("Usage: HealthProbe <http-or-https-url>");
            return 64;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        try
        {
            using var response = await client.GetAsync(
                target,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    $"Readiness endpoint returned HTTP {(int)response.StatusCode}.");
                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Readiness endpoint timed out.");
            return 1;
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
