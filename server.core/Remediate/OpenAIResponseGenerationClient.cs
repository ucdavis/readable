#pragma warning disable OPENAI001

using System.ClientModel;
using OpenAI;
using OpenAI.Responses;

namespace server.core.Remediate;

internal interface IOpenAIResponseGenerationClient
{
    Task<string> CreateResponseAsync(CreateResponseOptions options, CancellationToken cancellationToken);
}

internal sealed class OpenAIResponseGenerationClient : IOpenAIResponseGenerationClient
{
    private readonly ResponsesClient _client;

    public OpenAIResponseGenerationClient(string apiKey)
        : this(apiKey, endpoint: null)
    {
    }

    public OpenAIResponseGenerationClient(string apiKey, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _client = new ResponsesClient(apiKey);
            return;
        }

        _client = new ResponsesClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = NormalizeEndpoint(endpoint),
            });
    }

    public async Task<string> CreateResponseAsync(CreateResponseOptions options, CancellationToken cancellationToken)
    {
        ClientResult<ResponseResult> result = await _client.CreateResponseAsync(options, cancellationToken);
        return result.Value.GetOutputText();
    }

    private static Uri NormalizeEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint);
        if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
        {
            return new Uri(uri, "/v1");
        }

        return uri;
    }
}

internal static class OpenAIResponseOptions
{
    public const int TitleMaxOutputTokens = 256;
    public const int AltTextMaxOutputTokens = 256;
    public const int LinkAltTextMaxOutputTokens = 256;
    public const int TableClassificationMaxOutputTokens = 1024;
    public const int CharacterEncodingRepairMaxOutputTokens = 8192;

    public static CreateResponseOptions Create(
        string model,
        string remediationTask,
        int maxOutputTokenCount,
        ResponseTextFormat? textFormat = null)
    {
        var options = new CreateResponseOptions
        {
            Model = model,
            StoredOutputEnabled = false,
            MaxOutputTokenCount = maxOutputTokenCount,
            ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = ResponseReasoningEffortLevel.Low,
            },
        };

        options.Metadata.Add("feature", "pdf_remediation");
        options.Metadata.Add("task", remediationTask);

        if (textFormat is not null)
        {
            options.TextOptions = new ResponseTextOptions
            {
                TextFormat = textFormat,
            };
        }

        return options;
    }

    public static ResponseTextFormat CreateJsonSchemaFormat(string name, BinaryData schema)
        => ResponseTextFormat.CreateJsonSchemaFormat(
            jsonSchemaFormatName: name,
            jsonSchema: schema,
            jsonSchemaIsStrict: true);

    public static ResponseContentPart CreateInputImagePart(byte[] imageBytes, string mimeType)
    {
        var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(imageBytes)}";
        return ResponseContentPart.CreateInputImagePart(new Uri(dataUri), imageDetailLevel: null);
    }
}
