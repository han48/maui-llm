using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
#if !(IOS || ANDROID || MACCATALYST)
using LLama;
using LLama.Common;
#endif
using AIAgentLocal.Models;
#if (IOS || ANDROID || MACCATALYST)
using AIAgentLocal.Native;
#endif

namespace AIAgentLocal.ViewModels;

/// <summary>
/// ViewModel for the chat page. Manages messages, model loading, and inference.
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    // Constants for Windows/LLamaSharp fallback (mobile uses dynamic calculation)
    private const int FallbackMaxResponseTokens = 4096;
    private const int FallbackMaxHistory = 50;
    private const int FallbackContextSize = 2048;

    private string GetLocalizedSystemPrompt() => Services.L.Get("SystemPrompt");

#if (IOS || ANDROID || MACCATALYST)
    private LlamaCppEngine? _engine;
    private LlamaCppEngine? _visionEngine; // Paired Qwen3.5 vision model
    private MtmdEngine? _mtmdEngine;
#else
    private LLamaWeights? _model;
    private StatelessExecutor? _executor;
    private ModelParams? _modelParams;
#endif

    private readonly Services.ChatHistoryService _history = new();

    [ObservableProperty]
    private ObservableCollection<ChatMessage> _messages = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInput))]
    [NotifyPropertyChangedFor(nameof(IsNotGenerating))]
    private bool _isGenerating;

    [ObservableProperty]
    private bool _thinkingEnabled = false;

    /// <summary>Whether the currently loaded model is a vision model.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAttachButton))]
    private bool _isVisionModel;

    /// <summary>Path to the currently attached image (for vision models).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachedImage))]
    [NotifyPropertyChangedFor(nameof(AttachedImageSource))]
    [NotifyPropertyChangedFor(nameof(AttachedFileLabel))]
    private string? _attachedImagePath;

    /// <summary>Whether an image/video is currently attached.</summary>
    public bool HasAttachedImage => !string.IsNullOrEmpty(AttachedImagePath);

    /// <summary>ImageSource for the attached image preview (thumbnail for video).</summary>
    public ImageSource? AttachedImageSource => HasAttachedImage ? ImageSource.FromFile(AttachedImagePath!) : null;

    /// <summary>Label text for the attached file.</summary>
    public string AttachedFileLabel
    {
        get
        {
            if (!HasAttachedImage) return "";
            var ext = Path.GetExtension(AttachedImagePath!)?.ToLowerInvariant();
            if (ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm")
                return "🎬 Video đã đính kèm";
            return "🖼️ Ảnh đã đính kèm";
        }
    }

    /// <summary>Whether to show the attach button (show when model has vision capability).</summary>
    public bool ShowAttachButton => IsVisionModel || HasPairedVisionModel;

    /// <summary>Whether a paired vision engine is loaded.</summary>
    public bool HasPairedVisionModel
    {
        get
        {
#if (IOS || ANDROID || MACCATALYST)
            return _visionEngine != null && _visionEngine.IsLoaded;
#else
            return false;
#endif
        }
    }

    private CancellationTokenSource? _cts;

    public bool CanInput => IsModelLoaded && !IsGenerating;
    public bool IsNotGenerating => !IsGenerating;

    [ObservableProperty]
    private bool _isModelLoaded;

    [ObservableProperty]
    private string _statusText = "Đang tải model...";

    [ObservableProperty]
    private string _performanceText = string.Empty;

    public string PlaceholderText => Services.L.TypeMessage;
    public string SendButtonText => Services.L.Send;
    public string MenuChatHistory => Services.L.ChatHistory;
    public string MenuDeleteChat => Services.L.DeleteConversation;
    public string MenuModel => Services.L.SelectModel;
    public string MenuLanguage => Services.L.Get("Language");
    public string MenuDeviceInfo => Services.L.DeviceInfo;
    public string AppTitle => Services.L.Get("AppTitle");

    public ChatViewModel()
    {
        // Load chat history from previous session
        var history = _history.LoadRecent(10);
        foreach (var msg in history)
            Messages.Add(msg);

        // Delay model loading to ensure UI is ready (especially on iOS)
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // Wait for page to fully appear
            await MainThread.InvokeOnMainThreadAsync(() => LoadModelAsync());
        });
    }

    /// <summary>
    /// Finds the first .gguf file in the Models/ directory relative to the app.
    /// </summary>
    private static string? FindModelPath()
    {
        var searchPaths = new[]
        {
            Path.Combine(FileSystem.AppDataDirectory, "Models"),
            Path.Combine(AppContext.BaseDirectory, "Models"),
            Path.Combine(Directory.GetCurrentDirectory(), "Models"),
        };

        foreach (var dir in searchPaths)
        {
            if (!Directory.Exists(dir)) continue;
            var gguf = Directory.GetFiles(dir, "*.gguf").FirstOrDefault();
            if (gguf != null) return gguf;
        }

        return null;
    }

    private static List<ModelEntry>? _modelEntries;
    private static Dictionary<string, (string Url, string FileName, string DisplayName)>? _availableModels;

    private static List<ModelEntry> ModelEntries
    {
        get
        {
            if (_modelEntries == null)
                LoadModelsFromJson();
            return _modelEntries!;
        }
    }

    private static Dictionary<string, (string Url, string FileName, string DisplayName)> AvailableModels
    {
        get
        {
            if (_availableModels == null)
                LoadModelsFromJson();
            return _availableModels!;
        }
    }

    private static void LoadModelsFromJson()
    {
        try
        {
            using var stream = FileSystem.OpenAppPackageFileAsync("models.json").GetAwaiter().GetResult();
            using var reader = new System.IO.StreamReader(stream);
            var json = reader.ReadToEnd();
            var items = System.Text.Json.JsonSerializer.Deserialize<List<ModelEntry>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            _modelEntries = items;
            _availableModels = new Dictionary<string, (string, string, string)>();
            foreach (var item in items)
                _availableModels[item.Key] = (item.Url, item.FileName, item.DisplayName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AIAgentLocal] Failed to load models.json: {ex.Message}");
            _modelEntries = new();
            _availableModels = new Dictionary<string, (string, string, string)>
            {
                ["0.6B"] = ("https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q4_K_M.gguf", "Qwen3-0.6B-Q4_K_M.gguf", "Qwen3-0.6B (Q4_K_M)"),
            };
        }
    }

    private class ModelEntry
    {
        public string Key { get; set; } = "";
        public string Url { get; set; } = "";
        public string FileName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int SizeMB { get; set; }
        public int RequiredRamMB { get; set; }
        public bool IsVision { get; set; }
        public string MmProjUrl { get; set; } = "";
        public string MmProjFileName { get; set; } = "";
        public string PairedVisionKey { get; set; } = "";
    }

    private const string LastModelPrefKey = "last_model_key";

    private async Task LoadModelAsync()
    {
        string? modelPath = null;
        try
        {
            IsLoading = true;
            StatusText = Services.L.FindingModel;

            // Try to load last used model first
            var lastKey = Preferences.Get(LastModelPrefKey, "");
            if (!string.IsNullOrEmpty(lastKey) && AvailableModels.ContainsKey(lastKey))
            {
                var lastModel = AvailableModels[lastKey];
                var lastPath = Path.Combine(FileSystem.AppDataDirectory, "Models", lastModel.FileName);
                if (File.Exists(lastPath) && IsValidGgufFile(lastPath))
                    modelPath = lastPath;
            }

            // Fallback: find any downloaded model
            if (modelPath == null)
                modelPath = FindModelPath();

            // Ensure paired vision model is downloaded (even if text model already exists)
            var lastKeyForPair = Preferences.Get(LastModelPrefKey, "");
            var entryForPair = !string.IsNullOrEmpty(lastKeyForPair)
                ? ModelEntries.FirstOrDefault(m => m.Key == lastKeyForPair)
                : null;
            if (modelPath != null && !string.IsNullOrEmpty(entryForPair?.PairedVisionKey))
            {
                var visionEntry = ModelEntries.FirstOrDefault(m => m.Key == entryForPair.PairedVisionKey);
                if (visionEntry != null)
                {
                    var modelsDir = Path.Combine(FileSystem.AppDataDirectory, "Models");
                    var visionPath = Path.Combine(modelsDir, visionEntry.FileName);
                    if (!File.Exists(visionPath) || !IsValidGgufFile(visionPath))
                    {
                        StatusText = $"Đang tải vision model ({visionEntry.DisplayName})...";
                        await DownloadModelAsync(visionEntry.Url, visionEntry.FileName);
                    }
                    if (!string.IsNullOrEmpty(visionEntry.MmProjUrl))
                    {
                        var mmprojPath = Path.Combine(modelsDir, visionEntry.MmProjFileName);
                        if (!File.Exists(mmprojPath) || !IsValidGgufFile(mmprojPath))
                        {
                            StatusText = $"Đang tải vision encoder...";
                            await DownloadModelAsync(visionEntry.MmProjUrl, visionEntry.MmProjFileName);
                        }
                    }
                }
            }

            if (modelPath == null)
            {
                var selectedKey = await SelectModelAsync();
                if (selectedKey == null)
                {
                    StatusText = Services.L.NoModelSelected;
                    IsLoading = false;
                    return;
                }

                // Save preference
                Preferences.Set(LastModelPrefKey, selectedKey);

                var modelInfo = AvailableModels[selectedKey];
                StatusText = $"Đang tải {modelInfo.DisplayName}...";
                modelPath = await DownloadModelAsync(modelInfo.Url, modelInfo.FileName);
                if (modelPath == null)
                {
                    if (!StatusText.StartsWith("❌"))
                        StatusText = Services.L.CannotDownload;
                    IsLoading = false;
                    return;
                }

                // Download mmproj for vision models
                var entry = ModelEntries.FirstOrDefault(m => m.Key == selectedKey);
                if (entry?.IsVision == true && !string.IsNullOrEmpty(entry.MmProjUrl))
                {
                    StatusText = $"Đang tải vision encoder...";
                    var mmprojPath = await DownloadModelAsync(entry.MmProjUrl, entry.MmProjFileName);
                    if (mmprojPath == null)
                    {
                        StatusText = "⚠️ Không thể tải vision encoder. Model sẽ chạy ở chế độ text-only.";
                    }
                }

                // Download paired vision model + mmproj if this is a text model with a pair
                if (!string.IsNullOrEmpty(entry?.PairedVisionKey))
                {
                    var visionEntry = ModelEntries.FirstOrDefault(m => m.Key == entry.PairedVisionKey);
                    if (visionEntry != null)
                    {
                        var visionPath = Path.Combine(FileSystem.AppDataDirectory, "Models", visionEntry.FileName);
                        if (!File.Exists(visionPath) || !IsValidGgufFile(visionPath))
                        {
                            StatusText = $"Đang tải vision model ({visionEntry.DisplayName})...";
                            await DownloadModelAsync(visionEntry.Url, visionEntry.FileName);
                        }
                        // Download mmproj for paired vision model
                        if (!string.IsNullOrEmpty(visionEntry.MmProjUrl))
                        {
                            var mmprojPath2 = Path.Combine(FileSystem.AppDataDirectory, "Models", visionEntry.MmProjFileName);
                            if (!File.Exists(mmprojPath2) || !IsValidGgufFile(mmprojPath2))
                            {
                                StatusText = $"Đang tải vision encoder...";
                                await DownloadModelAsync(visionEntry.MmProjUrl, visionEntry.MmProjFileName);
                            }
                        }
                    }
                }
            }

            StatusText = $"Đang tải: {Path.GetFileName(modelPath)}...";

            // Check if device has enough RAM for this model
            var modelSizeMB = new FileInfo(modelPath).Length / (1024 * 1024);
            long ramMB = 0;
            if (long.TryParse(GetTotalMemoryMB(), out var parsed))
                ramMB = parsed;
            if (ramMB > 0 && modelSizeMB > ramMB * 0.6) // Model needs ~60% of total RAM minimum
            {
                StatusText = $"⚠️ Model {modelSizeMB}MB có thể quá lớn cho thiết bị ({ramMB}MB RAM). Thử model nhỏ hơn.";
                Console.WriteLine($"[AIAgentLocal] WARNING: Model {modelSizeMB}MB vs RAM {ramMB}MB");
            }

            await Task.Run(() =>
            {
#if (IOS || ANDROID || MACCATALYST)
                LlamaCppEngine.InitBackend();
                _engine = new LlamaCppEngine();
                _engine.LoadModel(modelPath);

                // Check model entry
                var currentModelKey = Preferences.Get(LastModelPrefKey, "");
                var entry = !string.IsNullOrEmpty(currentModelKey)
                    ? ModelEntries.FirstOrDefault(m => m.Key == currentModelKey)
                    : null;

                // Determine if this model has a paired vision model or is itself a vision model
                var isVision = entry?.IsVision == true
                    || !string.IsNullOrEmpty(entry?.PairedVisionKey)
                    || (entry == null && modelPath != null && Path.GetFileName(modelPath).Contains("VL", StringComparison.OrdinalIgnoreCase));

                MainThread.BeginInvokeOnMainThread(() => IsVisionModel = isVision);

                // Load paired vision model (Qwen3.5) if available
                if (!string.IsNullOrEmpty(entry?.PairedVisionKey))
                {
                    var visionEntry = ModelEntries.FirstOrDefault(m => m.Key == entry.PairedVisionKey);
                    if (visionEntry != null)
                    {
                        var visionModelPath = Path.Combine(FileSystem.AppDataDirectory, "Models", visionEntry.FileName);
                        if (File.Exists(visionModelPath))
                        {
                            try
                            {
                                _visionEngine?.Dispose();
                                _visionEngine = new LlamaCppEngine();
                                _visionEngine.LoadModel(visionModelPath);
                                Console.WriteLine($"[AIAgentLocal] Paired vision model loaded: {visionEntry.FileName}");
                                MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(ShowAttachButton)));

                                // Load mmproj for the vision model
                                if (!string.IsNullOrEmpty(visionEntry.MmProjFileName) && IsMtmdLibraryAvailable())
                                {
                                    var mmprojPath = Path.Combine(FileSystem.AppDataDirectory, "Models", visionEntry.MmProjFileName);
                                    if (File.Exists(mmprojPath))
                                    {
                                        try
                                        {
                                            _mtmdEngine?.Dispose();
                                            _mtmdEngine = new MtmdEngine();
                                            _mtmdEngine.LoadMmproj(mmprojPath, _visionEngine.ModelPtr);
                                            Console.WriteLine($"[AIAgentLocal] Vision mmproj loaded successfully");
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[AIAgentLocal] Vision mmproj failed: {ex.Message}");
                                            _mtmdEngine?.Dispose();
                                            _mtmdEngine = null;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[AIAgentLocal] Paired vision model failed: {ex.Message}");
                                _visionEngine?.Dispose();
                                _visionEngine = null;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[AIAgentLocal] Paired vision model not downloaded yet: {visionEntry.FileName}");
                        }
                    }
                }
                // Standalone vision model (VL-2B, VL-4B, Q35-* selected directly)
                else if (entry?.IsVision == true && !string.IsNullOrEmpty(entry.MmProjFileName) && IsMtmdLibraryAvailable())
                {
                    var mmprojPath = Path.Combine(FileSystem.AppDataDirectory, "Models", entry.MmProjFileName);
                    if (File.Exists(mmprojPath))
                    {
                        try
                        {
                            _mtmdEngine?.Dispose();
                            _mtmdEngine = new MtmdEngine();
                            _mtmdEngine.LoadMmproj(mmprojPath, _engine.ModelPtr);
                            Console.WriteLine($"[AIAgentLocal] mmproj loaded successfully");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[AIAgentLocal] mmproj load failed: {ex.Message}");
                            _mtmdEngine?.Dispose();
                            _mtmdEngine = null;
                        }
                    }
                }
#else
                _modelParams = new ModelParams(modelPath)
                {
                    ContextSize = 0, // 0 = auto from model
                    GpuLayerCount = 0
                };
                _model = LLamaWeights.LoadFromFile(_modelParams);
                _executor = new StatelessExecutor(_model, _modelParams);
                MainThread.BeginInvokeOnMainThread(() => IsVisionModel = false);
#endif
            });

            StatusText = $"✓ {Path.GetFileName(modelPath)}";
            IsModelLoaded = true;
        }
        catch (Exception ex)
        {
            var errorDetail = $"❌ {ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException != null)
                errorDetail += $"\n  Inner: {ex.InnerException.Message}";

            // If model load failed, delete the corrupt file so next attempt re-downloads
            if (modelPath != null && File.Exists(modelPath))
            {
                try
                {
                    File.Delete(modelPath);
                    errorDetail += "\n🔄 File đã xóa. Nhấn ⚙️ để tải lại.";
                    Console.WriteLine($"[AIAgentLocal] Deleted corrupt model: {modelPath}");
                }
                catch { }
            }

            StatusText = errorDetail;
            Console.WriteLine($"[AIAgentLocal] LoadModel FAILED: {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<string?> SelectModelAsync()
    {
        var modelsDir = Path.Combine(FileSystem.AppDataDirectory, "Models");
        long deviceRamMB = 0;
        if (long.TryParse(GetTotalMemoryMB(), out var parsed))
            deviceRamMB = parsed;

        // Filter models by device RAM and build display options
        // Hide paired vision models (Q35-*) from picker - they load automatically
        var eligibleModels = ModelEntries
            .Where(m => deviceRamMB <= 0 || m.RequiredRamMB <= deviceRamMB)
            .Where(m => string.IsNullOrEmpty(m.PairedVisionKey) || m.IsVision == false) // Show text models + standalone VL models
            .Where(m => !m.Key.StartsWith("Q35-")) // Hide Q35 models (auto-loaded as pairs)
            .ToList();

        if (eligibleModels.Count == 0)
            eligibleModels = ModelEntries.Take(2).ToList(); // At least show smallest models

        var options = eligibleModels.Select(m =>
        {
            var localPath = Path.Combine(modelsDir, m.FileName);
            var downloaded = File.Exists(localPath) && IsValidGgufFile(localPath);
            var ramLabel = m.RequiredRamMB >= 1024 ? $"{m.RequiredRamMB / 1024}GB RAM" : $"{m.RequiredRamMB}MB RAM";
            var label = $"{m.DisplayName} ~{m.SizeMB / 1024.0:F1}GB [{ramLabel}]";
            return downloaded ? $"✓ {label}" : label;
        }).ToArray();

        var result = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            // Wait for page to be ready (iOS may take longer on first launch)
            Page? page = null;
            for (int i = 0; i < 10; i++)
            {
                page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page != null) break;
                await Task.Delay(500);
            }
            if (page == null) return null;

            return await page.DisplayActionSheet(Services.L.SelectModel, Services.L.Cancel, null, options);
        });

        if (result == null || result == Services.L.Cancel) return null;
        var cleanResult = result.StartsWith("✓ ") ? result[2..] : result;
        var selected = eligibleModels.FirstOrDefault(m => cleanResult.Contains(m.DisplayName));
        return selected?.Key;
    }

    private async Task<string?> DownloadModelAsync(string url, string fileName)
    {
        try
        {
            var modelsDir = Path.Combine(FileSystem.AppDataDirectory, "Models");
            Directory.CreateDirectory(modelsDir);
            var localPath = Path.Combine(modelsDir, fileName);
            var tempPath = localPath + ".downloading";

            if (File.Exists(localPath) && IsValidGgufFile(localPath))
                return localPath;

            // Keep screen awake during download
            DeviceDisplay.Current.KeepScreenOn = true;

            // Support resume: check if partial download exists
            long existingBytes = 0;
            if (File.Exists(tempPath))
                existingBytes = new FileInfo(tempPath).Length;

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(4) };

            // Request with Range header for resume support
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existingBytes > 0)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            // If server doesn't support range, start over
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent && existingBytes > 0)
            {
                existingBytes = 0;
                File.Delete(tempPath);
            }
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength ?? -1;
            var totalBytes = contentLength > 0 ? contentLength + existingBytes : -1;
            var downloaded = existingBytes;

            // Save expected total size for validation later
            var metaPath = localPath + ".meta";
            if (totalBytes > 0)
                await File.WriteAllTextAsync(metaPath, totalBytes.ToString());

            await using (var stream = await response.Content.ReadAsStreamAsync())
            await using (var file = new FileStream(tempPath, existingBytes > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;

#if IOS
                var taskId = UIKit.UIApplication.SharedApplication.BeginBackgroundTask("ModelDownload", () => { });
#endif
                try
                {
                    while ((read = await stream.ReadAsync(buffer)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read));
                        downloaded += read;

                        if (totalBytes > 0)
                        {
                            var pct = (double)downloaded / totalBytes * 100;
                            MainThread.BeginInvokeOnMainThread(() =>
                                StatusText = $"Đang tải model... {pct:F0}% ({downloaded / (1024 * 1024)} MB)");
                        }
                    }
                }
                finally
                {
#if IOS
                    UIKit.UIApplication.SharedApplication.EndBackgroundTask(taskId);
#endif
                    DeviceDisplay.Current.KeepScreenOn = false;
                }
            } // file stream closed here

            // Validate download completeness
            var downloadedSize = new FileInfo(tempPath).Length;
            if (totalBytes > 0 && downloadedSize < totalBytes)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                    StatusText = $"{Services.L.DownloadIncomplete} ({downloadedSize * 100 / totalBytes}%)");
                return null;
            }

            // Rename temp to final
            if (File.Exists(localPath))
                File.Delete(localPath);
            File.Move(tempPath, localPath);

            // Cleanup meta file
            if (File.Exists(metaPath))
                File.Delete(metaPath);

            return localPath;
        }
        catch (Exception ex)
        {
            DeviceDisplay.Current.KeepScreenOn = false;
            MainThread.BeginInvokeOnMainThread(() =>
                StatusText = $"❌ Download: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Validates that a file is a complete GGUF model (checks magic header + minimum size).
    /// </summary>
    private static bool IsValidGgufFile(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length < 1_000_000) // Less than 1MB is definitely incomplete
                return false;

            // Check GGUF magic bytes: "GGUF" = 0x46554747
            using var fs = File.OpenRead(path);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) < 4)
                return false;

            return header[0] == 0x47 && header[1] == 0x47 && header[2] == 0x55 && header[3] == 0x46;
        }
        catch
        {
            return false;
        }
    }

    private string BuildPrompt(string userMessage, string? imagePath = null)
    {
        var systemPrompt = GetLocalizedSystemPrompt();

        // When image is attached, use vision model format (Qwen3.5 or Qwen3-VL)
        if (IsVisionModel && !string.IsNullOrEmpty(imagePath))
        {
            // Paired model: vision always uses Qwen3.5 format
            // Standalone VL model: uses Qwen3-VL format
            var useQwen35Format = IsCurrentModelQwen35();

#if (IOS || ANDROID || MACCATALYST)
            if (_mtmdEngine != null && _mtmdEngine.IsLoaded)
            {
                return useQwen35Format
                    ? ChatMessage.BuildVisionPromptQwen35Mtmd(systemPrompt, userMessage, ThinkingEnabled)
                    : ChatMessage.BuildVisionPromptMtmd(systemPrompt, userMessage, ThinkingEnabled);
            }
#endif
            return useQwen35Format
                ? ChatMessage.BuildVisionPromptQwen35(systemPrompt, userMessage, ThinkingEnabled)
                : ChatMessage.BuildVisionPrompt(systemPrompt, userMessage, ThinkingEnabled);
        }

        // Text-only: always use Qwen3 format (text engine is always Qwen3)
        return ChatMessage.BuildPrompt(systemPrompt, userMessage, ThinkingEnabled);
    }

    /// <summary>
    /// Check if the currently loaded model is a Qwen3.5 model (different prompt format).
    /// </summary>
    private static bool IsCurrentModelQwen35()
    {
        var key = Preferences.Get(LastModelPrefKey, "");
        if (key.StartsWith("Q35", StringComparison.OrdinalIgnoreCase))
            return true;
        // Check if paired vision model is Q35
        var entry = ModelEntries.FirstOrDefault(m => m.Key == key);
        return !string.IsNullOrEmpty(entry?.PairedVisionKey) && entry.PairedVisionKey.StartsWith("Q35");
    }

    [RelayCommand]
    private async Task ChangeModelAsync()
    {
        try
        {
            var selectedKey = await SelectModelAsync();
            if (selectedKey == null) return;

            // Save preference
            Preferences.Set(LastModelPrefKey, selectedKey);

            var modelInfo = AvailableModels[selectedKey];
            var modelsDir = Path.Combine(FileSystem.AppDataDirectory, "Models");
            var localPath = Path.Combine(modelsDir, modelInfo.FileName);

            if (!File.Exists(localPath) || !IsValidGgufFile(localPath))
            {
                IsLoading = true;
                StatusText = $"Đang tải {modelInfo.DisplayName}...";
                localPath = await DownloadModelAsync(modelInfo.Url, modelInfo.FileName);
                if (localPath == null)
                {
                    StatusText = "❌ Không thể tải model.";
                    IsLoading = false;
                    return;
                }
            }

            // Download mmproj for vision models if needed
            var modelEntry = ModelEntries.FirstOrDefault(m => m.Key == selectedKey);
            if (modelEntry?.IsVision == true && !string.IsNullOrEmpty(modelEntry.MmProjUrl))
            {
                var mmprojPath = Path.Combine(modelsDir, modelEntry.MmProjFileName);
                if (!File.Exists(mmprojPath) || !IsValidGgufFile(mmprojPath))
                {
                    IsLoading = true;
                    StatusText = $"Đang tải vision encoder...";
                    var downloadedMmproj = await DownloadModelAsync(modelEntry.MmProjUrl, modelEntry.MmProjFileName);
                    if (downloadedMmproj == null)
                    {
                        StatusText = "⚠️ Không thể tải vision encoder. Chạy text-only.";
                    }
                }
            }

            // Download paired vision model + mmproj if needed
            if (!string.IsNullOrEmpty(modelEntry?.PairedVisionKey))
            {
                var visionEntry = ModelEntries.FirstOrDefault(m => m.Key == modelEntry.PairedVisionKey);
                if (visionEntry != null)
                {
                    var visionPath = Path.Combine(modelsDir, visionEntry.FileName);
                    if (!File.Exists(visionPath) || !IsValidGgufFile(visionPath))
                    {
                        IsLoading = true;
                        StatusText = $"Đang tải vision model ({visionEntry.DisplayName})...";
                        await DownloadModelAsync(visionEntry.Url, visionEntry.FileName);
                    }
                    if (!string.IsNullOrEmpty(visionEntry.MmProjUrl))
                    {
                        var mmprojPath2 = Path.Combine(modelsDir, visionEntry.MmProjFileName);
                        if (!File.Exists(mmprojPath2) || !IsValidGgufFile(mmprojPath2))
                        {
                            StatusText = $"Đang tải vision encoder...";
                            await DownloadModelAsync(visionEntry.MmProjUrl, visionEntry.MmProjFileName);
                        }
                    }
                }
            }

            IsLoading = true;
            IsModelLoaded = false;
            StatusText = $"Đang tải: {modelInfo.FileName}...";

            await Task.Run(() =>
            {
#if (IOS || ANDROID || MACCATALYST)
                _mtmdEngine?.Dispose();
                _mtmdEngine = null;
                _visionEngine?.Dispose();
                _visionEngine = null;
                _engine?.Dispose();
                _engine = new LlamaCppEngine();
                _engine.LoadModel(localPath);

                // Check model entry
                var entry = ModelEntries.FirstOrDefault(m => m.Key == selectedKey);
                var isVision = entry?.IsVision == true || !string.IsNullOrEmpty(entry?.PairedVisionKey);

                MainThread.BeginInvokeOnMainThread(() => IsVisionModel = isVision);

                // Load paired vision model (Qwen3.5) if available
                if (!string.IsNullOrEmpty(entry?.PairedVisionKey))
                {
                    var visionEntry = ModelEntries.FirstOrDefault(m => m.Key == entry.PairedVisionKey);
                    if (visionEntry != null)
                    {
                        var visionModelPath = Path.Combine(FileSystem.AppDataDirectory, "Models", visionEntry.FileName);
                        if (File.Exists(visionModelPath))
                        {
                            try
                            {
                                _visionEngine = new LlamaCppEngine();
                                _visionEngine.LoadModel(visionModelPath);
                                Console.WriteLine($"[AIAgentLocal] Paired vision model loaded: {visionEntry.FileName}");
                                MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(ShowAttachButton)));

                                // Load mmproj for the vision model
                                if (!string.IsNullOrEmpty(visionEntry.MmProjFileName) && IsMtmdLibraryAvailable())
                                {
                                    var mmprojPath = Path.Combine(FileSystem.AppDataDirectory, "Models", visionEntry.MmProjFileName);
                                    if (File.Exists(mmprojPath))
                                    {
                                        try
                                        {
                                            _mtmdEngine = new MtmdEngine();
                                            _mtmdEngine.LoadMmproj(mmprojPath, _visionEngine.ModelPtr);
                                            Console.WriteLine($"[AIAgentLocal] Vision mmproj loaded successfully");
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[AIAgentLocal] Vision mmproj failed: {ex.Message}");
                                            _mtmdEngine?.Dispose();
                                            _mtmdEngine = null;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[AIAgentLocal] Paired vision model failed: {ex.Message}");
                                _visionEngine?.Dispose();
                                _visionEngine = null;
                            }
                        }
                    }
                }
                // Standalone vision model
                else if (entry?.IsVision == true && !string.IsNullOrEmpty(entry.MmProjFileName) && IsMtmdLibraryAvailable())
                {
                    var mmprojPath = Path.Combine(FileSystem.AppDataDirectory, "Models", entry.MmProjFileName);
                    if (File.Exists(mmprojPath))
                    {
                        try
                        {
                            _mtmdEngine = new MtmdEngine();
                            _mtmdEngine.LoadMmproj(mmprojPath, _engine.ModelPtr);
                            Console.WriteLine($"[AIAgentLocal] mmproj loaded successfully");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[AIAgentLocal] mmproj load failed: {ex.Message}");
                            _mtmdEngine?.Dispose();
                            _mtmdEngine = null;
                        }
                    }
                }
#else
                _model?.Dispose();
                _modelParams = new ModelParams(localPath)
                {
                    ContextSize = 0, // 0 = auto from model
                    GpuLayerCount = 0
                };
                _model = LLamaWeights.LoadFromFile(_modelParams);
                _executor = new StatelessExecutor(_model, _modelParams);
                MainThread.BeginInvokeOnMainThread(() => IsVisionModel = false);
#endif
            });

            Messages.Clear();
            _history.NewConversation();
            StatusText = $"✓ {modelInfo.FileName}";
            IsModelLoaded = true;
        }
        catch (Exception ex)
        {
            StatusText = $"❌ {ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"[AIAgentLocal] ChangeModel FAILED: {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
#if (IOS || ANDROID || MACCATALYST)
        if (string.IsNullOrWhiteSpace(InputText) || _engine == null || !_engine.IsLoaded) return;
#else
        if (string.IsNullOrWhiteSpace(InputText) || _executor == null) return;
#endif

        var userText = InputText.Trim();
        InputText = string.Empty;

        // Capture attached image before clearing
        var currentImagePath = AttachedImagePath;
        AttachedImagePath = null;

        var userMessage = new ChatMessage(userText, isUser: true);
        if (!string.IsNullOrEmpty(currentImagePath))
            userMessage.ImagePath = currentImagePath;
        Messages.Add(userMessage);

        var aiMessage = new ChatMessage(string.Empty, isUser: false) { IsGenerating = true };
        Messages.Add(aiMessage);

        IsLoading = true;
        IsGenerating = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var prompt = BuildPrompt(userText, currentImagePath);
            var sw = Stopwatch.StartNew();
            int tokenCount = 0;
            double ttftMs = 0;
            var responseBuilder = new StringBuilder();
            var rawResponseBuilder = new StringBuilder();

#if (IOS || ANDROID || MACCATALYST)
            await Task.Run(() =>
            {
                bool firstToken = true;

                // Vision inference with image
                if (IsVisionModel && !string.IsNullOrEmpty(currentImagePath))
                {
                    // Determine which engine to use for vision
                    var vEngine = _visionEngine ?? _engine;
                    var hasMtmd = _mtmdEngine != null && _mtmdEngine.IsLoaded;

                    if (hasMtmd && vEngine != null)
                    {
                        try
                        {
                            Console.WriteLine($"[ChatViewModel] Vision inference with paired engine: image={currentImagePath}");

                            var chunks = _mtmdEngine!.TokenizeWithImageFile(prompt, currentImagePath);
                            try
                            {
                                vEngine.ClearKvCache();
                                var nPast = _mtmdEngine.EvalChunks(vEngine.ContextPtr, chunks, 0);
                                Console.WriteLine($"[ChatViewModel] EvalChunks done, n_past={nPast}");

                                foreach (var piece in vEngine.GenerateAfterEval(nPast, vEngine.MaxResponseTokens, 0.7f, 0.8f))
                                {
                                    if (token.IsCancellationRequested) break;
                                    if (firstToken) { ttftMs = sw.Elapsed.TotalMilliseconds; firstToken = false; }
                                    tokenCount++;
                                    rawResponseBuilder.Append(piece);
                                    MainThread.BeginInvokeOnMainThread(() =>
                                        aiMessage.UpdateFromRawResponse(rawResponseBuilder.ToString()));
                                }
                            }
                            finally
                            {
                                _mtmdEngine.FreeChunks(chunks);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ChatViewModel] Vision inference EXCEPTION: {ex}, falling back to text");
                            // Fall through to text inference below
                            foreach (var piece in _engine!.InferRaw(prompt, maxTokens: _engine.MaxResponseTokens, temperature: 0.7f, topP: 0.9f))
                            {
                                if (token.IsCancellationRequested) break;
                                if (firstToken) { ttftMs = sw.Elapsed.TotalMilliseconds; firstToken = false; }
                                tokenCount++;
                                rawResponseBuilder.Append(piece);
                                MainThread.BeginInvokeOnMainThread(() => aiMessage.UpdateFromRawResponse(rawResponseBuilder.ToString()));
                            }
                        }
                    }
                    else
                    {
                        // No mtmd available, use text-only inference
                        foreach (var piece in _engine!.InferRaw(prompt, maxTokens: _engine.MaxResponseTokens, temperature: 0.7f, topP: 0.9f))
                        {
                            if (token.IsCancellationRequested) break;
                            if (firstToken) { ttftMs = sw.Elapsed.TotalMilliseconds; firstToken = false; }
                            tokenCount++;
                            rawResponseBuilder.Append(piece);
                            MainThread.BeginInvokeOnMainThread(() => aiMessage.UpdateFromRawResponse(rawResponseBuilder.ToString()));
                        }
                    }
                }
                else
                {
                    // Standard text-only inference (uses Qwen3 engine)
                    foreach (var piece in _engine!.InferRaw(prompt, maxTokens: _engine.MaxResponseTokens, temperature: 0.7f, topP: 0.9f))
                    {
                        if (token.IsCancellationRequested) break;
                        if (firstToken) { ttftMs = sw.Elapsed.TotalMilliseconds; firstToken = false; }
                        tokenCount++;
                        rawResponseBuilder.Append(piece);
                        MainThread.BeginInvokeOnMainThread(() => aiMessage.UpdateFromRawResponse(rawResponseBuilder.ToString()));
                    }
                }
            });
#else
            var inferenceParams = new InferenceParams
            {
                MaxTokens = FallbackMaxResponseTokens,
                AntiPrompts = new[] { "<|im_end|>", "<|endoftext|>", "<|im_start|>" },
                SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
                {
                    Temperature = 0.7f,
                    TopP = 0.9f
                }
            };

            bool firstTokenLlama = true;
            await foreach (var token in _executor!.InferAsync(prompt, inferenceParams))
            {
                if (token.Contains("<|im_end|>") || token.Contains("<|im_start|>") || token.Contains("<|endoftext|>"))
                    break;

                if (firstTokenLlama)
                {
                    ttftMs = sw.Elapsed.TotalMilliseconds;
                    firstTokenLlama = false;
                }
                tokenCount++;
                rawResponseBuilder.Append(token);
                responseBuilder.Append(token);

                var currentText = responseBuilder.ToString();
                MainThread.BeginInvokeOnMainThread(() => aiMessage.Content = currentText);
            }
#endif

            sw.Stop();
            var elapsed = sw.Elapsed;
            var tokPerSec = elapsed.TotalSeconds > 0 ? tokenCount / elapsed.TotalSeconds : 0;

#if (IOS || ANDROID || MACCATALYST)
            var inputTokens = _engine?.CountTokens(prompt) ?? 0;
            var responseTokens = tokenCount;
            var contextInfo = $"📥 {inputTokens} + 📤 {responseTokens} = {inputTokens + responseTokens}/{_engine?.ContextSize ?? 0} ctx";
#else
            var inputTokens = prompt.Length / 4; // Rough estimate for Windows/LLamaSharp
            var responseTokens = tokenCount;
            var contextInfo = $"📥 ~{inputTokens} + 📤 {responseTokens} = ~{inputTokens + responseTokens}/{FallbackContextSize} ctx";
#endif

            MainThread.BeginInvokeOnMainThread(() =>
            {
                aiMessage.IsGenerating = false;
                aiMessage.Stats = $"{tokPerSec:F1} tok/s • TTFT {ttftMs:F0}ms • {tokenCount} tokens • {elapsed.TotalSeconds:F1}s"
                    + (string.IsNullOrEmpty(contextInfo) ? "" : $"\n{contextInfo}");
                PerformanceText = $"⚡ {tokPerSec:F1} tok/s • TTFT {ttftMs:F0}ms";
            });

            // Save chat history
            _history.SavePair(userMessage, aiMessage, rawResponseBuilder.ToString());

            // Log full prompt + response to file
            SaveChatLog(prompt, rawResponseBuilder.ToString(), aiMessage.TrimmedThinkContent, aiMessage.TrimmedContent);
        }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                aiMessage.Content = $"❌ Lỗi: {ex.Message}";
                aiMessage.IsGenerating = false;
            });
        }
        finally
        {
            // Always ensure UI is re-enabled regardless of what happened
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsLoading = false;
                IsGenerating = false;
            });
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanSend() => IsModelLoaded && !IsGenerating && !string.IsNullOrWhiteSpace(InputText);

