using CommunityToolkit.Mvvm.ComponentModel;

namespace AIAgentLocal.Models;

/// <summary>
/// Represents a single chat message in the conversation.
/// </summary>
public partial class ChatMessage : ObservableObject
{
    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _thinkContent = string.Empty;

    [ObservableProperty]
    private bool _isUser;

    /// <summary>
    /// Path to an attached image file (for vision model messages).
    /// </summary>
    [ObservableProperty]
    private string? _imagePath;

    /// <summary>
    /// Whether this message has an image attachment.
    /// </summary>
    public bool HasImage => !string.IsNullOrEmpty(ImagePath);

    /// <summary>
    /// ImageSource for displaying the attached image in the UI.
    /// </summary>
    public ImageSource? ImageSource => HasImage ? ImageSource.FromFile(ImagePath!) : null;

    public bool IsAi => !IsUser;

    public bool HasThinkContent => !string.IsNullOrWhiteSpace(ThinkContent);

    public string TrimmedContent => CleanText(Content, isThink: false);
    public string TrimmedThinkContent
    {
        get
        {
            var cleaned = CleanText(ThinkContent, isThink: true);
            return cleaned.Length < 50 ? string.Empty : cleaned;
        }
    }

    private static string CleanText(string text, bool isThink)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = text;

        if (isThink)
        {
            // For think content: if <think> exists, only take text after it
            var thinkIdx = result.LastIndexOf("<think>");
            if (thinkIdx >= 0)
                result = result[(thinkIdx + 7)..];
        }

