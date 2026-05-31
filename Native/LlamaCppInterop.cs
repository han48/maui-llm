using System.Runtime.InteropServices;

namespace AIAgentLocal.Native;

/// <summary>
/// Direct P/Invoke bindings to llama.cpp C API.
/// Used on iOS and Android where LLamaSharp's internal library loading doesn't work.
/// iOS: linked via xcframework (__Internal)
/// Android: loaded from libllama.so
/// </summary>
internal static class LlamaCpp
{
#if IOS
    private const string LibName = "__Internal";
#elif ANDROID
    private const string LibName = "llama";
#elif MACCATALYST
    private const string LibName = "llama";
#else
    private const string LibName = "llama";
#endif

    // Backend
    [DllImport(LibName)] public static extern void llama_backend_init();
    [DllImport(LibName)] public static extern void llama_backend_free();
    [DllImport(LibName)] public static extern IntPtr llama_print_system_info();

    // Model params
    [DllImport(LibName, EntryPoint = "llama_model_default_params")]
    public static extern LlamaModelParams llama_model_default_params();
    
    [DllImport(LibName)]
    public static extern IntPtr llama_model_load_from_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, LlamaModelParams @params);
    
    [DllImport(LibName)] public static extern void llama_model_free(IntPtr model);

    // Safe model loading with detailed logging
    public static IntPtr llama_model_load_from_file_safe(string path)
    {
        var modelParams = llama_model_default_params();
        Console.WriteLine($"[LlamaCpp] default_params: n_gpu_layers={modelParams.n_gpu_layers}, use_mmap={modelParams.use_mmap}, struct_size={System.Runtime.InteropServices.Marshal.SizeOf<LlamaModelParams>()}");
        modelParams.n_gpu_layers = 0;
        modelParams.use_mmap = true;
        modelParams.use_mlock = false;
        modelParams.check_tensors = false;
        
        Console.WriteLine($"[LlamaCpp] Calling llama_model_load_from_file('{path}', params)...");
        var result = llama_model_load_from_file(path, modelParams);
        Console.WriteLine($"[LlamaCpp] Result: {result}");
        return result;
    }

    // Context params
    [DllImport(LibName)] public static extern LlamaContextParams llama_context_default_params();
    [DllImport(LibName)] public static extern IntPtr llama_init_from_model(IntPtr model, LlamaContextParams @params);
    [DllImport(LibName)] public static extern void llama_free(IntPtr ctx);

    // Model info
    [DllImport(LibName)] public static extern IntPtr llama_model_get_vocab(IntPtr model);
    [DllImport(LibName)] public static extern int llama_vocab_n_tokens(IntPtr vocab);
    [DllImport(LibName)] public static extern int llama_model_n_ctx_train(IntPtr model);
    [DllImport(LibName)] public static extern long llama_model_n_params(IntPtr model);
    [DllImport(LibName)] public static extern int llama_model_n_embd(IntPtr model);

    // Tokenization
    [DllImport(LibName)] public static extern int llama_tokenize(
        IntPtr vocab,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        int text_len,
        int[] tokens,
        int n_tokens_max,
        [MarshalAs(UnmanagedType.I1)] bool add_special,
        [MarshalAs(UnmanagedType.I1)] bool parse_special);

    [DllImport(LibName)] public static extern int llama_token_to_piece(
        IntPtr vocab, int token, byte[] buf, int length, int lstrip,
        [MarshalAs(UnmanagedType.I1)] bool special);

    // Vocab special tokens
    [DllImport(LibName)] public static extern int llama_vocab_bos(IntPtr vocab);
    [DllImport(LibName)] public static extern int llama_vocab_eos(IntPtr vocab);
    [DllImport(LibName, EntryPoint = "llama_vocab_is_eog")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool llama_vocab_is_eog(IntPtr vocab, int token);

    // Batch
    [DllImport(LibName)] public static extern LlamaBatch llama_batch_init(int n_tokens, int embd, int n_seq_max);
    [DllImport(LibName)] public static extern void llama_batch_free(LlamaBatch batch);
    [DllImport(LibName)] public static extern LlamaBatch llama_batch_get_one(IntPtr tokens, int n_tokens);

    // Decode
    [DllImport(LibName)] public static extern int llama_decode(IntPtr ctx, LlamaBatch batch);

    // Context info
    [DllImport(LibName)] public static extern uint llama_n_ctx(IntPtr ctx);

    // Logits
    [DllImport(LibName)] public static extern IntPtr llama_get_logits_ith(IntPtr ctx, int i);

    // KV cache / memory
    [DllImport(LibName)] public static extern void llama_memory_clear(IntPtr memory, [MarshalAs(UnmanagedType.I1)] bool data);
    [DllImport(LibName)] public static extern IntPtr llama_get_memory(IntPtr ctx);

    // Sampler
    [DllImport(LibName)] public static extern LlamaSamplerChainParams llama_sampler_chain_default_params();
    [DllImport(LibName)] public static extern IntPtr llama_sampler_chain_init(LlamaSamplerChainParams @params);
    [DllImport(LibName)] public static extern void llama_sampler_chain_add(IntPtr chain, IntPtr sampler);
    [DllImport(LibName)] public static extern void llama_sampler_free(IntPtr sampler);
    [DllImport(LibName)] public static extern int llama_sampler_sample(IntPtr sampler, IntPtr ctx, int idx);
    [DllImport(LibName)] public static extern void llama_sampler_reset(IntPtr sampler);

    // Sampler factories
    [DllImport(LibName)] public static extern IntPtr llama_sampler_init_top_k(int k);
    [DllImport(LibName)] public static extern IntPtr llama_sampler_init_top_p(float p, nint min_keep);
    [DllImport(LibName)] public static extern IntPtr llama_sampler_init_temp(float t);
    [DllImport(LibName)] public static extern IntPtr llama_sampler_init_dist(uint seed);
    [DllImport(LibName)] public static extern IntPtr llama_sampler_init_min_p(float p, nint min_keep);
    [DllImport(LibName)] public static extern IntPtr llama_sampler_init_penalties(int penalty_last_n, float penalty_repeat, float penalty_freq, float penalty_present);

    // Threading
    [DllImport(LibName)] public static extern void llama_set_n_threads(IntPtr ctx, int n_threads, int n_threads_batch);
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaModelParams
{
    public IntPtr devices;
    public IntPtr tensor_buft_overrides;
    public int n_gpu_layers;
    public int split_mode;
    public int main_gpu;
    public IntPtr tensor_split;
    public IntPtr progress_callback;
    public IntPtr progress_callback_user_data;
    public IntPtr kv_overrides;
    [MarshalAs(UnmanagedType.I1)] public bool vocab_only;
    [MarshalAs(UnmanagedType.I1)] public bool use_mmap;
    [MarshalAs(UnmanagedType.I1)] public bool use_direct_io;
    [MarshalAs(UnmanagedType.I1)] public bool use_mlock;
    [MarshalAs(UnmanagedType.I1)] public bool check_tensors;
    [MarshalAs(UnmanagedType.I1)] public bool use_extra_bufts;
    [MarshalAs(UnmanagedType.I1)] public bool no_host;
    [MarshalAs(UnmanagedType.I1)] public bool no_alloc;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaContextParams
{
    public uint n_ctx;
    public uint n_batch;
    public uint n_ubatch;
    public uint n_seq_max;
    public uint n_rs_seq;
    public int n_threads;
    public int n_threads_batch;
    public int ctx_type;
    public int rope_scaling_type;
    public int pooling_type;
    public int attention_type;
    public int flash_attn_type;
    public float rope_freq_base;
    public float rope_freq_scale;
    public float yarn_ext_factor;
    public float yarn_attn_factor;
    public float yarn_beta_fast;
    public float yarn_beta_slow;
    public uint yarn_orig_ctx;
    public float defrag_thold;
    public IntPtr cb_eval;
    public IntPtr cb_eval_user_data;
    public int type_k;
    public int type_v;
    public IntPtr abort_callback;
    public IntPtr abort_callback_data;
    [MarshalAs(UnmanagedType.I1)] public bool embeddings;
    [MarshalAs(UnmanagedType.I1)] public bool offload_kqv;
    [MarshalAs(UnmanagedType.I1)] public bool no_perf;
    [MarshalAs(UnmanagedType.I1)] public bool op_offload;
    [MarshalAs(UnmanagedType.I1)] public bool swa_full;
    [MarshalAs(UnmanagedType.I1)] public bool kv_unified;
    public IntPtr samplers;
    public nint n_samplers;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaBatch
{
    public int n_tokens;
    public IntPtr token;
    public IntPtr embd;
    public IntPtr pos;
    public IntPtr n_seq_id;
    public IntPtr seq_id;
    public IntPtr logits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaSamplerChainParams
{
    [MarshalAs(UnmanagedType.I1)] public bool no_perf;
}
