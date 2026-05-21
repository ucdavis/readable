#pragma warning disable OPENAI001

using System.ClientModel;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenAI.Responses;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Table;
using server.core.Remediate.Title;

namespace server.tests.Remediate;

public sealed class OpenAIRemediationResponseOptionsTests
{
    [Fact]
    public void ResponseGenerationClient_WhenEndpointMissing_UsesUsRegionalEndpoint()
    {
        var client = new OpenAIResponseGenerationClient("test-key");
        var responsesClient = GetResponsesClient(client);

        responsesClient.Endpoint.Should().Be(new Uri("https://us.api.openai.com/v1"));
    }

    [Fact]
    public void ResponseGenerationClient_WhenEndpointProvided_UsesConfiguredEndpoint()
    {
        var client = new OpenAIResponseGenerationClient("test-key", "https://us.api.openai.com");
        var responsesClient = GetResponsesClient(client);

        responsesClient.Endpoint.Should().Be(new Uri("https://us.api.openai.com/v1"));
    }

    [Fact]
    public async Task PdfTitleService_UsesResponsesOptions()
    {
        var client = new CapturingResponseGenerationClient("Generated title");
        var service = new OpenAIPdfTitleService("test-model", client);

        var title = await service.GenerateTitleAsync(
            new PdfTitleRequest("Current", "Extracted document text.", "English"),
            CancellationToken.None);

        title.Should().Be("Generated title");
        var options = client.SingleRequest();
        AssertCommonOptions(options, "pdf_title", OpenAIResponseOptions.TitleMaxOutputTokens);

        using var json = Serialize(options);
        json.RootElement.GetProperty("input").GetArrayLength().Should().Be(1);
        json.RootElement.GetRawText().Should().Contain("Extracted document text.");
    }

    [Fact]
    public async Task AltTextService_UsesResponsesOptionsForImageInput()
    {
        var client = new CapturingResponseGenerationClient("\"Accessible chart summary\"");
        var service = new OpenAIAltTextService("test-model", client);

        var altText = await service.GetAltTextForImageAsync(
            new ImageAltTextRequest([1, 2, 3], "image/png", "Before", "After", "English"),
            CancellationToken.None);

        altText.Should().Be("Accessible chart summary");
        var options = client.SingleRequest();
        AssertCommonOptions(options, "pdf_image_alt_text", OpenAIResponseOptions.AltTextMaxOutputTokens);
        options.Instructions.Should().Contain("WCAG 2.1-compliant");

        using var json = Serialize(options);
        var requestJson = json.RootElement.GetRawText();
        requestJson.Should().Contain("\"type\":\"input_image\"");
        requestJson.Should().Contain("data:image/png;base64,AQID");
    }

    [Fact]
    public async Task TableClassificationService_UsesJsonSchemaResponsesOptions()
    {
        var client = new CapturingResponseGenerationClient(
            """
            {"kind":"data_table","confidence":0.9,"reason":"Rows contain comparable records."}
            """);
        var service = new OpenAIPdfTableClassificationService("test-model", client);

        var result = await service.ClassifyAsync(
            new PdfTableClassificationRequest(
                RowCount: 1,
                MaxColumnCount: 2,
                HasNestedTable: false,
                Rows: [["Header", "Value"]],
                PrimaryLanguage: "English"),
            CancellationToken.None);

        result.Kind.Should().Be(PdfTableKind.DataTable);
        var options = client.SingleRequest();
        AssertCommonOptions(
            options,
            "pdf_table_classification",
            OpenAIResponseOptions.TableClassificationMaxOutputTokens);
        AssertJsonSchemaOptions(options, "pdf_table_classification");
    }

