using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace MeshWeaver.Blazor.Chat;

public enum ChatPosition
{
    Right,
    Left,
    Bottom
}

public partial class AgentChatView : BlazorView<AgentChatControl, AgentChatView>
{
    private Lazy<Task<IAgentChat>> lazyChat;
    private CancellationTokenSource currentResponseCancellation = new();
    private ChatMessage? currentResponseMessage;
    private ChatInput? chatInput;
    private ChatHistorySelector? chatHistorySelector;
    private readonly List<ChatMessage> messages = new();
    // Chat persistence properties
    private string? currentConversationId;
    private ChatConversation? currentConversation;
    private bool isLoadingConversation;
    private bool isGeneratingResponse;

    // Bound context from the control
    private readonly AgentContext? boundContext;

    // Bound title from the control
    private readonly string chatTitle = "AI Chat";

    [Parameter] public bool UseStreaming { get; set; } = true;
    // Chat history panel state
    private bool showChatHistory;

    // Chat position state
    private ChatPosition currentPosition = ChatPosition.Right;
    private bool positionMenuVisible = false;
    [Parameter] public EventCallback<ChatPosition> OnPositionChanged { get; set; }
    [Parameter] public EventCallback<ChatMessage> OnMessageAdded { get; set; }

    private async Task OnNewMessageReceived(ChatMessage message)
    {
        // Handle new message events (e.g., auto-scroll, notifications, analytics)
        if (OnMessageAdded.HasDelegate)
        {
            await OnMessageAdded.InvokeAsync(message);
        }
    }


