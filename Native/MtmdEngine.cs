using System.Runtime.InteropServices;
using System.Text;

namespace AIAgentLocal.Native;

/// <summary>
/// High-level wrapper for multimodal (vision) inference using llama.cpp's mtmd library.
/// Works alongside LlamaCppEngine to provide image understanding capabilities.
/// </summary>
internal sealed class MtmdEngine : IDisposable
{
    private IntPtr _mtmdCtx;
    private bool _disposed;

    public bool IsLoaded => _mtmdCtx != IntPtr.Zero;

    /// <summary>Pointer to the mtmd context (needed for getting embeddings).</summary>
    public IntPtr ContextPtr => _mtmdCtx;

    /// <summary>
    /// Initialize the multimodal context with the mmproj file and the loaded text model.
    /// Must be called after the text model is loaded via LlamaCppEngine.
    /// </summary>
    public void LoadMmproj(string mmprojPath, IntPtr textModel, int nThreads = 0)
    {
        if (textModel == IntPtr.Zero)
            throw new InvalidOperationException("Text model must be loaded first");

        if (!File.Exists(mmprojPath))
            throw new FileNotFoundException($"mmproj file not found: {mmprojPath}");

        if (nThreads <= 0)
            nThreads = Math.Max(1, Environment.ProcessorCount - 1);

        Console.WriteLine($"[MtmdEngine] Loading mmproj: {mmprojPath}, threads={nThreads}");

        var ctxParams = MtmdCpp.mtmd_context_params_default();
        ctxParams.n_threads = nThreads;
        ctxParams.use_gpu = false; // CPU for mobile compatibility
        ctxParams.print_timings = true;
        ctxParams.warmup = false;

        _mtmdCtx = MtmdCpp.mtmd_init_from_file(mmprojPath, textModel, ctxParams);
        if (_mtmdCtx == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to initialize mtmd context from: {mmprojPath}");

        var supportsVision = MtmdCpp.mtmd_support_vision(_mtmdCtx);
        Console.WriteLine($"[MtmdEngine] Loaded successfully. Supports vision: {supportsVision}");
    }

    /// <summary>
    /// Tokenize a prompt with an image into input chunks.
    /// The prompt must contain the media marker where the image should be inserted.
    /// </summary>
    /// <param name="prompt">Text prompt with media marker</param>
    /// <param name="imageData">Raw RGB image data (width * height * 3 bytes)</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <returns>Pointer to input chunks (must be freed with mtmd_input_chunks_free)</returns>
    public IntPtr TokenizeWithImage(string prompt, byte[] imageData, uint width, uint height)
    {
        if (!IsLoaded)
            throw new InvalidOperationException("MtmdEngine not loaded");

        // Create bitmap from image data
        var dataHandle = GCHandle.Alloc(imageData, GCHandleType.Pinned);
        IntPtr bitmap = IntPtr.Zero;
        IntPtr chunks = IntPtr.Zero;

        try
        {
            bitmap = MtmdCpp.mtmd_bitmap_init(width, height, dataHandle.AddrOfPinnedObject());
            if (bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create mtmd bitmap");

            // Create input chunks
            chunks = MtmdCpp.mtmd_input_chunks_init();
            if (chunks == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create input chunks");

            // Prepare input text
            var promptBytes = Encoding.UTF8.GetBytes(prompt + "\0");
            var promptHandle = GCHandle.Alloc(promptBytes, GCHandleType.Pinned);

            try
            {
                var inputText = new MtmdInputText
                {
                    text = promptHandle.AddrOfPinnedObject(),
                    add_special = true,
                    parse_special = true
                };

                var bitmaps = new IntPtr[] { bitmap };
                var result = MtmdCpp.mtmd_tokenize(_mtmdCtx, chunks, ref inputText, bitmaps, 1);

                if (result != 0)
                {
                    MtmdCpp.mtmd_input_chunks_free(chunks);
                    throw new InvalidOperationException($"mtmd_tokenize failed with code: {result}");
                }

                var nChunks = MtmdCpp.mtmd_input_chunks_size(chunks);
                Console.WriteLine($"[MtmdEngine] Tokenized into {nChunks} chunks");

                return chunks;
            }
            finally
            {
                promptHandle.Free();
            }
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                MtmdCpp.mtmd_bitmap_free(bitmap);
            dataHandle.Free();
        }
    }

    /// <summary>
    /// Encode an image chunk to get embeddings.
    /// </summary>
    public float[]? EncodeImageChunk(IntPtr chunk)
    {
        if (!IsLoaded)
            throw new InvalidOperationException("MtmdEngine not loaded");

        var result = MtmdCpp.mtmd_encode_chunk(_mtmdCtx, chunk);
        if (result != 0)
        {
            Console.WriteLine($"[MtmdEngine] mtmd_encode_chunk failed: {result}");
            return null;
        }

        var embdPtr = MtmdCpp.mtmd_get_output_embd(_mtmdCtx);
        if (embdPtr == IntPtr.Zero)
            return null;

        var nTokens = MtmdCpp.mtmd_input_chunk_get_n_tokens(chunk);
        // We'd need n_embd from the model to know the full size
        // For now return the pointer info
        Console.WriteLine($"[MtmdEngine] Encoded image chunk, n_tokens={nTokens}");
        return null; // Embeddings are used internally by the decode pipeline
    }

    /// <summary>
    /// Get the number of chunks from a tokenized result.
    /// </summary>
    public nint GetChunkCount(IntPtr chunks) => MtmdCpp.mtmd_input_chunks_size(chunks);

    /// <summary>
    /// Get a specific chunk from the tokenized result.
    /// </summary>
    public IntPtr GetChunk(IntPtr chunks, nint idx) => MtmdCpp.mtmd_input_chunks_get(chunks, idx);

    /// <summary>
    /// Get the type of a chunk.
    /// </summary>
    public MtmdInputChunkType GetChunkType(IntPtr chunk) => (MtmdInputChunkType)MtmdCpp.mtmd_input_chunk_get_type(chunk);

    /// <summary>
    /// Get text tokens from a text chunk.
    /// </summary>
    public int[] GetTextTokens(IntPtr chunk)
    {
        var tokensPtr = MtmdCpp.mtmd_input_chunk_get_tokens_text(chunk, out var nTokens);
        if (tokensPtr == IntPtr.Zero || nTokens <= 0)
            return Array.Empty<int>();

        var tokens = new int[nTokens];
        Marshal.Copy(tokensPtr, tokens, 0, (int)nTokens);
        return tokens;
    }

    /// <summary>
    /// Get the number of tokens in a chunk.
    /// </summary>
    public nint GetChunkTokenCount(IntPtr chunk) => MtmdCpp.mtmd_input_chunk_get_n_tokens(chunk);

    /// <summary>
    /// Check if decode should use non-causal attention for a chunk.
    /// </summary>
    public bool ShouldUseNonCausal(IntPtr chunk) => MtmdCpp.mtmd_decode_use_non_causal(_mtmdCtx, chunk);

    /// <summary>
    /// Check if the model uses M-RoPE.
    /// </summary>
    public bool UsesMRope() => MtmdCpp.mtmd_decode_use_mrope(_mtmdCtx);

    /// <summary>
    /// Free input chunks.
    /// </summary>
    public void FreeChunks(IntPtr chunks)
    {
        if (chunks != IntPtr.Zero)
            MtmdCpp.mtmd_input_chunks_free(chunks);
    }

    /// <summary>
    /// Evaluate all chunks using the helper function.
    /// This handles text decode, image encode+decode with proper M-RoPE positions automatically.
    /// Returns new n_past position after all chunks are processed.
    /// </summary>
    public int EvalChunks(IntPtr llamaCtx, IntPtr chunks, int nPast, int nBatch = 512)
    {
        if (!IsLoaded)
            throw new InvalidOperationException("MtmdEngine not loaded");

        var ret = MtmdCpp.mtmd_helper_eval_chunks(
            _mtmdCtx, llamaCtx, chunks,
            nPast, 0, nBatch, true, out var newNPast);

        if (ret != 0)
            throw new InvalidOperationException($"mtmd_helper_eval_chunks failed: {ret}");

        Console.WriteLine($"[MtmdEngine] EvalChunks: n_past {nPast} -> {newNPast}");
        return newNPast;
    }

    /// <summary>
    /// Load a bitmap directly from a file path (supports jpg, png, bmp, gif, etc.)
    /// Returns bitmap pointer or IntPtr.Zero on failure.
    /// </summary>
    public IntPtr LoadBitmapFromFile(string filePath)
    {
        if (!IsLoaded) return IntPtr.Zero;
        return MtmdCpp.mtmd_helper_bitmap_init_from_file(_mtmdCtx, filePath);
    }

    /// <summary>
    /// Tokenize a prompt with an image file directly (using helper bitmap loader).
    /// Simpler than TokenizeWithImage - no need to manually decode image to RGB.
    /// </summary>
    public IntPtr TokenizeWithImageFile(string prompt, string imageFilePath)
    {
        if (!IsLoaded)
            throw new InvalidOperationException("MtmdEngine not loaded");

        var bitmap = MtmdCpp.mtmd_helper_bitmap_init_from_file(_mtmdCtx, imageFilePath);
        if (bitmap == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load image: {imageFilePath}");

        try
        {
            var chunks = MtmdCpp.mtmd_input_chunks_init();
            if (chunks == IntPtr.Zero)
            {
                MtmdCpp.mtmd_bitmap_free(bitmap);
                throw new InvalidOperationException("Failed to create input chunks");
            }

            var promptBytes = System.Text.Encoding.UTF8.GetBytes(prompt + "\0");
            var promptHandle = System.Runtime.InteropServices.GCHandle.Alloc(promptBytes, System.Runtime.InteropServices.GCHandleType.Pinned);

            try
            {
                var inputText = new MtmdInputText
                {
                    text = promptHandle.AddrOfPinnedObject(),
                    add_special = true,
                    parse_special = true
                };

                var bitmaps = new IntPtr[] { bitmap };
                var result = MtmdCpp.mtmd_tokenize(_mtmdCtx, chunks, ref inputText, bitmaps, 1);

                if (result != 0)
                {
                    MtmdCpp.mtmd_input_chunks_free(chunks);
                    throw new InvalidOperationException($"mtmd_tokenize failed: {result}");
                }

                return chunks;
            }
            finally
            {
                promptHandle.Free();
            }
        }
        finally
        {
            MtmdCpp.mtmd_bitmap_free(bitmap);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_mtmdCtx != IntPtr.Zero)
        {
            MtmdCpp.mtmd_free(_mtmdCtx);
            _mtmdCtx = IntPtr.Zero;
        }
    }
}
