using System.Runtime.InteropServices;
using System.Text;

namespace AIAgentLocal.Native;

/// <summary>
/// High-level wrapper around llama.cpp C API for iOS and Android.
/// Replaces LLamaSharp's StatelessExecutor functionality.
/// </summary>
internal sealed class LlamaCppEngine : IDisposable
{
    private IntPtr _model;
    private IntPtr _ctx;
    private IntPtr _vocab;
    private IntPtr _sampler;
    private int _nVocab;
    private bool _disposed;

    public bool IsLoaded => _model != IntPtr.Zero && _ctx != IntPtr.Zero;

    /// <summary>Pointer to the loaded model (needed by MtmdEngine).</summary>
    public IntPtr ModelPtr => _model;

    /// <summary>Max response tokens, calculated based on device RAM.</summary>
    public int MaxResponseTokens { get; private set; } = 1024;

    /// <summary>Context size used for this session.</summary>
    public uint ContextSize { get; private set; }

    /// <summary>Model parameter count in billions.</summary>
    public double ModelSizeB { get; private set; }

    /// <summary>Count tokens in a text string.</summary>
    public int CountTokens(string text)
    {
        if (!IsLoaded || string.IsNullOrEmpty(text)) return 0;
        return Tokenize(text, addSpecial: false, parseSpecial: true).Length;
    }

    /// <summary>
    /// Calculate how many history messages can fit given system prompt, user message, and actual message contents.
    /// Returns max number of messages to include (from most recent). Never truncates message content.
    /// </summary>
    public int CalculateMaxHistoryMessages(string systemPrompt, string userMessage, IList<string> historyContents)
    {
        if (!IsLoaded) return 10;

        // Models under 4B don't handle history well — skip it
        if (ModelSizeB < 4.0)
        {
            Console.WriteLine($"[LlamaCppEngine] Model {ModelSizeB:F1}B < 4B, skipping history");
            return 0;
        }

        // Tokens for fixed parts
        var systemTokens = Tokenize(systemPrompt, addSpecial: false, parseSpecial: false).Length;
        var userTokens = Tokenize(userMessage, addSpecial: false, parseSpecial: false).Length;
        var templateOverhead = 30;

        // Reserve for response: min of MaxResponseTokens or 50% of context
        var reserveForResponse = Math.Min(MaxResponseTokens, (int)ContextSize / 2);
        var fixedTokens = systemTokens + userTokens + templateOverhead + reserveForResponse;
        var availableForHistory = (int)ContextSize - fixedTokens;

        Console.WriteLine($"[LlamaCppEngine] Context budget: ctx={ContextSize}, system={systemTokens}, user={userTokens}, reserve_response={reserveForResponse}, available_history={availableForHistory}");

        if (availableForHistory <= 0) return 0;

        // Walk from most recent backward, adding full messages until budget exhausted
        int count = 0;
        int usedTokens = 0;
        for (int i = historyContents.Count - 1; i >= 0; i--)
        {
            var msgTokens = Tokenize(historyContents[i], addSpecial: false, parseSpecial: false).Length + 10; // +10 for role tags
            if (usedTokens + msgTokens > availableForHistory)
                break; // Can't fit this message — stop here
            usedTokens += msgTokens;
            count++;
        }

        Console.WriteLine($"[LlamaCppEngine] History: {count} messages, {usedTokens} tokens");
        return count;
    }

    public static void InitBackend()
    {
#if ANDROID
        // On Android, native .so files are bundled in the APK and extracted to the app's native lib dir.
        var nativeLibDir = Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir;
        Console.WriteLine($"[LlamaCppEngine] Native lib dir: {nativeLibDir}");
        
        if (nativeLibDir != null)
        {
            // First load all dependencies in order so libllama.so can resolve its symbols
            var depsToLoad = new[]
            {
                "libggml-base.so",
                "libggml.so",
                "libggml-cpu.so",
                "libllama.so",
                "libmtmd.so",
            };
            
            IntPtr llamaHandle = IntPtr.Zero;
            foreach (var lib in depsToLoad)
            {
                var fullPath = Path.Combine(nativeLibDir, lib);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        var h = NativeLibrary.Load(fullPath);
                        Console.WriteLine($"[LlamaCppEngine] Loaded: {lib} -> {h}");
                        if (lib == "libllama.so") llamaHandle = h;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LlamaCppEngine] Failed {lib}: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[LlamaCppEngine] Not found: {fullPath}");
                }
            }
            