#if (IOS || ANDROID || MACCATALYST)
    /// <summary>
    /// Load an image file and convert to raw RGB byte array.
    /// </summary>
    private static byte[]? LoadImageAsRgb(string imagePath, out uint width, out uint height)
    {
        width = 0;
        height = 0;

        try
        {
            if (!File.Exists(imagePath))
                return null;

            // Use MAUI's image decoder to load and resize the image
            // For vision models, we resize to a reasonable size (max 1024px on longest side)
            using var stream = File.OpenRead(imagePath);

#if ANDROID
            var bitmap = Android.Graphics.BitmapFactory.DecodeStream(stream);
            if (bitmap == null) return null;

            // Resize if too large
            var maxDim = 1024;
            var scale = Math.Min((float)maxDim / bitmap.Width, (float)maxDim / bitmap.Height);
            if (scale < 1.0f)
            {
                var newW = (int)(bitmap.Width * scale);
                var newH = (int)(bitmap.Height * scale);
                var resized = Android.Graphics.Bitmap.CreateScaledBitmap(bitmap, newW, newH, true);
                bitmap.Dispose();
                bitmap = resized;
            }

            width = (uint)bitmap!.Width;
            height = (uint)bitmap.Height;

            // Extract RGB data
            var pixels = new int[width * height];
            bitmap.GetPixels(pixels, 0, (int)width, 0, 0, (int)width, (int)height);

            var rgb = new byte[width * height * 3];
            for (int i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                rgb[i * 3] = (byte)((pixel >> 16) & 0xFF);     // R
                rgb[i * 3 + 1] = (byte)((pixel >> 8) & 0xFF);  // G
                rgb[i * 3 + 2] = (byte)(pixel & 0xFF);          // B
            }

            bitmap.Dispose();
            return rgb;
#elif IOS || MACCATALYST
            var uiImage = UIKit.UIImage.LoadFromData(Foundation.NSData.FromStream(stream)!);
            if (uiImage == null) return null;

            // Resize if too large
            var maxDim = 1024.0;
            var imgWidth = uiImage.Size.Width;
            var imgHeight = uiImage.Size.Height;
            var scale = Math.Min(maxDim / imgWidth, maxDim / imgHeight);
            if (scale < 1.0)
            {
                var newSize = new CoreGraphics.CGSize(imgWidth * scale, imgHeight * scale);
                var renderer = new UIKit.UIGraphicsImageRenderer(newSize);
                var resized = renderer.CreateImage((ctx) =>
                {
                    uiImage.Draw(new CoreGraphics.CGRect(0, 0, newSize.Width, newSize.Height));
                });
                uiImage.Dispose();
                uiImage = resized;
            }

            width = (uint)uiImage.Size.Width;
            height = (uint)uiImage.Size.Height;

            // Get raw pixel data via CGImage
            var cgImage = uiImage.CGImage;
            if (cgImage == null) return null;

            var bytesPerRow = width * 4;
            var rawData = new byte[bytesPerRow * height];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(rawData, System.Runtime.InteropServices.GCHandleType.Pinned);

            try
            {
                using var colorSpace = CoreGraphics.CGColorSpace.CreateDeviceRGB();
                using var context = new CoreGraphics.CGBitmapContext(
                    handle.AddrOfPinnedObject(),
                    (nint)width, (nint)height, 8, (nint)bytesPerRow,
                    colorSpace,
                    CoreGraphics.CGImageAlphaInfo.NoneSkipLast);
                context.DrawImage(new CoreGraphics.CGRect(0, 0, width, height), cgImage);
            }
            finally
            {
                handle.Free();
            }

            // Convert RGBX to RGB
            var rgb = new byte[width * height * 3];
            for (uint i = 0; i < width * height; i++)
            {
                rgb[i * 3] = rawData[i * 4];       // R
                rgb[i * 3 + 1] = rawData[i * 4 + 1]; // G
                rgb[i * 3 + 2] = rawData[i * 4 + 2]; // B
            }

            uiImage.Dispose();
            return rgb;
#else
            return null;
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatViewModel] LoadImageAsRgb failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Check if a file path is a video file.
    /// </summary>
    private static bool IsVideoFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm";
    }

    /// <summary>
    /// Check if the mtmd native library is available (without triggering DllNotFoundException).
    /// </summary>
    private static bool IsMtmdLibraryAvailable()
    {
        try
        {
#if ANDROID
            var nativeLibDir = Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir;
            if (nativeLibDir != null)
                return File.Exists(Path.Combine(nativeLibDir, "libmtmd.so"));
            return false;
#elif MACCATALYST
            var resourcePath = Foundation.NSBundle.MainBundle.ResourcePath;
            return File.Exists(Path.Combine(resourcePath, "libmtmd.dylib"));
#elif IOS
            // On iOS, mtmd is statically linked - check if the symbol exists
            return true; // If it compiled with the static lib, it's available
#else
            return false;
#endif
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extract frames from a video file for vision model input.
    /// Returns a list of frame file paths (as images).
    /// Extracts up to maxFrames evenly spaced frames.
    /// </summary>
    private static async Task<List<string>> ExtractVideoFramesAsync(string videoPath, int maxFrames = 4)
    {
        var frames = new List<string>();
        var cacheDir = Path.Combine(FileSystem.CacheDirectory, "VideoFrames");
        Directory.CreateDirectory(cacheDir);

        try
        {
#if ANDROID
            var retriever = new Android.Media.MediaMetadataRetriever();
            await Task.Run(() =>
            {
                retriever.SetDataSource(videoPath);
                var durationStr = retriever.ExtractMetadata(Android.Media.MetadataKey.Duration);
                var durationMs = long.TryParse(durationStr, out var d) ? d : 10000;

                // Calculate frame positions evenly spaced
                var interval = durationMs / (maxFrames + 1);
                for (int i = 1; i <= maxFrames; i++)
                {
                    var timeUs = interval * i * 1000; // Convert ms to microseconds
                    var bitmap = retriever.GetFrameAtTime(timeUs, Android.Media.Option.ClosestSync);
                    if (bitmap == null) continue;

                    var framePath = Path.Combine(cacheDir, $"frame_{i}_{DateTime.Now:HHmmss}.jpg");
                    using var stream = File.Create(framePath);
                    bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 85, stream);
                    bitmap.Dispose();
                    frames.Add(framePath);
                }
                retriever.Release();
            });
#elif IOS || MACCATALYST
            await Task.Run(() =>
            {
                var url = Foundation.NSUrl.FromFilename(videoPath);
                var asset = AVFoundation.AVAsset.FromUrl(url);
                var generator = new AVFoundation.AVAssetImageGenerator(asset);
                generator.AppliesPreferredTrackTransform = true;
                generator.MaximumSize = new CoreGraphics.CGSize(1024, 1024);

                var duration = asset.Duration;
                var totalSeconds = duration.Seconds;
                if (totalSeconds <= 0) totalSeconds = 10;

                var interval = totalSeconds / (maxFrames + 1);
                for (int i = 1; i <= maxFrames; i++)
                {
                    var time = CoreMedia.CMTime.FromSeconds(interval * i, 600);
                    var imageRef = generator.CopyCGImageAtTime(time, out var actualTime, out var error);
                    if (imageRef == null) continue;

                    var uiImage = new UIKit.UIImage(imageRef);
                    var jpegData = uiImage.AsJPEG(0.85f);
                    if (jpegData != null)
                    {
                        var framePath = Path.Combine(cacheDir, $"frame_{i}_{DateTime.Now:HHmmss}.jpg");
                        jpegData.Save(framePath, true);
                        frames.Add(framePath);
                    }
                    uiImage.Dispose();
                    imageRef.Dispose();
                }
            });
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatViewModel] ExtractVideoFrames failed: {ex.Message}");
        }

        return frames;
    }
