using SQLite;
using AIAgentLocal.Models;

namespace AIAgentLocal.Services;

/// <summary>
/// Persists chat history to SQLite database. Each conversation is a separate .db file.
/// </summary>
public class ChatHistoryService
{
    private static readonly string ConversationsDir = Path.Combine(FileSystem.AppDataDirectory, "Conversations");
    private const string CurrentConvPrefKey = "current_conversation_id";

    private SQLiteConnection? _db;
    private string _currentId = "";

    public string CurrentId => _currentId;

    public ChatHistoryService()
    {
        Directory.CreateDirectory(ConversationsDir);
        var savedId = Preferences.Get(CurrentConvPrefKey, "");
        if (!string.IsNullOrEmpty(savedId) && File.Exists(GetDbPath(savedId)))
            OpenConversation(savedId);
        else
            NewConversation();
    }

    private string GetDbPath(string id) => Path.Combine(ConversationsDir, $"{id}.db");

    private void OpenDb(string id)
    {
        _db?.Close();
        _currentId = id;
        _db = new SQLiteConnection(GetDbPath(id));
        _db.CreateTable<ChatRecord>();
        Preferences.Set(CurrentConvPrefKey, id);
    }

    /// <summary>Create a new conversation.</summary>
    public void NewConversation()
    {
        var id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        OpenDb(id);
    }

    /// <summary>Open an existing conversation by ID.</summary>
    public void OpenConversation(string id)
    {
        OpenDb(id);
    }

    /// <summary>Get list of all conversations (newest first).</summary>
    public List<ConversationInfo> GetConversations()
    {
        try
        {
            var files = Directory.GetFiles(ConversationsDir, "*.db")
                .OrderByDescending(f => f)
                .ToList();

            var result = new List<ConversationInfo>();
            foreach (var file in files)
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var preview = GetPreview(file);
                var isCurrent = id == _currentId;
                result.Add(new ConversationInfo(id, preview, isCurrent));
            }
            return result;
        }
        catch
        {
            return new();
        }
    }

    private static string GetPreview(string dbPath)
    {
        try
        {
            using var db = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadOnly);
            var first = db.Table<ChatRecord>().OrderBy(r => r.Id).FirstOrDefault();
            if (first != null)
            {
                var text = first.Content.Length > 40 ? first.Content[..40] + "..." : first.Content;
                return text;
            }
        }
        catch { }
        return "(trống)";
    }

    public void Save(ChatMessage message, string? rawResponse = null)
    {
        try
        {
            if (_db == null || message.IsGenerating || string.IsNullOrWhiteSpace(message.Content))
                return;

            _db.Insert(new ChatRecord
            {
                Content = message.TrimmedContent,
                ThinkContent = message.TrimmedThinkContent,
                RawResponse = rawResponse,
                IsUser = message.IsUser,
                Stats = message.Stats,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatHistory] Save failed: {ex.Message}");
        }
    }

    public void SavePair(ChatMessage userMsg, ChatMessage aiMsg, string? rawResponse = null)
    {
        Save(userMsg);
        Save(aiMsg, rawResponse);
    }

    public List<ChatMessage> LoadRecent(int count = 10)
    {
        try
        {
            if (_db == null) return new();

            var records = _db.Table<ChatRecord>()
                .OrderByDescending(r => r.Id)
                .Take(count)
                .ToList();

            records.Reverse();

            return records.Select(r => new ChatMessage(r.Content, r.IsUser)
            {
                ThinkContent = r.ThinkContent ?? "",
                Stats = r.Stats ?? ""
            }).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatHistory] Load failed: {ex.Message}");
            return new();
        }
    }

    /// <summary>
    /// Load recent conversation pairs (user, ai) for prompt history injection.
    /// Returns newest pairs last (chronological order).
    /// Includes think content for proper ChatML formatting.
    /// </summary>
    public List<(string User, string AiThink, string AiContent)> LoadRecentPairs(int maxPairs = 3)
    {
        try
        {
            if (_db == null) return new();

            var records = _db.Table<ChatRecord>()
                .OrderByDescending(r => r.Id)
                .Take(maxPairs * 2 + 2)
                .ToList();

            records.Reverse(); // chronological order

            var pairs = new List<(string User, string AiThink, string AiContent)>();
            for (int i = 0; i < records.Count - 1; i++)
            {
                if (records[i].IsUser && !records[i + 1].IsUser)
                {
                    pairs.Add((
                        records[i].Content,
                        records[i + 1].ThinkContent ?? "",
                        records[i + 1].Content
                    ));
                    i++; // skip the AI message
                }
            }

            return pairs.TakeLast(maxPairs).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatHistory] LoadRecentPairs failed: {ex.Message}");
            return new();
        }
    }

    /// <summary>
    /// Iterate messages from most recent, one at a time (for token budget calculation).
    /// Yields messages newest-first without loading all into memory.
    /// </summary>
    public IEnumerable<string> IterateRecentContents()
    {
        if (_db == null) yield break;

        var offset = 0;
        const int batchSize = 5;
        while (true)
        {
            var batch = _db.Table<ChatRecord>()
                .OrderByDescending(r => r.Id)
                .Skip(offset)
                .Take(batchSize)
                .ToList();

            if (batch.Count == 0) break;

            foreach (var r in batch)
                yield return r.Content ?? "";

            offset += batchSize;
        }
    }

    public void DeleteConversation(string id)
    {
        try
        {
            if (id == _currentId)
            {
                _db?.Close();
                _db = null;
            }
            var path = GetDbPath(id);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    [Table("chat_messages")]
    private class ChatRecord
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public string? ThinkContent { get; set; }
        public string? RawResponse { get; set; }
        public bool IsUser { get; set; }
        public string? Stats { get; set; }
        public DateTime Timestamp { get; set; }
    }
}

public record ConversationInfo(string Id, string Preview, bool IsCurrent);
