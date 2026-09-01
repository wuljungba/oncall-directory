using System.Text.Json;

namespace OnCallApi.Services;

/// <summary>What the public NPI registry says about a number.</summary>
/// <param name="Found">Whether the registry returned a record at all.</param>
/// <param name="Reachable">
/// Whether the registry could be reached. A lookup that failed is NOT a failed check --
/// treating an outage as "this organization does not exist" would reject real hospitals
/// whenever CMS has a bad afternoon.
/// </param>
public record NpiLookup(
    bool Found,
    bool Reachable,
    string? LegalName,
    string? OrganizationType,
    string? City,
    string? State,
    string? PostalCode,
    string? Message);

public interface INppesRegistryClient
{
    Task<NpiLookup> LookupAsync(string npi, CancellationToken ct = default);
}

/// <summary>
/// Looks up an organizational NPI in the CMS NPPES public registry.
///
/// This is the whole reason verification can be more than a form: the NPI, the legal name
/// and the address are public record, published by CMS, and an applicant does not control
/// what comes back. Checking one answer against a source outside the applicant's reach is
/// worth more than ten fields they filled in themselves.
///
/// The API is free and needs no key.
/// </summary>
public class NppesRegistryClient : INppesRegistryClient
{
    private const string Endpoint = "https://npiregistry.cms.hhs.gov/api/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NppesRegistryClient> _logger;

    public NppesRegistryClient(IHttpClientFactory httpClientFactory, ILogger<NppesRegistryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<NpiLookup> LookupAsync(string npi, CancellationToken ct = default)
    {
        var digits = new string((npi ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length != 10)
            return new NpiLookup(false, true, null, null, null, null, null, "An NPI is ten digits.");

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var url = $"{Endpoint}?version=2.1&number={digits}";
            using var response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("NPPES lookup returned {Status} for an NPI", (int)response.StatusCode);
                return Unreachable($"The NPI registry returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);

            if (!json.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return new NpiLookup(false, true, null, null, null, null, null,
                    "No organization is registered under that NPI.");
            }

            var record = results[0];
            var basic = record.TryGetProperty("basic", out var b) ? b : default;

            var legalName = Text(basic, "organization_name") ?? Text(basic, "name");
            var enumerationType = Text(record, "enumeration_type");

            string? city = null, state = null, postalCode = null;
            if (record.TryGetProperty("addresses", out var addresses)
                && addresses.ValueKind == JsonValueKind.Array
                && addresses.GetArrayLength() > 0)
            {
                // Prefer the practice location; fall back to whatever is first.
                var address = addresses.EnumerateArray()
                    .FirstOrDefault(a => Text(a, "address_purpose") == "LOCATION");
                if (address.ValueKind != JsonValueKind.Object) address = addresses[0];

                city = Text(address, "city");
                state = Text(address, "state");
                postalCode = Text(address, "postal_code");
            }

            return new NpiLookup(true, true, legalName, enumerationType, city, state, postalCode, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("NPPES lookup timed out");
            return Unreachable("The NPI registry did not respond in time.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NPPES lookup failed");
            return Unreachable("The NPI registry could not be reached.");
        }
    }

    /// <summary>
    /// An unreachable registry is reported as exactly that, never as "not found". The
    /// difference decides whether a real hospital is sent to a human queue or told it does
    /// not exist.
    /// </summary>
    private static NpiLookup Unreachable(string message) =>
        new(false, false, null, null, null, null, null, message);

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
