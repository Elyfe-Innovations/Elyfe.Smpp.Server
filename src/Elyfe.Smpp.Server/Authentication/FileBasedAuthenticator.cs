using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server.Authentication;

/// <summary>
///     Options for <see cref="FileBasedAuthenticator" />.
/// </summary>
public sealed class FileAuthenticatorOptions
{
    /// <summary>Path to a JSON file containing an array of <see cref="SmppAccount" /> objects.</summary>
    public string FilePath { get; set; } = string.Empty;
}

/// <summary>
///     An <see cref="IAuthenticator" /> that validates bind credentials against a JSON file of accounts. The file is
///     reloaded automatically when its last-write timestamp changes, so credentials can be rotated without a restart.
/// </summary>
public sealed class FileBasedAuthenticator : IAuthenticator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;
    private readonly ILogger<FileBasedAuthenticator> _logger;
    private readonly object _gate = new();

    private Dictionary<string, SmppAccount> _accounts = new(StringComparer.Ordinal);
    private DateTime _lastWriteUtc = DateTime.MinValue;

    /// <summary>Creates the authenticator over the supplied file path.</summary>
    public FileBasedAuthenticator(FileAuthenticatorOptions options, ILogger<FileBasedAuthenticator> logger)
    {
        _filePath = options.FilePath;
        _logger = logger;
        Reload();
    }

    /// <inheritdoc />
    public Task<AuthenticationResult> AuthenticateAsync(SmppCredentials credentials, CancellationToken cancellationToken)
    {
        ReloadIfChanged();

        SmppAccount? account;
        lock (_gate)
        {
            _accounts.TryGetValue(credentials.SystemId, out account);
        }

        return Task.FromResult(SmppAccountVerifier.Verify(account, credentials));
    }

    private void ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(_filePath);
            if (writeTime != _lastWriteUtc)
            {
                Reload();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to check credential file {FilePath}", _filePath);
        }
    }

    private void Reload()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogWarning("SMPP credential file {FilePath} not found", _filePath);
                return;
            }

            var json = File.ReadAllText(_filePath);
            var accounts = JsonSerializer.Deserialize<List<SmppAccount>>(json, SerializerOptions) ?? [];
            var map = accounts
                .Where(a => !string.IsNullOrWhiteSpace(a.SystemId))
                .ToDictionary(a => a.SystemId, a => a, StringComparer.Ordinal);

            lock (_gate)
            {
                _accounts = map;
                _lastWriteUtc = File.GetLastWriteTimeUtc(_filePath);
            }

            _logger.LogInformation("Loaded {Count} SMPP account(s) from {FilePath}", map.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load SMPP credential file {FilePath}", _filePath);
        }
    }
}