    [Fact]
    public async Task CharacterEncodingRepairService_UsesJsonSchemaResponsesOptions()
    {
        var client = new CapturingResponseGenerationClient(
            """
            {"repairs":[{"fontObjectId":"12 0 R","sourceCode":"00E9","replacement":"e","confidence":0.8,"reason":"Context supports e."}]}
            """);
        var service = new OpenAIPdfCharacterEncodingRepairService("test-model", client);

        var result = await service.ProposeRepairsAsync(
            new PdfCharacterEncodingRepairRequest(
                [
                    new PdfCharacterEncodingAnomalyGroup(
                        "12 0 R",
                        "ABCDEF+Font",
                        "00e9",
                        "private_use",
                        [new PdfCharacterEncodingAnomalyContext(1, 2, "caf?", "The word", "appears here")]),
                ],
                "English"),
            CancellationToken.None);

        result.Repairs.Should().ContainSingle();
        var options = client.SingleRequest();
        AssertCommonOptions(
            options,
            "pdf_character_encoding_repairs",
            OpenAIResponseOptions.CharacterEncodingRepairMaxOutputTokens);
        AssertJsonSchemaOptions(options, "pdf_character_encoding_repairs");
    }

    [Fact]
    public async Task CharacterEncodingActualTextRepairService_UsesJsonSchemaResponsesOptions()
    {
        var client = new CapturingResponseGenerationClient(
            """
            {"repairs":[{"pageNumber":1,"mcid":2,"actualText":"Cafe","confidence":0.8,"reason":"Context supports Cafe."}]}
            """);
        var service = new OpenAIPdfCharacterEncodingRepairService("test-model", client);

        var result = await service.ProposeActualTextRepairsAsync(
            new PdfCharacterEncodingActualTextRepairRequest(
                [new PdfCharacterEncodingActualTextIssue(1, 2, "Caf?", "The word", "appears here")],
                "English"),
            CancellationToken.None);

        result.Repairs.Should().ContainSingle();
        var options = client.SingleRequest();
        AssertCommonOptions(
            options,
            "pdf_character_encoding_actual_text_repairs",
            OpenAIResponseOptions.CharacterEncodingRepairMaxOutputTokens);
        AssertJsonSchemaOptions(options, "pdf_character_encoding_actual_text_repairs");
    }

    private static void AssertCommonOptions(
        CreateResponseOptions options,
        string expectedTask,
        int expectedMaxOutputTokenCount)
    {
        options.Model.Should().Be("test-model");
        options.StoredOutputEnabled.Should().BeFalse();
        options.MaxOutputTokenCount.Should().Be(expectedMaxOutputTokenCount);
        options.ReasoningOptions.Should().NotBeNull();
        options.ReasoningOptions!.ReasoningEffortLevel.Should().Be(ResponseReasoningEffortLevel.Low);
        options.Metadata.Should().Contain("feature", "pdf_remediation");
        options.Metadata.Should().Contain("task", expectedTask);
    }

    private static void AssertJsonSchemaOptions(CreateResponseOptions options, string expectedSchemaName)
    {
        options.TextOptions.Should().NotBeNull();
        options.TextOptions!.TextFormat.Should().NotBeNull();
        options.TextOptions.TextFormat.Kind.Should().Be(ResponseTextFormatKind.JsonSchema);

        using var json = Serialize(options);
        var requestJson = json.RootElement.GetRawText();
        requestJson.Should().Contain("\"type\":\"json_schema\"");
        requestJson.Should().Contain($"\"name\":\"{expectedSchemaName}\"");
        requestJson.Should().Contain("\"strict\":true");
    }

    private static JsonDocument Serialize(CreateResponseOptions options)
    {
        BinaryContent content = options;
        using var stream = new MemoryStream();
        content.WriteTo(stream, CancellationToken.None);
        return JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static ResponsesClient GetResponsesClient(OpenAIResponseGenerationClient client)
    {
        var field = typeof(OpenAIResponseGenerationClient).GetField(
            "_client",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        field.Should().NotBeNull();
        return field!.GetValue(client).Should().BeOfType<ResponsesClient>().Subject;
    }

    private sealed class CapturingResponseGenerationClient : IOpenAIResponseGenerationClient
    {
        private readonly string _response;
        private readonly List<CreateResponseOptions> _requests = [];

        public CapturingResponseGenerationClient(string response)
        {
            _response = response;
        }

        public Task<string> CreateResponseAsync(CreateResponseOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(options);
            return Task.FromResult(_response);
        }

        public CreateResponseOptions SingleRequest()
        {
            _requests.Should().ContainSingle();
            return _requests[0];
        }
    }
}
