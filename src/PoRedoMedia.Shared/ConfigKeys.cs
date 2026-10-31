namespace PoRedoMedia.Shared;

/// <summary>Configuration key names. Read configuration through these, never string literals.</summary>
public static class ConfigKeys
{
    public const string AzureAdClientId = "AzureAd:ClientId";
    public const string AzureAdClientSecret = "AzureAd:ClientSecret";
    public const string AzureAdTenantId = "AzureAd:TenantId";
    public const string AzureAdCallbackPath = "AzureAd:CallbackPath";
    public const string AzureAdSignedOutCallbackPath = "AzureAd:SignedOutCallbackPath";
    public const string AzureAdAllowedTenantIds = "AzureAd:AllowedTenantIds";
    public const string AuthEnableFakeAuth = "Auth:EnableFakeAuth";

    /// <summary>Comma-separated email addresses that may sign in. Empty = any Microsoft account.</summary>
    public const string AuthAllowedEmails = "Auth:AllowedEmails";

    /// <summary>The address share links are built on, such as https://example.com. Empty = the request's own host.</summary>
    public const string AppPublicBaseUrl = "App:PublicBaseUrl";
    public const string KeyVaultUri = "KeyVault:Uri";
    public const string MocksUseMockAi = "Mocks:UseMockAi";
    public const string StorageConnectionString = "Storage:ConnectionString";
    public const string RenderQuotaDailyLimit = "RenderQuota:DailyLimit";

    /// <summary>Image runs (remote model waits, no render) executed at once. Video runs always go one at a time.</summary>
    public const string RunsRemoteConcurrency = "Runs:RemoteConcurrency";
    public const string RetentionDays = "Retention:Days";
    public const string HousekeepingEnabled = "Housekeeping:Enabled";

    /// <summary>Where traces, logs and metrics go. Empty = not sent anywhere.</summary>
    public const string ApplicationInsightsConnectionString = "ApplicationInsights:ConnectionString";

    public const string OpenAiEndpoint = "OpenAI:Endpoint";
    public const string OpenAiKey = "OpenAI:Key";
    public const string OpenAiChatDeployment = "OpenAI:ChatCompletionsDeployment";
    public const string AiFoundryEndpoint = "AiFoundry:Endpoint";
    public const string AiFoundryKey = "AiFoundry:Key";
    public const string AiFoundryTtsDeployment = "AiFoundry:TtsDeployment";

    /// <summary>The Foundry image model that draws when Gemini cannot (FLUX.1 Kontext). Empty = no stand-in.</summary>
    public const string AiFoundryImageDeployment = "AiFoundry:ImageDeployment";

    public const string GoogleApiKey = "Google:ApiKey";
    public const string GoogleVisionModel = "Google:VisionModel";
    public const string GoogleImageModel = "Google:ImageModel";
    public const string GoogleLyriaModel = "Google:LyriaModel";
    /// <summary>The model that turns a picture into a clip (Interactions API). Empty = gemini-omni-1.1-flash.</summary>
    public const string GoogleVideoModel = "Google:VideoModel";

    /// <summary>The model that watches a whole video for Meme-ify. Empty = <see cref="GoogleVisionModel"/>.</summary>
    public const string GoogleClipVisionModel = "Google:ClipVisionModel";

    public const string VisionSecondOpinion = "Vision:SecondOpinion";
}