            // Set DLL import resolver to use the correct libllama.so path
            var llamaPath = Path.Combine(nativeLibDir, "libllama.so");
            var mtmdPath = Path.Combine(nativeLibDir, "libmtmd.so");
            NativeLibrary.SetDllImportResolver(typeof(LlamaCpp).Assembly, (name, assembly, searchPath) =>
            {
                if (name == "llama")
                {
                    if (NativeLibrary.TryLoad(llamaPath, out var handle))
                        return handle;
                }
                if (name == "mtmd")
                {
                    if (NativeLibrary.TryLoad(mtmdPath, out var handle))
                        return handle;
                }
                return IntPtr.Zero;
            });
            
            // Verify key symbols exist
            if (NativeLibrary.TryLoad(llamaPath, out var verifyHandle))
            {
                if (NativeLibrary.TryGetExport(verifyHandle, "llama_backend_init", out var initPtr))
                    Console.WriteLine($"[LlamaCppEngine] llama_backend_init at: {initPtr}");
                else
                    Console.WriteLine($"[LlamaCppEngine] llama_backend_init NOT FOUND!");
            }
        }
#endif
        LlamaCpp.llama_backend_init();
        Console.WriteLine($"[LlamaCppEngine] backend_init done");
        var infoPtr = LlamaCpp.llama_print_system_info();
        var info = infoPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(infoPtr) : "(null)";
        Console.WriteLine($"[LlamaCppEngine] System info: '{info}'");
    }

    public void LoadModel(string modelPath, uint contextSize = 0, int nThreads = 0)
    {
        if (nThreads <= 0)
            nThreads = Math.Max(1, Environment.ProcessorCount - 1);

        // Verify file exists and is accessible
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model file not found: {modelPath}");

        var fileSize = new FileInfo(modelPath).Length;
        Console.WriteLine($"[LlamaCppEngine] Loading model: {modelPath} ({fileSize / (1024*1024)} MB), threads={nThreads}");

        // Verify file is readable and has valid GGUF header
        try
        {
            using var fs = File.OpenRead(modelPath);
            var header = new byte[8];
            fs.Read(header, 0, 8);
            var magic = System.Text.Encoding.ASCII.GetString(header, 0, 4);
            Console.WriteLine($"[LlamaCppEngine] File magic: '{magic}' ({header[0]:X2}{header[1]:X2}{header[2]:X2}{header[3]:X2})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LlamaCppEngine] Cannot read file: {ex.Message}");
        }

        // Load model
        Console.WriteLine($"[LlamaCppEngine] Getting default model params...");
        _model = LlamaCpp.llama_model_load_from_file_safe(modelPath);
        if (_model == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load model: {modelPath}");

        _vocab = LlamaCpp.llama_model_get_vocab(_model);
        _nVocab = LlamaCpp.llama_vocab_n_tokens(_vocab);
        ModelSizeB = LlamaCpp.llama_model_n_params(_model) / 1_000_000_000.0;
        Console.WriteLine($"[LlamaCppEngine] Model params: {ModelSizeB:F1}B");

        // Create context — use model's trained context size, capped by device RAM
        if (contextSize == 0)
        {
            var modelCtx = (uint)LlamaCpp.llama_model_n_ctx_train(_model);
            
            // Estimate max context based on device RAM
            // KV cache uses ~0.5MB per 1024 context tokens per layer (rough estimate for 4B model)
            // Conservative: allocate max 25% of total RAM for KV cache
            var totalRamMB = GetDeviceRamMB();
            uint maxCtxByRam = totalRamMB switch
            {
                >= 32768 => modelCtx,   // 32GB+ → use full model context
                >= 16384 => 32768,      // 16GB → 32k
                >= 8192 => 16384,       // 8GB → 16k
                >= 6144 => 8192,        // 6GB → 8k
                >= 4096 => 4096,        // 4GB → 4k
                _ => 2048               // <4GB → 2k
            };
            
            contextSize = Math.Min(modelCtx, maxCtxByRam);
            Console.WriteLine($"[LlamaCppEngine] Model n_ctx_train={modelCtx}, RAM={totalRamMB}MB, using n_ctx={contextSize}");
        }

        var ctxParams = LlamaCpp.llama_context_default_params();
        ctxParams.n_ctx = contextSize;
        ContextSize = contextSize;
        ctxParams.n_batch = 512;
        ctxParams.n_ubatch = 512;
        ctxParams.n_threads = nThreads;
        ctxParams.n_threads_batch = nThreads;
        ctxParams.flash_attn_type = 0; // disabled

        _ctx = LlamaCpp.llama_init_from_model(_model, ctxParams);
        if (_ctx == IntPtr.Zero)
        {
            LlamaCpp.llama_model_free(_model);
            _model = IntPtr.Zero;
            throw new InvalidOperationException("Failed to create llama context");
        }

        // Create sampler chain
        ResetSampler(0.7f, 0.9f, 50);

        // Calculate max response tokens based on RAM
        var ramMB = GetDeviceRamMB();
        MaxResponseTokens = ramMB switch
        {
            >= 32768 => 8192,
            >= 16384 => 6144,
            >= 8192 => 4096,
            >= 6144 => 3072,
            >= 4096 => 2048,
            _ => 1024
        };
        Console.WriteLine($"[LlamaCppEngine] MaxResponseTokens={MaxResponseTokens} (RAM={ramMB}MB)");
    }

    private void ResetSampler(float temperature, float topP, int topK)
    {
        if (_sampler != IntPtr.Zero)
            LlamaCpp.llama_sampler_free(_sampler);

        var chainParams = LlamaCpp.llama_sampler_chain_default_params();
        _sampler = LlamaCpp.llama_sampler_chain_init(chainParams);
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_top_k(topK));
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_top_p(topP, 1));
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_min_p(0.05f, 1));
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_temp(temperature));
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_dist((uint)Random.Shared.Next()));
    }

    private void ResetVisionSampler(float temperature, float topP, int topK)
    {
        if (_sampler != IntPtr.Zero)
            LlamaCpp.llama_sampler_free(_sampler);

        var chainParams = LlamaCpp.llama_sampler_chain_default_params();
        _sampler = LlamaCpp.llama_sampler_chain_init(chainParams);
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_top_k(topK));
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_top_p(topP, 1));
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_min_p(0.05f, 1));
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_temp(temperature));
        LlamaCpp.llama_sampler_chain_add(_sampler, LlamaCpp.llama_sampler_init_dist((uint)Random.Shared.Next()));
    }

    public int[] Tokenize(string text, bool addSpecial = true, bool parseSpecial = true)
    {
        // First call to get required size
        var nTokens = LlamaCpp.llama_tokenize(_vocab, text, text.Length, null!, 0, addSpecial, parseSpecial);
        nTokens = Math.Abs(nTokens); // returns negative count when buffer too small

        var tokens = new int[nTokens];
        var actual = LlamaCpp.llama_tokenize(_vocab, text, text.Length, tokens, nTokens, addSpecial, parseSpecial);
        if (actual < 0)
            throw new InvalidOperationException($"Tokenization failed: {actual}");

        return tokens[..actual];
    }

    public string TokenToString(int token)
    {
        var buf = new byte[128];
        var len = LlamaCpp.llama_token_to_piece(_vocab, token, buf, buf.Length, 0, true);
        if (len < 0)
        {
            buf = new byte[-len];
            len = LlamaCpp.llama_token_to_piece(_vocab, token, buf, buf.Length, 0, true);
        }
        return len > 0 ? Encoding.UTF8.GetString(buf, 0, len) : string.Empty;
    }

    public bool IsEog(int token) => LlamaCpp.llama_vocab_is_eog(_vocab, token);

    /// <summary>Get the embedding dimension of the model.</summary>
    public int EmbeddingDim => LlamaCpp.llama_model_n_embd(_model);

    /// <summary>Get the llama context pointer (needed for vision inference with mtmd).</summary>
    public IntPtr ContextPtr => _ctx;

    /// <summary>
    /// Generate response tokens after context has been filled externally (e.g. by mtmd_helper_eval_chunks).
    /// Starts sampling from the current context state.
    /// </summary>
    public IEnumerable<string> GenerateAfterEval(int nPast, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.8f)
    {
        if (!IsLoaded) throw new InvalidOperationException("Model not loaded");

        ResetVisionSampler(temperature, topP, 20);

        var curPos = nPast;
        var nGenerated = 0;

        while (nGenerated < maxTokens)
        {
            var newToken = LlamaCpp.llama_sampler_sample(_sampler, _ctx, -1);
            if (IsEog(newToken)) break;

            nGenerated++;
            var piece = TokenToString(newToken);

            if (piece.Contains("<|im_end|>") || piece.Contains("<|im_start|>") || piece.Contains("<|endoftext|>"))
                break;

            yield return piece;

            // Decode next token
            var nextBatch = LlamaCpp.llama_batch_init(1, 0, 1);
            try
            {
                Marshal.WriteInt32(nextBatch.token, 0, newToken);
                Marshal.WriteInt32(nextBatch.pos, 0, curPos);
                Marshal.WriteInt32(nextBatch.n_seq_id, 0, 1);
                var seqIdPtr = Marshal.ReadIntPtr(nextBatch.seq_id, 0);
                Marshal.WriteInt32(seqIdPtr, 0);
                Marshal.WriteByte(nextBatch.logits, 0, 1);
                nextBatch.n_tokens = 1;

                var ret = LlamaCpp.llama_decode(_ctx, nextBatch);
                if (ret != 0) break;
            }
            finally
            {
                LlamaCpp.llama_batch_free(nextBatch);
            }
            curPos++;
        }
    }

    /// <summary>
    /// Clear the KV cache. Call before mtmd_helper_eval_chunks.
    /// </summary>
    public void ClearKvCache()
    {
        if (!IsLoaded) return;
        var memory = LlamaCpp.llama_get_memory(_ctx);
        if (memory != IntPtr.Zero)
            LlamaCpp.llama_memory_clear(memory, false);
    }

    /// <summary>
    /// Run vision inference: process pre-tokenized text tokens and image embeddings from mtmd,
    /// then generate response tokens.
    /// </summary>
    /// <param name="textTokensBefore">Text tokens before the image</param>
    /// <param name="imageEmbeddings">Image embeddings from mtmd_get_output_embd (n_tokens * n_embd floats)</param>
    /// <param name="nImageTokens">Number of image tokens</param>
    /// <param name="textTokensAfter">Text tokens after the image</param>
    /// <param name="maxTokens">Max response tokens to generate</param>
    /// <param name="temperature">Sampling temperature</param>
    /// <param name="topP">Top-p sampling</param>
    public IEnumerable<string> InferVision(int[] textTokensBefore, IntPtr imageEmbeddings, int nImageTokens, int[] textTokensAfter, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.8f)
    {
        if (!IsLoaded) throw new InvalidOperationException("Model not loaded");

        // Vision models need penalties to avoid repetition (Qwen3-VL recommends presence_penalty=1.5)
        ResetVisionSampler(temperature, topP, 20);

        var memory = LlamaCpp.llama_get_memory(_ctx);
        if (memory != IntPtr.Zero)
            LlamaCpp.llama_memory_clear(memory, false);

        var nEmbd = EmbeddingDim;
        var curPos = 0;

        Console.WriteLine($"[LlamaCppEngine] InferVision: textBefore={textTokensBefore.Length}, imgTokens={nImageTokens}, textAfter={textTokensAfter.Length}, nEmbd={nEmbd}");

        // 1. Process text tokens before image
        if (textTokensBefore.Length > 0)
        {
            DecodeTokenBatch(textTokensBefore, ref curPos, isLast: false);
        }

        // 2. Process image embeddings using mtmd_encode_chunk result
        // The embeddings from mtmd are already projected to text model's n_embd
        if (imageEmbeddings != IntPtr.Zero && nImageTokens > 0)
        {
            Console.WriteLine($"[LlamaCppEngine] Decoding {nImageTokens} image embedding tokens (embd_dim={nEmbd})");
            var batchSize = Math.Min(512, nImageTokens);
            for (int i = 0; i < nImageTokens; i += batchSize)
            {
                var count = Math.Min(batchSize, nImageTokens - i);
                // Create batch with embeddings
                var batch = LlamaCpp.llama_batch_init(count, nEmbd, 1);
                try
                {
                    // Copy embeddings into batch.embd
                    var srcOffset = i * nEmbd * sizeof(float);
                    var srcPtr = IntPtr.Add(imageEmbeddings, srcOffset);
                    var byteCount = count * nEmbd * sizeof(float);
                    var tempBuffer = new byte[byteCount];
                    Marshal.Copy(srcPtr, tempBuffer, 0, byteCount);
                    Marshal.Copy(tempBuffer, 0, batch.embd, byteCount);

                    for (int j = 0; j < count; j++)
                    {
                        Marshal.WriteInt32(batch.pos, j * 4, curPos + j);
                        Marshal.WriteInt32(batch.n_seq_id, j * 4, 1);
                        var seqIdPtr = Marshal.ReadIntPtr(batch.seq_id, j * IntPtr.Size);
                        Marshal.WriteInt32(seqIdPtr, 0);
                        var isLast = (i + j == nImageTokens - 1 && textTokensAfter.Length == 0) ? (byte)1 : (byte)0;
                        Marshal.WriteByte(batch.logits, j, isLast);
                    }
                    batch.n_tokens = count;

                    var ret = LlamaCpp.llama_decode(_ctx, batch);
                    if (ret != 0)
                    {
                        Console.WriteLine($"[LlamaCppEngine] llama_decode (embd) failed: {ret} at batch offset {i}");
                        throw new InvalidOperationException($"llama_decode (embd) failed: {ret}");
                    }
                }
                finally
                {
                    LlamaCpp.llama_batch_free(batch);
                }
                curPos += count;
            }
            Console.WriteLine($"[LlamaCppEngine] Image embeddings decoded, curPos={curPos}");
        }

        // 3. Process text tokens after image
        if (textTokensAfter.Length > 0)
        {
            DecodeTokenBatch(textTokensAfter, ref curPos, isLast: true);
        }

        // 4. Generate response tokens
        var nGenerated = 0;
        while (nGenerated < maxTokens)
        {
            var newToken = LlamaCpp.llama_sampler_sample(_sampler, _ctx, -1);
            if (IsEog(newToken)) break;

            nGenerated++;
            var piece = TokenToString(newToken);

            if (piece.Contains("<|im_end|>") || piece.Contains("<|im_start|>") || piece.Contains("<|endoftext|>"))
                break;

            yield return piece;

            // Decode next token
            var nextBatch = LlamaCpp.llama_batch_init(1, 0, 1);
            try
            {
                Marshal.WriteInt32(nextBatch.token, 0, newToken);
                Marshal.WriteInt32(nextBatch.pos, 0, curPos);
                Marshal.WriteInt32(nextBatch.n_seq_id, 0, 1);
                var seqIdPtr = Marshal.ReadIntPtr(nextBatch.seq_id, 0);
                Marshal.WriteInt32(seqIdPtr, 0);
                Marshal.WriteByte(nextBatch.logits, 0, 1);
                nextBatch.n_tokens = 1;

                var ret = LlamaCpp.llama_decode(_ctx, nextBatch);
                if (ret != 0) break;
            }
            finally
            {
                LlamaCpp.llama_batch_free(nextBatch);
            }
            curPos++;
        }
    }

    private void DecodeTokenBatch(int[] tokens, ref int curPos, bool isLast)
    {
        var batchSize = 512;
        for (int i = 0; i < tokens.Length; i += batchSize)
        {
            var count = Math.Min(batchSize, tokens.Length - i);
            var batchTokens = tokens[i..(i + count)];

            var batch = LlamaCpp.llama_batch_init(count, 0, 1);
            try
            {
                for (int j = 0; j < count; j++)
                {
                    Marshal.WriteInt32(batch.token, j * 4, batchTokens[j]);
                    Marshal.WriteInt32(batch.pos, j * 4, curPos + j);
                    Marshal.WriteInt32(batch.n_seq_id, j * 4, 1);
                    var seqIdPtr = Marshal.ReadIntPtr(batch.seq_id, j * IntPtr.Size);
                    Marshal.WriteInt32(seqIdPtr, 0);
                    var lastInBatch = (isLast && i + j == tokens.Length - 1) ? (byte)1 : (byte)0;
                    Marshal.WriteByte(batch.logits, j, lastInBatch);
                }
                batch.n_tokens = count;

                var ret = LlamaCpp.llama_decode(_ctx, batch);
                if (ret != 0)
                    throw new InvalidOperationException($"llama_decode failed: {ret}");
            }
            finally
            {
                LlamaCpp.llama_batch_free(batch);
            }
            curPos += count;
        }
    }

    /// <summary>
    /// Run inference yielding raw text tokens exactly as model outputs (including special tokens like think tags).
    /// </summary>
    public IEnumerable<string> InferRaw(string prompt, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.9f)
    {
        if (!IsLoaded) throw new InvalidOperationException("Model not loaded");

        ResetSampler(temperature, topP, 50);

        var memory = LlamaCpp.llama_get_memory(_ctx);
        if (memory != IntPtr.Zero)
            LlamaCpp.llama_memory_clear(memory, false);

        var tokens = Tokenize(prompt, addSpecial: false, parseSpecial: true);
        var nCtx = (int)LlamaCpp.llama_n_ctx(_ctx);

        if (tokens.Length >= nCtx)
            throw new InvalidOperationException($"Prompt too long: {tokens.Length} tokens > context {nCtx}");

        var batchSize = 512;
        for (int i = 0; i < tokens.Length; i += batchSize)
        {
            var count = Math.Min(batchSize, tokens.Length - i);
            var batchTokens = tokens[i..(i + count)];

            var batch = LlamaCpp.llama_batch_init(count, 0, 1);
            try
            {
                for (int j = 0; j < count; j++)
                {
                    Marshal.WriteInt32(batch.token, j * 4, batchTokens[j]);
                    Marshal.WriteInt32(batch.pos, j * 4, i + j);
                    Marshal.WriteInt32(batch.n_seq_id, j * 4, 1);
                    var seqIdPtr = Marshal.ReadIntPtr(batch.seq_id, j * IntPtr.Size);
                    Marshal.WriteInt32(seqIdPtr, 0);
                    var isLast = (i + j == tokens.Length - 1) ? (byte)1 : (byte)0;
                    Marshal.WriteByte(batch.logits, j, isLast);
                }
                batch.n_tokens = count;

                var ret = LlamaCpp.llama_decode(_ctx, batch);
                if (ret != 0)
                    throw new InvalidOperationException($"llama_decode failed: {ret}");
            }
            finally
            {
                LlamaCpp.llama_batch_free(batch);
            }
        }

        var nGenerated = 0;
        var curPos = tokens.Length;

        Console.WriteLine($"[LlamaCppEngine] InferRaw: prompt={tokens.Length} tokens, starting generation...");

        // Reset sampler state before generation
        LlamaCpp.llama_sampler_reset(_sampler);

        while (nGenerated < maxTokens)
        {
            var newToken = LlamaCpp.llama_sampler_sample(_sampler, _ctx, -1);

            if (nGenerated == 0)
                Console.WriteLine($"[LlamaCppEngine] First token: id={newToken}, isEog={IsEog(newToken)}, text='{TokenToString(newToken)}'");

            // Check for true EOS (end of text) but NOT <|im_end|> which is just end-of-turn
            // For Qwen3/3.5: <|im_end|> and <|endoftext|> are both marked as EOG
            // We handle them via text content check instead
            var piece = TokenToString(newToken);

            // Stop on actual end-of-generation tokens
            if (piece.Contains("<|im_end|>") || piece.Contains("<|im_start|>") || piece.Contains("<|endoftext|>"))
            {
                if (nGenerated == 0)
                    Console.WriteLine($"[LlamaCppEngine] Stop token at first position, skipping: '{piece}'");
                break;
            }

            // Also check is_eog but only for tokens that aren't the above (e.g. true EOS with no text)
            if (IsEog(newToken) && string.IsNullOrEmpty(piece))
                break;

            nGenerated++;
            yield return piece;

            var nextBatch = LlamaCpp.llama_batch_init(1, 0, 1);
            try
            {
                Marshal.WriteInt32(nextBatch.token, 0, newToken);
                Marshal.WriteInt32(nextBatch.pos, 0, curPos);
                Marshal.WriteInt32(nextBatch.n_seq_id, 0, 1);
                var seqIdPtr = Marshal.ReadIntPtr(nextBatch.seq_id, 0);
                Marshal.WriteInt32(seqIdPtr, 0);
                Marshal.WriteByte(nextBatch.logits, 0, 1);
                nextBatch.n_tokens = 1;

                var ret = LlamaCpp.llama_decode(_ctx, nextBatch);
                if (ret != 0) break;
            }
            finally
            {
                LlamaCpp.llama_batch_free(nextBatch);
            }

            curPos++;
        }
    }

    /// <summary>
    /// Run inference yielding text tokens (tags stripped via InferWithThinking).
    /// </summary>
    public IEnumerable<string> Infer(string prompt, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.9f)
    {
        foreach (var token in InferWithThinking(prompt, maxTokens, temperature, topP))
            yield return token.Text;
    }

    /// <summary>
    /// Run inference with think/response separation.
    /// Parses &lt;think&gt;...&lt;/think&gt; blocks from the model output stream.
    /// Yields InferenceToken tagged as Think or Response content.
    /// </summary>
    public IEnumerable<InferenceToken> InferWithThinking(string prompt, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.9f)
    {
        if (!IsLoaded) throw new InvalidOperationException("Model not loaded");

        ResetSampler(temperature, topP, 50);

        var memory = LlamaCpp.llama_get_memory(_ctx);
        if (memory != IntPtr.Zero)
            LlamaCpp.llama_memory_clear(memory, false);

        var tokens = Tokenize(prompt, addSpecial: false, parseSpecial: true);
        var nCtx = (int)LlamaCpp.llama_n_ctx(_ctx);

        if (tokens.Length >= nCtx)
            throw new InvalidOperationException($"Prompt too long: {tokens.Length} tokens > context {nCtx}");

        // Process prompt in batch
        var batchSize = 512;
        for (int i = 0; i < tokens.Length; i += batchSize)
        {
            var count = Math.Min(batchSize, tokens.Length - i);
            var batchTokens = tokens[i..(i + count)];

            var batch = LlamaCpp.llama_batch_init(count, 0, 1);
            try
            {
                for (int j = 0; j < count; j++)
                {
                    Marshal.WriteInt32(batch.token, j * 4, batchTokens[j]);
                    Marshal.WriteInt32(batch.pos, j * 4, i + j);
                    Marshal.WriteInt32(batch.n_seq_id, j * 4, 1);
                    var seqIdPtr = Marshal.ReadIntPtr(batch.seq_id, j * IntPtr.Size);
                    Marshal.WriteInt32(seqIdPtr, 0);
                    var isLast = (i + j == tokens.Length - 1) ? (byte)1 : (byte)0;
                    Marshal.WriteByte(batch.logits, j, isLast);
                }
                batch.n_tokens = count;

                var ret = LlamaCpp.llama_decode(_ctx, batch);
                if (ret != 0)
                    throw new InvalidOperationException($"llama_decode failed: {ret}");
            }
            finally
            {
                LlamaCpp.llama_batch_free(batch);
            }
        }

        // Generate tokens with think/response parsing
        var nGenerated = 0;
        var curPos = tokens.Length;
        var buffer = new StringBuilder();
        var isThinking = false;
        var thinkTagDetected = false;

        while (nGenerated < maxTokens)
        {
            var newToken = LlamaCpp.llama_sampler_sample(_sampler, _ctx, -1);

            if (IsEog(newToken))
                break;

            nGenerated++;
            var piece = TokenToString(newToken);

            // Check for stop tokens
            if (piece.Contains("<|im_end|>") || piece.Contains("<|im_start|>") || piece.Contains("<|endoftext|>"))
                break;

            buffer.Append(piece);
            var bufStr = buffer.ToString();

            // State machine for <think>...</think> parsing
            if (!thinkTagDetected)
            {
                if (bufStr.Contains("<think>"))
                {
                    thinkTagDetected = true;
                    isThinking = true;
                    var idx = bufStr.IndexOf("<think>");
                    if (idx > 0)
                        yield return new InferenceToken(bufStr[..idx], false);
                    buffer.Clear();
                    buffer.Append(bufStr[(idx + 7)..]);
                }
                else if (!bufStr.StartsWith("<") || bufStr.Length > 10)
                {
                    // No think tag, emit as response
                    yield return new InferenceToken(bufStr, false);
                    buffer.Clear();
                }
            }
            else if (isThinking)
            {
                if (bufStr.Contains("</think>"))
                {
                    var idx = bufStr.IndexOf("</think>");
                    if (idx > 0)
                        yield return new InferenceToken(bufStr[..idx], true);
                    isThinking = false;
                    buffer.Clear();
                    var afterClose = bufStr[(idx + 8)..];
                    if (afterClose.Length > 0)
                        yield return new InferenceToken(afterClose, false);
                }
                else if (bufStr.Length > 8)
                {
                    // Emit think content, keep tail for tag detection
                    var emitLen = bufStr.Length - 8;
                    yield return new InferenceToken(bufStr[..emitLen], true);
                    buffer.Clear();
                    buffer.Append(bufStr[emitLen..]);
                }
            }
            else
            {
                // After </think>, everything is response
                yield return new InferenceToken(bufStr, false);
                buffer.Clear();
            }

            // Decode next token
            var nextBatch = LlamaCpp.llama_batch_init(1, 0, 1);
            try
            {
                Marshal.WriteInt32(nextBatch.token, 0, newToken);
                Marshal.WriteInt32(nextBatch.pos, 0, curPos);
                Marshal.WriteInt32(nextBatch.n_seq_id, 0, 1);
                var seqIdPtr = Marshal.ReadIntPtr(nextBatch.seq_id, 0);
                Marshal.WriteInt32(seqIdPtr, 0);
                Marshal.WriteByte(nextBatch.logits, 0, 1);
                nextBatch.n_tokens = 1;

                var ret = LlamaCpp.llama_decode(_ctx, nextBatch);
                if (ret != 0) break;
            }
            finally
            {
                LlamaCpp.llama_batch_free(nextBatch);
            }

            curPos++;
        }

        // Flush remaining buffer
        if (buffer.Length > 0)
            yield return new InferenceToken(buffer.ToString(), isThinking);
    }

    private static long GetDeviceRamMB()
    {
        try
        {
#if ANDROID
            var activityManager = Android.App.Application.Context.GetSystemService(Android.Content.Context.ActivityService) as Android.App.ActivityManager;
            if (activityManager != null)
            {
                var memInfo = new Android.App.ActivityManager.MemoryInfo();
                activityManager.GetMemoryInfo(memInfo);
                return memInfo.TotalMem / (1024 * 1024);
            }
#elif IOS || MACCATALYST
            return (long)(Foundation.NSProcessInfo.ProcessInfo.PhysicalMemory / (1024 * 1024));
#endif
            var gcMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (gcMem > 0) return gcMem / (1024 * 1024);
        }
        catch { }
        return 4096; // Default assume 4GB
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sampler != IntPtr.Zero)
        {
            LlamaCpp.llama_sampler_free(_sampler);
            _sampler = IntPtr.Zero;
        }
        if (_ctx != IntPtr.Zero)
        {
            LlamaCpp.llama_free(_ctx);
            _ctx = IntPtr.Zero;
        }
        if (_model != IntPtr.Zero)
        {
            LlamaCpp.llama_model_free(_model);
            _model = IntPtr.Zero;
        }
    }
}
