using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeGateway.Core.Security;

namespace EdgeGateway.Service.Security;

public sealed class EdgeGatewayAuthService(EdgeGatewayServiceOptions options)
{
    private const int PasswordIterations = 210_000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, EdgeGatewaySession> sessions = new(StringComparer.Ordinal);

    private string UsersPath => Path.Combine(options.EffectiveSecurityDirectory, "users.json");

    public bool AuthRequired =>
        options.AuthenticationMode == EdgeGatewayAuthenticationMode.Local;

    public EdgeGatewayAuthStatus GetStatus()
    {
        return new EdgeGatewayAuthStatus(
            AuthRequired,
            AuthRequired && RequiresBootstrapNoGate(),
            options.AuthenticationMode.ToString()
        );
    }

    public async Task<EdgeGatewayLoginResult> BootstrapAdminAsync(
        EdgeGatewayBootstrapRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!AuthRequired)
        {
            throw new InvalidOperationException("Local authentication is not enabled.");
        }

        string userName = NormalizeUserName(request.UserName, fallback: "admin");
        ValidatePassword(request.Password);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UserStore store = await ReadStoreNoGateAsync(cancellationToken).ConfigureAwait(false);
            if (store.Users.Count > 0)
            {
                throw new InvalidOperationException("Admin bootstrap has already been completed.");
            }

            store.Users.Add(CreateUser(userName, request.Password, EdgeGatewayRole.Admin));
            await WriteStoreNoGateAsync(store, cancellationToken).ConfigureAwait(false);
            return CreateSessionNoGate(userName, EdgeGatewayRole.Admin);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<EdgeGatewayLoginResult> LoginAsync(
        EdgeGatewayLoginRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!AuthRequired)
        {
            EdgeGatewayRole role = options.DefaultAccessRole;
            return CreateSessionNoGate(Environment.UserName, role);
        }

        string userName = NormalizeUserName(request.UserName, fallback: "");
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new InvalidOperationException("User name is required.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UserStore store = await ReadStoreNoGateAsync(cancellationToken).ConfigureAwait(false);
            UserRecord? user = store.Users.FirstOrDefault(user =>
                user.Enabled && string.Equals(user.UserName, userName, StringComparison.OrdinalIgnoreCase)
            );
            if (user is null || !VerifyPassword(request.Password, user))
            {
                throw new InvalidOperationException("User name or password is invalid.");
            }

            return CreateSessionNoGate(user.UserName, user.Role);
        }
        finally
        {
            gate.Release();
        }
    }

    public EdgeGatewayAccessContext? ResolveToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        lock (sessions)
        {
            if (!sessions.TryGetValue(token, out EdgeGatewaySession? session))
            {
                return null;
            }

            if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                sessions.Remove(token);
                return null;
            }

            return ToContext(session.UserName, session.Role);
        }
    }

    private bool RequiresBootstrapNoGate()
    {
        if (!File.Exists(UsersPath))
        {
            return true;
        }

        try
        {
            UserStore store = JsonSerializer.Deserialize<UserStore>(
                File.ReadAllText(UsersPath),
                JsonOptions
            ) ?? new UserStore();
            return store.Users.Count == 0;
        }
        catch
        {
            return true;
        }
    }

    private EdgeGatewayLoginResult CreateSessionNoGate(string userName, EdgeGatewayRole role)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);
        lock (sessions)
        {
            sessions[token] = new EdgeGatewaySession(userName, role, expiresAt);
        }

        return new EdgeGatewayLoginResult(token, expiresAt, ToContext(userName, role));
    }

    private static EdgeGatewayAccessContext ToContext(string userName, EdgeGatewayRole role)
    {
        return new EdgeGatewayAccessContext(
            userName,
            role,
            EdgeGatewayRolePolicy.PermissionsFor(role)
        );
    }

    private async Task<UserStore> ReadStoreNoGateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(UsersPath))
        {
            return new UserStore();
        }

        await using FileStream stream = File.Open(
            UsersPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        return await JsonSerializer.DeserializeAsync<UserStore>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false)
            ?? new UserStore();
    }

    private async Task WriteStoreNoGateAsync(
        UserStore store,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(options.EffectiveSecurityDirectory);
        string tempPath = $"{UsersPath}.tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(tempPath, UsersPath, overwrite: true);
    }

    private static UserRecord CreateUser(
        string userName,
        string password,
        EdgeGatewayRole role
    )
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = HashPassword(password, salt, PasswordIterations);
        return new UserRecord(
            userName,
            role,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash),
            PasswordIterations,
            true,
            DateTimeOffset.UtcNow
        );
    }

    private static bool VerifyPassword(string password, UserRecord user)
    {
        byte[] salt = Convert.FromBase64String(user.Salt);
        byte[] expected = Convert.FromBase64String(user.PasswordHash);
        byte[] actual = HashPassword(password, salt, user.Iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] HashPassword(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32
        );
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }
    }

    private static string NormalizeUserName(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private sealed record EdgeGatewaySession(
        string UserName,
        EdgeGatewayRole Role,
        DateTimeOffset ExpiresAt
    );

    private sealed class UserStore
    {
        public List<UserRecord> Users { get; init; } = [];
    }

    private sealed record UserRecord(
        string UserName,
        EdgeGatewayRole Role,
        string Salt,
        string PasswordHash,
        int Iterations,
        bool Enabled,
        DateTimeOffset CreatedAt
    );
}
