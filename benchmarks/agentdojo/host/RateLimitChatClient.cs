using System.ClientModel;
using System.Globalization;
using Microsoft.Extensions.AI;

namespace Provenance.AgentDojo.Host;

/// <summary>
/// Waits out short rate limits (a per-minute cap) and fails fast on long ones (a daily cap), so a
/// benchmark run stops within seconds instead of hanging for an hour, and <c>--resume</c> picks up later.
/// </summary>
/// <remarks>With <paramref name="retryNetworkErrors"/>, a dropped connection (no response at all) is retried
/// every minute too, so an unattended run survives a short internet outage.</remarks>
internal sealed class RateLimitChatClient(IChatClient inner, TimeSpan maxWait, int maxAttempts = 5, bool retryNetworkErrors = false)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(history, options, cancellationToken);
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                var wait = RetryAfter(ex) ?? TimeSpan.FromSeconds(10 * attempt);
                if (wait > maxWait || attempt >= maxAttempts)
                {
                    throw new RateLimitException(wait, ex);
                }

                // A few seconds' margin, so the first request after the wait isn't refused again.
                wait += TimeSpan.FromSeconds(5);
                if (wait > TimeSpan.FromMinutes(1))
                {
                    Console.WriteLine(
                        $"[rate limit] waiting {Math.Ceiling(wait.TotalMinutes)} min, until about {DateTime.Now.Add(wait):HH:mm}. Leave this window open.");
                }

                await Task.Delay(wait, cancellationToken);
            }
            catch (ClientResultException ex) when (ex.Status == 0 && retryNetworkErrors && attempt < maxAttempts)
            {
                // Status 0: the request never got a response (no internet, DNS failure, connection reset).
                Console.WriteLine($"[network] {ex.Message.Split('\n')[0]} Retrying in 1 min (attempt {attempt}).");
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
    }

    private static TimeSpan? RetryAfter(ClientResultException ex)
    {
        var response = ex.GetRawResponse();
        if (response is not null
            && response.Headers.TryGetValue("retry-after", out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }
}

internal sealed class RateLimitException(TimeSpan retryAfter, Exception inner) : Exception(
    $"The model's rate limit was reached and resets in about {Math.Ceiling(retryAfter.TotalMinutes)} min " +
    "(on a free tier this is usually the daily cap). Run the same command again later with --resume.",
    inner);