        // Replace replacement character with blank
        result = result.Replace("\uFFFD", "");
        // Remove think tags and chat template tags
        result = result.Replace("<think>", "").Replace("</think>", "");
        result = result.Replace("<|im_start|>", "").Replace("<|im_end|>", "");
        result = result.Replace("|im_start|", "").Replace("|im_end|", "");
        return result.Trim();
    }

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private string _stats = string.Empty;

    public ChatMessage(string content, bool isUser)
    {
        Content = content;
        IsUser = isUser;
    }

    /// <summary>
    /// Parse raw model response into ThinkContent and Content.
    /// If &lt;think&gt;...&lt;/think&gt; block exists: inside = ThinkContent, after = Content.
    /// If no think tags: everything = Content.
    /// </summary>
    public void UpdateFromRawResponse(string oraw)
    {
        string raw = new(oraw.TrimStart());
        if (raw.StartsWith("inking>")) {
            raw = "<think>" + raw["inking>".Length..];
        } else if (raw.StartsWith("ink>")) {
            raw = "<think>" + raw["ink>".Length..];
        }
        raw = raw.Replace("</thinking>", "</think>");
        var thinkStart = raw.IndexOf("<think>");
        var thinkEnd = raw.IndexOf("</think>");

        if (thinkStart >= 0 && thinkEnd > thinkStart)
        {
            // Complete think block
            ThinkContent = raw[(thinkStart + 7)..thinkEnd];
            Content = raw[(thinkEnd + 8)..];
        }
        else if (thinkStart >= 0 && thinkEnd < 0)
        {
            // Think started but not closed — still thinking
            ThinkContent = raw[(thinkStart + 7)..];
            Content = "";
        }
        else if (thinkStart < 0 && thinkEnd >= 0)
        {
            // No <think> but has </think> — think tag was at very start (stripped or implicit)
            ThinkContent = raw[..thinkEnd];
            Content = raw[(thinkEnd + 8)..];
        }
        else
        {
            // No think tags — all is content
            ThinkContent = "";
            Content = raw;
        }
    }

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(TrimmedContent));
    }

    partial void OnThinkContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasThinkContent));
        OnPropertyChanged(nameof(TrimmedThinkContent));
    }

    partial void OnImagePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(ImageSource));
    }

    /// <summary>
    /// Build a ChatML prompt from system prompt and new user message.
    /// For Qwen3 models (supports /think and /no_think soft switch).
    /// </summary>
    public static string BuildPrompt(string systemPrompt, string userMessage, bool thinkingEnabled = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<|im_start|>system\n{systemPrompt}<|im_end|>\n");
        sb.Append($"<|im_start|>user\n{userMessage}{(thinkingEnabled ? " /think" : " /no_think")}<|im_end|>\n");
        sb.Append($"<|im_start|>assistant\n{(thinkingEnabled ? "<think>" : "")}");

        return sb.ToString();
    }

    /// <summary>
    /// Build a ChatML prompt for Qwen3.5 models.
    /// Qwen3.5 does NOT support /think or /no_think soft switch.
    /// Let the model decide whether to think - just end with assistant prefix.
    /// </summary>
    public static string BuildPromptQwen35(string systemPrompt, string userMessage, bool thinkingEnabled = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<|im_start|>system\n{systemPrompt}<|im_end|>\n");
        sb.Append($"<|im_start|>user\n{userMessage}<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n");

        return sb.ToString();
    }

    /// <summary>
    /// Build a ChatML prompt for Qwen3.5 vision (with image).
    /// Uses same vision tokens as Qwen3-VL.
    /// </summary>
    public static string BuildVisionPromptQwen35(string systemPrompt, string userMessage, bool thinkingEnabled = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<|im_start|>system\n{systemPrompt}<|im_end|>\n");
        sb.Append($"<|im_start|>user\n<|vision_start|><|image_pad|><|vision_end|>{userMessage}<|im_end|>\n");
        sb.Append($"<|im_start|>assistant\n{(thinkingEnabled ? "<think>\n" : "<think>\n")}");

        return sb.ToString();
    }

    /// <summary>
    /// Build a ChatML prompt for Qwen3.5 vision using mtmd marker.
    /// </summary>
    public static string BuildVisionPromptQwen35Mtmd(string systemPrompt, string userMessage, bool thinkingEnabled = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<|im_start|>system\n{systemPrompt}<|im_end|>\n");
        sb.Append("<|im_start|>user\n<__media__>");
        sb.Append($"{userMessage}<|im_end|>\n");
        sb.Append($"<|im_start|>assistant\n{(thinkingEnabled ? "<think>\n" : "<think>\n")}");

        return sb.ToString();
    }

    /// <summary>
    /// Build a ChatML prompt for vision models with image placeholder.
    /// The media marker will be replaced by the mtmd library with image embeddings.
    /// If mtmd is not available, the prompt uses Qwen3-VL native vision tokens.
    /// </summary>
    public static string BuildVisionPrompt(string systemPrompt, string userMessage, bool thinkingEnabled = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<|im_start|>system\n{systemPrompt}<|im_end|>\n");
        sb.Append($"<|im_start|>user\n<|vision_start|><|image_pad|><|vision_end|>{userMessage}{(thinkingEnabled ? " /think" : " /no_think")}<|im_end|>\n");
        sb.Append($"<|im_start|>assistant\n{(thinkingEnabled ? "<think>" : "")}");

        return sb.ToString();
    }

    /// <summary>
    /// Build a ChatML prompt for vision models using mtmd media marker.
    /// The mtmd library will replace the marker with proper vision tokens and image embeddings.
    /// </summary>
    public static string BuildVisionPromptMtmd(string systemPrompt, string userMessage, bool thinkingEnabled = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<|im_start|>system\n{systemPrompt}<|im_end|>\n");
        sb.Append("<|im_start|>user\n<__media__>");
        sb.Append($"{userMessage}{(thinkingEnabled ? " /think" : " /no_think")}<|im_end|>\n");
        sb.Append($"<|im_start|>assistant\n{(thinkingEnabled ? "<think>" : "")}");

        return sb.ToString();
    }

    /// <summary>
    /// Build a ChatML prompt for vision models with video (multiple frames).
    /// Each frame gets its own vision token block.
    /// </summary>
    public static string BuildVisionVideoPrompt(string systemPrompt, string userMessage, int frameCount, bool thinkingEnabled = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<|im_start|>system\n{systemPrompt}<|im_end|>\n");
        sb.Append("<|im_start|>user\n");
        // Video: multiple image_pad tokens wrapped in vision_start/end
        sb.Append("<|vision_start|>");
        for (int i = 0; i < frameCount; i++)
            sb.Append("<|image_pad|>");
        sb.Append("<|vision_end|>\n");
        sb.Append($"{userMessage}{(thinkingEnabled ? " /think" : " /no_think")}<|im_end|>\n");
        sb.Append($"<|im_start|>assistant\n{(thinkingEnabled ? "<think>" : "")}");

        return sb.ToString();
    }

    /// <summary>
    /// Build a ChatML prompt for video using mtmd media markers (one per frame).
    /// </summary>
    public static string BuildVisionVideoPromptMtmd(string systemPrompt, string userMessage, int frameCount, bool thinkingEnabled = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<|im_start|>system\n{systemPrompt}<|im_end|>\n");
        sb.Append("<|im_start|>user\n");
        for (int i = 0; i < frameCount; i++)
            sb.Append("<__media__>\n");
        sb.Append($"{userMessage}{(thinkingEnabled ? " /think" : " /no_think")}<|im_end|>\n");
        sb.Append($"<|im_start|>assistant\n{(thinkingEnabled ? "<think>" : "")}");

        return sb.ToString();
    }   
}
