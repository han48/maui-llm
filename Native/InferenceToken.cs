namespace AIAgentLocal.Native;

/// <summary>
/// Represents a streamed token from inference, tagged as either Think or Response content.
/// </summary>
public readonly struct InferenceToken
{
    /// <summary>The text piece generated.</summary>
    public string Text { get; }

    /// <summary>True if this token is part of the thinking/reasoning block.</summary>
    public bool IsThinking { get; }

    /// <summary>True if this token is part of the final response.</summary>
    public bool IsResponse => !IsThinking;

    public InferenceToken(string text, bool isThinking)
    {
        Text = text;
        IsThinking = isThinking;
    }
}