    protected override async Task OnInitializedAsync()
    {
        // Remove padding/margin from body-content when this is a standalone chat page
        var currentPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        if (currentPath == "chat")
        {
            await JSRuntime.InvokeVoidAsync("eval", @"
                const bodyContent = document.querySelector('.body-content, .custom-body-content');
                if (bodyContent) {
                    bodyContent.style.padding = '0';
                    bodyContent.style.margin = '0';
                }
            ");
        }

        // Try to load the most recent conversation on startup
        try
        {
            var conversations = await ChatPersistenceService.GetConversationsAsync();
            var mostRecent = conversations.OrderByDescending(c => c.LastModifiedAt).FirstOrDefault();

            if (mostRecent != null)
            {
                await LoadConversation(mostRecent.Id);
            }
            else
            {
                await StartNewConversationAsync();
            }
        }
        catch
        {
            // If there's an error loading conversations, start fresh
            await StartNewConversationAsync();
        }
    }

    public AgentChatView()
    {
        lazyChat = GetLazyChat();
    }

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Context, x => x.boundContext);
        DataBind(ViewModel.Title, x => x.chatTitle, defaultValue: "AI Chat");
    }

    private Lazy<Task<IAgentChat>> GetLazyChat()
    {
        return new(() => AgentChatFactory.CreateAsync());
    }

    // Public method to allow parent components to reset the conversation
    public async Task ResetConversationAsync()
    {
        await StartNewConversationAsync();
    }
    private async Task StartNewConversationAsync()
    {
        CancelAnyCurrentResponse();
        // Clear current state
        currentConversationId = null;
        currentConversation = null;
        messages.Clear();
        lazyChat = GetLazyChat();

        // Set a new thread ID for the new conversation
        var chat = await lazyChat.Value;
        chat.SetThreadId(Guid.NewGuid().AsString());

        if (chatInput != null)
        {
            await chatInput.FocusAsync();
        }

        // Close chat history when starting new conversation
        showChatHistory = false;
        StateHasChanged();
    }

    private void ToggleChatHistory()
    {
        showChatHistory = !showChatHistory;
        StateHasChanged();
    }

    private void CloseChatHistory()
    {
        showChatHistory = false;
        StateHasChanged();
    }
    private async Task OnConversationSelectionChanged(string conversationId)
    {
        if (conversationId != currentConversationId)
        {
            if (string.IsNullOrEmpty(conversationId))
            {
                await StartNewConversationAsync();
            }
            else
            {
                await LoadConversation(conversationId);
            }

            // Close the chat history panel after selection
            showChatHistory = false;
            StateHasChanged();
        }
    }
    private async Task LoadConversation(string conversationId)
    {
        if (isLoadingConversation) return; try
        {
            isLoadingConversation = true;
            StateHasChanged(); // Show loading spinner immediately
            CancelAnyCurrentResponse();

            var conversation = await ChatPersistenceService.LoadConversationAsync(conversationId);
            if (conversation != null)
            {
                currentConversationId = conversationId;
                currentConversation = conversation;

                // Load messages from the conversation
                messages.Clear();
                foreach (var persistedMessage in conversation.Messages)
                {
                    messages.Add(persistedMessage);
                } // Restore AgentChat with proper conversation history using the new persistence method
                var restoredChat = await ChatPersistenceService.RestoreAgentChatAsync(conversationId);

                // Set the thread ID to match the conversation ID
                restoredChat.SetThreadId(conversationId);

                lazyChat = new Lazy<Task<IAgentChat>>(() => Task.FromResult(restoredChat));

                StateHasChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading conversation: {ex.Message}");
            await StartNewConversationAsync();
        }
        finally
        {
            isLoadingConversation = false;
            StateHasChanged();
        }
    }
    private async Task SaveCurrentConversation()
    {
        if (messages.Any())
        {

            try
            {
                if (currentConversation == null)
                {
                    AgentContext? agentContext = null;

                    // Try to extract AgentContext from current conversation if there are any messages
                    if (lazyChat.IsValueCreated)
                    {
                        // TODO: Find a way to extract current context from the chat
                        // For now, we'll use the current URL-based context
                        agentContext = GetCurrentAgentContext();
                    }

                    currentConversation = new ChatConversation
                    {
                        Id = Guid.NewGuid().ToString(),
                        Messages = messages,
                        CreatedAt = DateTime.UtcNow,
                        LastModifiedAt = DateTime.UtcNow,
                        AgentContext = agentContext
                    };
                    currentConversationId = currentConversation.Id;
                }
                else
                {
                    AgentContext? agentContext = null;

                    // Try to extract AgentContext from current conversation if there are any messages
                    if (lazyChat.IsValueCreated)
                    {
                        // TODO: Find a way to extract current context from the chat
                        // For now, we'll use the current URL-based context
                        agentContext = GetCurrentAgentContext();
                    }
                    currentConversation = currentConversation with
                    {
                        Messages = messages,
                        LastModifiedAt = DateTime.UtcNow,
                        AgentContext = agentContext
                    };
                }

                // Get the current AgentChat instance
                var agentChat = lazyChat.IsValueCreated ? await lazyChat.Value : null;

                await ChatPersistenceService.SaveConversationAsync(currentConversation, agentChat);

                // Refresh the conversation list in the sidebar
                if (chatHistorySelector != null)
                {
                    await chatHistorySelector.RefreshConversations();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving conversation: {ex.Message}");
            }
        }
    }
    private async Task AddUserMessageAsync(ChatMessage userMessage)
    {
        // Cancel any existing response
        CancelAnyCurrentResponse();

        IAgentChat chat;
        try
        {
            chat = await lazyChat.Value;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("API key") || ex.Message.Contains("endpoint URL"))
        {
            // Handle configuration errors (missing API key, endpoint, etc.)
            var errorMessage = "❌ **Configuration Error**\n\n" +
                             "The AI service is not properly configured. Please check that:\n" +
                             "• The API key is set\n" +
                             "• The Endpoint URL is configured\n" +
                             "• The configuration section is properly loaded\n\n" +
                             "Please contact your administrator to configure the AI service.";

            var errorResponseText = new TextContent(errorMessage);
            var errorResponseMessage = new ChatMessage(new ChatRole("Assistant"), [errorResponseText]);

            // Add the user message to show what they tried to ask
            messages.Add(userMessage);
            messages.Add(errorResponseMessage);

            StateHasChanged();
            return;
        }
        catch (Exception ex)
        {
            // Handle other initialization errors
            var errorMessage = $"❌ **Service Error**\n\nFailed to initialize AI service: {ex.Message}\n\nPlease try again or contact your administrator if the problem persists.";

            var errorResponseText = new TextContent(errorMessage);
            var errorResponseMessage = new ChatMessage(new ChatRole("Assistant"), [errorResponseText]);

            // Add the user message to show what they tried to ask
            messages.Add(userMessage);
            messages.Add(errorResponseMessage);

            StateHasChanged();
            return;
        }

        SetAgentContext(chat);

        // Add the user message to the conversation
        messages.Add(userMessage);
        var lastRole = "Assistant";
        var responseText = new TextContent(string.Empty);
        currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText]);

        currentResponseCancellation = new();
        isGeneratingResponse = true;
        StateHasChanged(); // Ensure the UI updates to show the loading indicator

        try
        {
            if (UseStreaming)
                await InvokeStreamingAsync(userMessage, chat, lastRole, responseText);
            else
                await InvokeAsync(userMessage, chat, lastRole, responseText);
        }
        catch (Exception e)
        {
            if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
                // Store the final response in the conversation
                messages.Add(currentResponseMessage!);
            lastRole = "Assistant";
            responseText = new("An error has occurred during processing: " + e.Message);
            currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText]);
            ChatMessageItem.NotifyChanged(currentResponseMessage);
            CancelAnyCurrentResponse();
        }
        finally
        {
            // Store the final response in the conversation
            if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
            {
                messages.Add(currentResponseMessage!);
                currentResponseMessage = null;
                await SaveCurrentConversation();

            }
            // Re-enable the input and focus it
            if (chatInput != null)
            {
                await chatInput.FocusAsync();
            }

            isGeneratingResponse = false;
            StateHasChanged();
        }
    }

    private async Task InvokeAsync(ChatMessage userMessage, IAgentChat chat, string lastRole, TextContent responseText)
    {
        // Stream and display a new response from the IChatClient
        await foreach (var update in chat.GetResponseAsync([userMessage], currentResponseCancellation.Token))
        {


            if (lastRole == update.AuthorName)
            {
                messages.AddMessages(new ChatResponseUpdate(new(update.AuthorName ?? update.Role.Value), update.Text), filter: c => c is not TextContent);
                responseText.Text += update.Text;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
                    // Store the final response in the conversation
                    messages.Add(currentResponseMessage!);
                lastRole = update.AuthorName ?? "Assistant";
                responseText = new TextContent(update.Text);
                currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText]);
            }

            StateHasChanged();

            if (currentResponseMessage != null)
                ChatMessageItem.NotifyChanged(currentResponseMessage);

        }

    }
    private async Task InvokeStreamingAsync(ChatMessage userMessage, IAgentChat chat, string lastRole, TextContent responseText)
    {
        // Stream and display a new response from the IChatClient
        await foreach (var update in chat.GetStreamingResponseAsync([userMessage], currentResponseCancellation.Token))
        {
            var currentAuthor = update.AuthorName ?? "Assistant";



            if (lastRole == currentAuthor)
            {
                // Same author - concatenate to existing message
                responseText.Text += update.Text;
                messages.AddMessages(update, filter: c => c is not TextContent);
            }
            else
            {
                // Different author - finalize previous message and start new one
                if (!string.IsNullOrWhiteSpace(currentResponseMessage?.Text))
                {
                    // Store the final response in the conversation
                    messages.Add(currentResponseMessage!);
                }

                // Start new message with new author
                lastRole = currentAuthor;
                responseText = new TextContent(update.Text);
                currentResponseMessage = new ChatMessage(new ChatRole(lastRole), [responseText])
                {
                    AuthorName = currentAuthor
                };
            }

            if (currentResponseMessage != null)
                ChatMessageItem.NotifyChanged(currentResponseMessage);

            StateHasChanged();
        }

        // Delegation messages are now added immediately during streaming, no need to defer them
    }

    private void SetAgentContext(IAgentChat chat)
    {
        // Get the current context (either from control binding or URL)
        var context = GetCurrentAgentContext();

        // Always set the context (even if null) to ensure it's updated
        chat.SetContext(context);
    }

    private AgentContext? GetCurrentAgentContext()
    {
        // If boundContext is set (via control binding), use it
        // Otherwise, get it from the current URL
        return boundContext ?? GetContextFromCurrentUrl();
    }

    private AgentContext? GetContextFromCurrentUrl()
    {
        var path = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);

        Logger.LogDebug("Current URL path: '{Path}'", path);

        // Skip if path is empty or just "chat"
        if (string.IsNullOrEmpty(path) || path == "chat")
        {
            Logger.LogDebug("Path is empty or 'chat', returning null context");
            return null;
        }

        // Split the path into segments
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Logger.LogDebug("Path segments: {Segments} (count: {Count})", string.Join(", ", segments), segments.Length);

        // Need at least addressType and addressId
        if (segments.Length < 2)
        {
            Logger.LogDebug("Not enough segments for context, returning null");
            return null;
        }

        var addressType = segments[0];
        var addressId = segments[1];

        // Create the Address with the extracted values
        var address = new Address(addressType, addressId);

        var layoutArea = segments.Length == 2 ? null : new LayoutAreaReference(segments[2])
        {
            Id = string.Join('/', segments.Skip(3))
        };

        var context = new AgentContext
        {
            Address = address,
            LayoutArea = layoutArea
        };

        Logger.LogDebug("Created context - Address: {Address}, LayoutArea: {LayoutArea}", address, layoutArea?.Area);

        return context;
    }

    private void CancelAnyCurrentResponse()
    {
        // If a response was cancelled while streaming, include it in the conversation so it's not lost
        if (currentResponseMessage is not null)
        {
            messages.Add(currentResponseMessage);
        }

        currentResponseCancellation.Cancel();
        currentResponseCancellation = new();
        currentResponseMessage = null;
        isGeneratingResponse = false;
        StateHasChanged();
    }

    private async Task CancelCurrentResponse()
    {
        CancelAnyCurrentResponse();

        // Re-enable the input and focus it
        if (chatInput != null)
        {
            await chatInput.FocusAsync();
        }
    }
    [Parameter] public EventCallback OnCloseRequested { get; set; }

    private async Task CloseChatAsync()
    {
        // If parent component provided a close callback, use it
        if (OnCloseRequested.HasDelegate)
        {
            await OnCloseRequested.InvokeAsync();
        }
        else
        {
            // Otherwise, navigate away from the chat page
            NavigationManager.NavigateTo("/");
        }
    }

    private async Task ChangeChatPosition(ChatPosition newPosition)
    {
        positionMenuVisible = false;

        if (currentPosition != newPosition)
        {
            currentPosition = newPosition;
            StateHasChanged(); // Update UI immediately

            if (OnPositionChanged.HasDelegate)
            {
                await OnPositionChanged.InvokeAsync(currentPosition);
            }
        }
        else
        {
            StateHasChanged();
        }
    }

    public void Dispose()
    => currentResponseCancellation.Cancel();


}