#endif

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task AttachImageAsync()
    {
        try
        {
            // Show action sheet to choose between photo and video
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null) return;

            var choice = await page.DisplayActionSheet(
                "Đính kèm file", "Hủy", null,
                "🖼️ Chọn ảnh", "🎬 Chọn video");

            if (choice == null || choice == "Hủy") return;

            FileResult? result = null;
            if (choice == "🖼️ Chọn ảnh")
            {
                result = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions
                {
                    Title = "Chọn ảnh"
                });
            }
            else if (choice == "🎬 Chọn video")
            {
                result = await MediaPicker.Default.PickVideoAsync(new MediaPickerOptions
                {
                    Title = "Chọn video"
                });
            }

            if (result == null) return;

            // Copy to app's cache directory for reliable access
            var cacheDir = Path.Combine(FileSystem.CacheDirectory, "AttachedMedia");
            Directory.CreateDirectory(cacheDir);
            var ext = Path.GetExtension(result.FileName);
            var destPath = Path.Combine(cacheDir, $"media_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");

            using var sourceStream = await result.OpenReadAsync();
            using var destStream = File.Create(destPath);
            await sourceStream.CopyToAsync(destStream);

            // For video, extract a thumbnail frame for preview
            var videoExts = new[] { ".mp4", ".mov", ".avi", ".mkv", ".webm" };
            if (videoExts.Contains(ext?.ToLowerInvariant()))
            {
                // Store the video path; thumbnail will be generated on display
                AttachedImagePath = destPath;
                Console.WriteLine($"[ChatViewModel] Video attached: {destPath}");
            }
            else
            {
                AttachedImagePath = destPath;
                Console.WriteLine($"[ChatViewModel] Image attached: {destPath}");
            }
        }
        catch (PermissionException)
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page != null)
                await page.DisplayAlert("Quyền truy cập", "Cần cấp quyền truy cập ảnh/video trong Cài đặt.", "OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatViewModel] Failed to attach media: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveImage()
    {
        if (AttachedImagePath != null && File.Exists(AttachedImagePath))
        {
            try { File.Delete(AttachedImagePath); } catch { }
        }
        AttachedImagePath = null;
    }

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsLoadingChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsModelLoadedChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInput));
    }
    partial void OnIsGeneratingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInput));
        OnPropertyChanged(nameof(IsNotGenerating));
    }

    [RelayCommand]
    private void NewConversation()
    {
        _history.NewConversation();
        Messages.Clear();
    }

    [RelayCommand]
    private async Task OpenConversationAsync()
    {
        var conversations = _history.GetConversations();
        if (conversations.Count == 0)
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page != null)
                await page.DisplayAlert(Services.L.History, Services.L.NoConversations, Services.L.OK);
            return;
        }

        var options = conversations.Select(c =>
            c.IsCurrent ? $"● {c.Id} - {c.Preview}" : $"  {c.Id} - {c.Preview}"
        ).ToArray();

        var result = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null) return null;
            return await page.DisplayActionSheet(Services.L.SelectConversation, Services.L.Cancel, null, options);
        });

        if (result == null || result == Services.L.Cancel) return;

        var selectedId = conversations
            .FirstOrDefault(c => result.Contains(c.Id))?.Id;

        if (selectedId != null && selectedId != _history.CurrentId)
        {
            _history.OpenConversation(selectedId);
            Messages.Clear();
            var history = _history.LoadRecent(10);
            foreach (var msg in history)
                Messages.Add(msg);
        }
    }

    [RelayCommand]
    private async Task DeleteConversationAsync()
    {
        var conversations = _history.GetConversations();
        if (conversations.Count == 0) return;

        var options = conversations.Select(c =>
            c.IsCurrent ? $"● {c.Id} - {c.Preview}" : $"  {c.Id} - {c.Preview}"
        ).ToArray();

        var result = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null) return null;
            return await page.DisplayActionSheet(Services.L.DeleteWhich, Services.L.Cancel, null, options);
        });

        if (result == null || result == Services.L.Cancel) return;

        var selectedId = conversations.FirstOrDefault(c => result.Contains(c.Id))?.Id;
        if (selectedId == null) return;

        var page2 = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page2 != null)
        {
            var confirm = await page2.DisplayAlert(Services.L.Confirm, $"Xóa hội thoại {selectedId}?", Services.L.Delete, Services.L.Cancel);
            if (!confirm) return;
        }

        _history.DeleteConversation(selectedId);

        // If deleted current conversation, start new one
        if (selectedId == _history.CurrentId)
        {
            _history.NewConversation();
            Messages.Clear();
        }
    }

    [RelayCommand]
    private async Task ChangeLanguageAsync()
    {
        var languages = Services.L.GetSupportedLanguages();
        var currentCode = Services.L.GetCurrentLanguageCode();

        var pickerPage = new Views.LanguagePickerPage(languages, currentCode);
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;

        await page.Navigation.PushModalAsync(pickerPage);

        // Wait for page to close
        var tcs = new TaskCompletionSource();
        pickerPage.Disappearing += (s, e) => tcs.TrySetResult();
        await tcs.Task;

        var selectedCode = pickerPage.SelectedCode;
        if (selectedCode == null || selectedCode == currentCode) return;
        if (selectedCode != null && selectedCode != currentCode)
        {
            Services.L.SetLanguage(selectedCode);

            // Refresh UI bindings
            OnPropertyChanged(nameof(PlaceholderText));
            OnPropertyChanged(nameof(SendButtonText));
            OnPropertyChanged(nameof(MenuChatHistory));
            OnPropertyChanged(nameof(MenuDeleteChat));
            OnPropertyChanged(nameof(MenuModel));
            OnPropertyChanged(nameof(MenuLanguage));
            OnPropertyChanged(nameof(MenuDeviceInfo));
            OnPropertyChanged(nameof(AppTitle));
            StatusText = Services.L.Get("LoadingModel");
            if (IsModelLoaded)
                StatusText = "✓ Model loaded";
        }
    }

    [RelayCommand]
    private async Task ShowDeviceInfoAsync()
    {
        var info = new StringBuilder();
        info.AppendLine($"📱 Device: {DeviceInfo.Manufacturer} {DeviceInfo.Model}");
        info.AppendLine($"💻 Platform: {DeviceInfo.Platform} {DeviceInfo.VersionString}");
        info.AppendLine($"🏗️ Architecture: {RuntimeInformation.ProcessArchitecture}");
        info.AppendLine($"🧠 Processors: {Environment.ProcessorCount} cores");
        info.AppendLine($"🧮 RAM: {GetTotalMemoryMB()} MB");
        info.AppendLine($"📦 App: {AppInfo.Name} v{AppInfo.VersionString}");
        info.AppendLine($"🔧 .NET: {Environment.Version}");
        info.AppendLine($"💾 Device Type: {DeviceInfo.DeviceType}");
        info.AppendLine($"🖥️ Idiom: {DeviceInfo.Idiom}");

        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page != null)
            await page.DisplayAlert(Services.L.DeviceInfo, info.ToString(), Services.L.OK);
    }

    private static void SaveChatLog(string prompt, string rawResponse, string thinkContent, string content)
    {
        try
        {
            var logsDir = Path.Combine(FileSystem.AppDataDirectory, "Logs");
            Directory.CreateDirectory(logsDir);
            var fileName = $"chat_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var filePath = Path.Combine(logsDir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("=== INPUT (with chat template) ===");
            sb.AppendLine(prompt);
            sb.AppendLine();
            sb.AppendLine("=== RAW RESPONSE ===");
            sb.AppendLine(rawResponse);
            sb.AppendLine();
            sb.AppendLine("=== THINK ===");
            sb.AppendLine(thinkContent);
            sb.AppendLine();
            sb.AppendLine("=== CONTENT ===");
            sb.AppendLine(content);

            File.WriteAllText(filePath, sb.ToString());
            Console.WriteLine($"[AIAgentLocal] Chat log saved: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AIAgentLocal] Failed to save chat log: {ex.Message}");
        }
    }

    private static string GetTotalMemoryMB()
    {
        try
        {
#if ANDROID
            var activityManager = Android.App.Application.Context.GetSystemService(Android.Content.Context.ActivityService) as Android.App.ActivityManager;
            if (activityManager != null)
            {
                var memInfo = new Android.App.ActivityManager.MemoryInfo();
                activityManager.GetMemoryInfo(memInfo);
                return $"{memInfo.TotalMem / (1024 * 1024)}";
            }
#elif IOS || MACCATALYST
            var totalMemory = Foundation.NSProcessInfo.ProcessInfo.PhysicalMemory;
            return $"{totalMemory / (1024 * 1024)}";
#endif
            // Fallback: GC info (not total RAM but gives an idea)
            var gcMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (gcMemory > 0)
                return $"{gcMemory / (1024 * 1024)}";
        }
        catch { }
        return "N/A";
    }
}
