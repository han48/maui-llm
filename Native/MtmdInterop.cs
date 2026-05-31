using System.Runtime.InteropServices;

namespace AIAgentLocal.Native;

/// <summary>
/// P/Invoke bindings to llama.cpp's mtmd (multimodal) C API.
/// Used for vision model support (Qwen3-VL).
/// Not available on iOS Simulator (native lib is device-only).
/// </summary>
internal static class MtmdCpp
{
#if IOS && !IOS_SIMULATOR
    private const string LibName = "__Internal";
#elif ANDROID
    private const string LibName = "mtmd";
#elif MACCATALYST
    private const string LibName = "mtmd";
#else
    private const string LibName = "mtmd";
#endif

#if !IOS_SIMULATOR
    // Context management
    [DllImport(LibName)]
    public static extern MtmdContextParams mtmd_context_params_default();

    [DllImport(LibName)]
    public static extern IntPtr mtmd_init_from_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mmproj_fname,
        IntPtr text_model,
        MtmdContextParams ctx_params);

    [DllImport(LibName)]
    public static extern void mtmd_free(IntPtr ctx);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool mtmd_support_vision(IntPtr ctx);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool mtmd_decode_use_non_causal(IntPtr ctx, IntPtr chunk);

    [DllImport(LibName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool mtmd_decode_use_mrope(IntPtr ctx);

    // Bitmap (image data)
    [DllImport(LibName)]
    public static extern IntPtr mtmd_bitmap_init(uint nx, uint ny, IntPtr data);

    [DllImport(LibName)]
    public static extern void mtmd_bitmap_free(IntPtr bitmap);

    [DllImport(LibName)]
    public static extern uint mtmd_bitmap_get_nx(IntPtr bitmap);

    [DllImport(LibName)]
    public static extern uint mtmd_bitmap_get_ny(IntPtr bitmap);

    // Input chunks
    [DllImport(LibName)]
    public static extern IntPtr mtmd_input_chunks_init();

    [DllImport(LibName)]
    public static extern nint mtmd_input_chunks_size(IntPtr chunks);

    [DllImport(LibName)]
    public static extern IntPtr mtmd_input_chunks_get(IntPtr chunks, nint idx);

    [DllImport(LibName)]
    public static extern void mtmd_input_chunks_free(IntPtr chunks);

    // Input chunk
    [DllImport(LibName)]
    public static extern int mtmd_input_chunk_get_type(IntPtr chunk);

    [DllImport(LibName)]
    public static extern IntPtr mtmd_input_chunk_get_tokens_text(IntPtr chunk, out nint n_tokens);

    [DllImport(LibName)]
    public static extern IntPtr mtmd_input_chunk_get_tokens_image(IntPtr chunk);

    [DllImport(LibName)]
    public static extern nint mtmd_input_chunk_get_n_tokens(IntPtr chunk);

    // Tokenize
    [DllImport(LibName)]
    public static extern int mtmd_tokenize(
        IntPtr ctx,
        IntPtr output,
        ref MtmdInputText text,
        IntPtr[] bitmaps,
        nint n_bitmaps);

    // Encode
    [DllImport(LibName)]
    public static extern int mtmd_encode_chunk(IntPtr ctx, IntPtr chunk);

    // Get output embeddings
    [DllImport(LibName)]
    public static extern IntPtr mtmd_get_output_embd(IntPtr ctx);

    // Image tokens
    [DllImport(LibName)]
    public static extern nint mtmd_image_tokens_get_n_tokens(IntPtr image_tokens);

    // Default marker
    [DllImport(LibName)]
    public static extern IntPtr mtmd_default_marker();

    // Helper functions (from mtmd-helper.h)
    [DllImport(LibName)]
    public static extern int mtmd_helper_eval_chunks(
        IntPtr ctx,          // mtmd_context*
        IntPtr lctx,         // llama_context*
        IntPtr chunks,       // mtmd_input_chunks*
        int n_past,          // llama_pos
        int seq_id,          // llama_seq_id
        int n_batch,         // int32_t
        [MarshalAs(UnmanagedType.I1)] bool logits_last,
        out int new_n_past); // llama_pos*

    [DllImport(LibName)]
    public static extern nint mtmd_helper_get_n_tokens(IntPtr chunks);

    [DllImport(LibName)]
    public static extern int mtmd_helper_get_n_pos(IntPtr chunks);

    [DllImport(LibName)]
    public static extern IntPtr mtmd_helper_bitmap_init_from_file(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fname);

    [DllImport(LibName)]
    public static extern IntPtr mtmd_helper_bitmap_init_from_buf(
        IntPtr ctx,
        IntPtr buf,
        nint len);
#else
    // Stub implementations for iOS Simulator where native mtmd lib is unavailable
    public static MtmdContextParams mtmd_context_params_default() => throw new PlatformNotSupportedException("mtmd is not available on iOS Simulator");
    public static IntPtr mtmd_init_from_file(string mmproj_fname, IntPtr text_model, MtmdContextParams ctx_params) => throw new PlatformNotSupportedException("mtmd is not available on iOS Simulator");
    public static void mtmd_free(IntPtr ctx) { }
    public static bool mtmd_support_vision(IntPtr ctx) => false;
    public static bool mtmd_decode_use_non_causal(IntPtr ctx, IntPtr chunk) => false;
    public static bool mtmd_decode_use_mrope(IntPtr ctx) => false;
    public static IntPtr mtmd_bitmap_init(uint nx, uint ny, IntPtr data) => IntPtr.Zero;
    public static void mtmd_bitmap_free(IntPtr bitmap) { }
    public static uint mtmd_bitmap_get_nx(IntPtr bitmap) => 0;
    public static uint mtmd_bitmap_get_ny(IntPtr bitmap) => 0;
    public static IntPtr mtmd_input_chunks_init() => IntPtr.Zero;
    public static nint mtmd_input_chunks_size(IntPtr chunks) => 0;
    public static IntPtr mtmd_input_chunks_get(IntPtr chunks, nint idx) => IntPtr.Zero;
    public static void mtmd_input_chunks_free(IntPtr chunks) { }
    public static int mtmd_input_chunk_get_type(IntPtr chunk) => 0;
    public static IntPtr mtmd_input_chunk_get_tokens_text(IntPtr chunk, out nint n_tokens) { n_tokens = 0; return IntPtr.Zero; }
    public static IntPtr mtmd_input_chunk_get_tokens_image(IntPtr chunk) => IntPtr.Zero;
    public static nint mtmd_input_chunk_get_n_tokens(IntPtr chunk) => 0;
    public static int mtmd_tokenize(IntPtr ctx, IntPtr output, ref MtmdInputText text, IntPtr[] bitmaps, nint n_bitmaps) => -1;
    public static int mtmd_encode_chunk(IntPtr ctx, IntPtr chunk) => -1;
    public static IntPtr mtmd_get_output_embd(IntPtr ctx) => IntPtr.Zero;
    public static nint mtmd_image_tokens_get_n_tokens(IntPtr image_tokens) => 0;
    public static IntPtr mtmd_default_marker() => IntPtr.Zero;
    public static int mtmd_helper_eval_chunks(IntPtr ctx, IntPtr lctx, IntPtr chunks, int n_past, int seq_id, int n_batch, bool logits_last, out int new_n_past) { new_n_past = 0; return -1; }
    public static nint mtmd_helper_get_n_tokens(IntPtr chunks) => 0;
    public static int mtmd_helper_get_n_pos(IntPtr chunks) => 0;
    public static IntPtr mtmd_helper_bitmap_init_from_file(IntPtr ctx, string fname) => IntPtr.Zero;
    public static IntPtr mtmd_helper_bitmap_init_from_buf(IntPtr ctx, IntPtr buf, nint len) => IntPtr.Zero;
#endif
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtmdContextParams
{
    [MarshalAs(UnmanagedType.I1)] public bool use_gpu;
    [MarshalAs(UnmanagedType.I1)] public bool print_timings;
    public int n_threads;
    public IntPtr image_marker;  // const char* (deprecated)
    public IntPtr media_marker;  // const char*
    public int flash_attn_type;
    [MarshalAs(UnmanagedType.I1)] public bool warmup;
    public int image_min_tokens;
    public int image_max_tokens;
    public IntPtr cb_eval;
    public IntPtr cb_eval_user_data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtmdInputText
{
    public IntPtr text;  // const char*
    [MarshalAs(UnmanagedType.I1)] public bool add_special;
    [MarshalAs(UnmanagedType.I1)] public bool parse_special;
}

internal enum MtmdInputChunkType
{
    Text = 0,
    Image = 1,
    Audio = 2,
}
