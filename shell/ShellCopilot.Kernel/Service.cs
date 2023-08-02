using Azure;
using Azure.Core;
using Azure.AI.OpenAI;
using SharpToken;

namespace ShellCopilot.Kernel;

internal class ChatResponse
{
    internal ChatResponse(ChatChoice choice)
    {
        Content = choice.Message.Content;
        FinishReason = choice.FinishReason;
    }

    internal string Content { get; }
    internal CompletionsFinishReason FinishReason { get; }
}

internal class BackendService
{
    // TODO: Maybe expose this to our model registration?
    // We can still use 250 as the default value.
    private const int MaxResponseToken = 250;

    private OpenAIClient _client;
    private AIModel _activeModel;

    private readonly string _chatHistoryFile;
    private readonly List<ChatMessage> _chatHistory;
    private readonly ServiceConfig _config;

    internal BackendService(bool loadChatHistory, string chatHistoryFile)
    {
        _config = ServiceConfig.ReadFromConfigFile();
        _chatHistory = new List<ChatMessage>();
        _chatHistoryFile = chatHistoryFile ??
            Path.Combine(Utils.AppConfigHome, $"{Utils.AppName}.history.{Utils.GetParentProcessId()}");

        RefreshOpenAIClient();
    }

    internal ServiceConfig Configuration => _config;

    // TODO: chat history loading/saving
    private void LoadChatHistory()
    {

    }

    private void SaveChatHistory()
    {
        if (_chatHistory.Count is 0)
        {
            return;
        }
    }

    private void RefreshOpenAIClient()
    {
        AIModel modelInUse = _config.GetModelInUse();
        if (_activeModel == modelInUse)
        {
            // Active model was not changed.
            return;
        }

        _activeModel = modelInUse;
        var clientOptions = new OpenAIClientOptions() { RetryPolicy = new ApimRetryPolicy() };
        bool isApimEndpoint = _activeModel.Endpoint.TrimEnd('/').EndsWith(Utils.ApimGatewayDomain);

        if (isApimEndpoint)
        {
            string userkey = Utils.GetDataFromSecureString(_activeModel.Key);
            clientOptions.AddPolicy(
                new UserKeyPolicy(
                    new AzureKeyCredential(userkey),
                    Utils.ApimAuthorizationHeader),
                HttpPipelinePosition.PerRetry
            );
        }

        string azOpenAIApiKey = isApimEndpoint
            ? "placeholder-api-key"
            : Utils.GetDataFromSecureString(_activeModel.Key);

        _client = new(
            new Uri(_activeModel.Endpoint),
            new AzureKeyCredential(azOpenAIApiKey),
            clientOptions);
    }

    private int CountTokenForMessages(IEnumerable<ChatMessage> messages)
    {
        ModelDetail modelDetail = _activeModel.ModelDetail;
        GptEncoding encoding = modelDetail.GptEncoding;
        int tokensPerMessage = modelDetail.TokensPerMessage;
        int tokensPerName = modelDetail.TokensPerName;

        int tokenNumber = 0;
        foreach (ChatMessage message in messages)
        {
            tokenNumber += tokensPerMessage;
            tokenNumber += encoding.Encode(message.Role.ToString()).Count;
            tokenNumber += encoding.Encode(message.Content).Count;

            if (message.Name is not null)
            {
                tokenNumber += tokensPerName;
                tokenNumber += encoding.Encode(message.Name).Count;
            }
        }

        // Every reply is primed with <|start|>assistant<|message|>, which takes 3 tokens.
        tokenNumber += 3;
        return tokenNumber;
    }

    private void ReduceChatHistoryAsNeeded(List<ChatMessage> history, ChatMessage input)
    {
        int totalTokens = CountTokenForMessages(Enumerable.Repeat(input, 1));
        int tokenLimit = _activeModel.ModelDetail.TokenLimit;

        if (totalTokens + MaxResponseToken >= tokenLimit)
        {
            var message = $"The input is too long to get a proper response without exceeding the token limit ({tokenLimit}).\nPlease reduce the input and try again.";
            throw new InvalidOperationException(message);
        }

        history.Add(input);
        totalTokens = CountTokenForMessages(history);

        while (totalTokens + MaxResponseToken >= tokenLimit)
        {
            history.RemoveAt(0);
            totalTokens = CountTokenForMessages(history);
        }
    }

    private ChatCompletionsOptions PrepareForChatCompletion(string input, bool insertToHistory)
    {
        // Refresh the client in case the active model was changed.
        RefreshOpenAIClient();

        // TODO: Shall we expose some of the setting properties to our model registration?
        //  - max_tokens
        //  - temperature
        //  - top_p
        //  - presence_penalty
        //  - frequency_penalty
        // Those settings seem to be important enough, as the Semantic Kernel plugin specifies
        // those settings (see the URL below). We can use default values when not defined.
        // https://github.com/microsoft/semantic-kernel/blob/main/samples/skills/FunSkill/Joke/config.json
        ChatCompletionsOptions chatOptions = new()
        {
            ChoiceCount = 1,
            Temperature = (float)0.7,
            MaxTokens = MaxResponseToken,
        };

        List<ChatMessage> history = insertToHistory ? _chatHistory : new List<ChatMessage>();
        if (history.Count is 0)
        {
            history.Add(new ChatMessage(ChatRole.System, _activeModel.SystemPrompt));
        }

        ReduceChatHistoryAsNeeded(history, new ChatMessage(ChatRole.User, input));
        foreach (ChatMessage message in history)
        {
            chatOptions.Messages.Add(message);
        }

        return chatOptions;
    }

    public ChatResponse GetChatResponse(string input, bool insertToHistory = true)
    {
        ChatCompletionsOptions chatOptions = PrepareForChatCompletion(input, insertToHistory);
        Response<ChatCompletions> response = _client.GetChatCompletions(_activeModel.Deployment, chatOptions);
        return new ChatResponse(response.Value.Choices[0]);
    }

    public async Task<ChatResponse> GetChatResponseAsync(string input, bool insertToHistory = true, CancellationToken cancellationToken = default)
    {
        ChatCompletionsOptions chatOptions = PrepareForChatCompletion(input, insertToHistory);
        Response<ChatCompletions> response = await _client.GetChatCompletionsAsync(_activeModel.Deployment, chatOptions, cancellationToken);
        return new ChatResponse(response.Value.Choices[0]);
    }
}
