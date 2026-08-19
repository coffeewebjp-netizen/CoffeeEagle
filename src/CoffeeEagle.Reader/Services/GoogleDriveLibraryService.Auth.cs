using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed partial class GoogleDriveLibraryService
{

    public async Task<bool> HasRefreshTokenAsync()
    {
        return !string.IsNullOrWhiteSpace(await GetRefreshTokenAsync());
    }


    public async Task AuthorizeWithBrowserAsync(
        EagleReaderState state,
        IProgress<LibrarySyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state.GoogleDriveClientId))
        {
            throw new InvalidOperationException("Google OAuth Client ID が未設定です。");
        }

        var codeVerifier = CreateCodeVerifier();
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var authUri = CreateAuthorizationUri(state.GoogleDriveClientId, codeChallenge);

        progress?.Report(new LibrarySyncProgress("Googleログインを開いています", "Google OAuth", Detail: "accounts.google.com"));
        var result = await WebAuthenticator.Default.AuthenticateAsync(authUri, new Uri(BrowserRedirectUri));
        if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            var error = result.Properties.TryGetValue("error", out var errorValue) ? errorValue : "authorization_failed";
            throw new InvalidOperationException($"Google認証に失敗しました: {error}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new LibrarySyncProgress("認証トークンを取得中", "OAuthアクセストークン", Detail: "oauth2.googleapis.com"));
        var form = new Dictionary<string, string>
        {
            ["client_id"] = state.GoogleDriveClientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = BrowserRedirectUri
        };
        if (!string.IsNullOrWhiteSpace(state.GoogleDriveClientSecret))
        {
            form["client_secret"] = state.GoogleDriveClientSecret;
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(TokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google認証に失敗しました: {GetErrorMessage(body)}");
        }

        await SaveTokenResponseAsync(state, body, cancellationToken);
    }


    private async Task<string> GetValidAccessTokenAsync(EagleReaderState state, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken)
            && DateTimeOffset.UtcNow < _accessTokenExpiresAt.AddSeconds(-60))
        {
            return _accessToken;
        }

        if (string.IsNullOrWhiteSpace(state.GoogleDriveClientId))
        {
            throw new InvalidOperationException("Google OAuth Client ID が未設定です。");
        }

        var refreshToken = await GetRefreshTokenAsync();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new GoogleDriveReconnectRequiredException("Google Driveに接続してください。");
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = state.GoogleDriveClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };
        if (!string.IsNullOrWhiteSpace(state.GoogleDriveClientSecret))
        {
            form["client_secret"] = state.GoogleDriveClientSecret;
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(TokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (string.Equals(GetErrorCode(body), "invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                await SecureStorage.Default.SetAsync(RefreshTokenKey, string.Empty);
                throw new GoogleDriveReconnectRequiredException("Google Driveの認証期限が切れたか、Google側で取り消されています。もう一度Google Driveに接続してください。");
            }

            throw new InvalidOperationException($"Google Driveの再接続に失敗しました: {GetErrorMessage(body)}");
        }

        await SaveTokenResponseAsync(state, body, cancellationToken, keepExistingRefreshToken: true);
        return _accessToken ?? throw new InvalidOperationException("Google Driveのアクセストークンを取得できませんでした。");
    }


    private async Task SaveTokenResponseAsync(
        EagleReaderState state,
        string body,
        CancellationToken cancellationToken,
        bool keepExistingRefreshToken = false)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        _accessToken = GetString(root, "access_token");
        var expiresIn = GetInt32(root, "expires_in", 3600);
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        var refreshToken = GetString(root, "refresh_token");
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await SecureStorage.Default.SetAsync(RefreshTokenKey, refreshToken);
        }
        else if (!keepExistingRefreshToken)
        {
            throw new InvalidOperationException("Google Driveの更新トークンを取得できませんでした。");
        }

        state.GoogleDriveConnectedAt = DateTimeOffset.UtcNow;
    }


    private static async Task<string?> GetRefreshTokenAsync()
    {
        try
        {
            var token = await SecureStorage.Default.GetAsync(RefreshTokenKey);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    private static Uri CreateAuthorizationUri(string clientId, string codeChallenge)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = BrowserRedirectUri,
            ["response_type"] = "code",
            ["scope"] = DriveReadonlyScope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        var query = string.Join("&", parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"{AuthorizationUrl}?{query}");
    }


    private static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64UrlEncode(bytes);
    }


    private static string CreateCodeChallenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }


    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }


    private static string GetErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return GetString(document.RootElement, "error");
        }
        catch
        {
            return string.Empty;
        }
    }


    private static string GetErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var description = GetString(root, "error_description");
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? body;
                }

                if (error.ValueKind == JsonValueKind.Object)
                {
                    var message = GetString(error, "message");
                    return string.IsNullOrWhiteSpace(message) ? body : message;
                }
            }
        }
        catch
        {
            // The response may be plain text or HTML.
        }

        return body.Length > 300 ? body[..300] : body;
    }
}
