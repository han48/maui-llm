#if !(IOS || ANDROID || MACCATALYST)
using LLama.Native;
#endif
#if MACCATALYST
using Foundation;
using System.Runtime.InteropServices;
#endif
using Microsoft.Extensions.Logging;
using AIAgentLocal.ViewModels;

namespace AIAgentLocal;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Apply saved language preference early, before any UI loads
        Services.L.Init();
#if !(IOS || ANDROID || MACCATALYST)
        // Configure LLamaSharp native library loading (Windows only)
        NativeLibraryConfig.All.WithAutoFallback();
#endif
#if MACCATALYST
        // On macOS Catalyst, preload native libs from app bundle Resources
        try
        {
            var resourcePath = NSBundle.MainBundle.ResourcePath;
            var llamaLibPath = Path.Combine(resourcePath, "libllama.dylib");
            
            Console.WriteLine($"[AIAgentLocal] ResourcePath: {resourcePath}");
            
            // Load dependencies in order (load .0.dylib first as they are the soname targets)
            foreach (var lib in new[] { 
                "libggml-base.0.dylib", "libggml-base.dylib",
                "libggml.0.dylib", "libggml.dylib",
                "libggml-cpu.0.dylib", "libggml-cpu.dylib",
                "libggml-metal.0.dylib", "libggml-metal.dylib",
                "libggml-blas.0.dylib", "libggml-blas.dylib",
                "libggml-rpc.0.dylib", "libggml-rpc.dylib",
                "libllama.0.dylib", "libllama.dylib",
                "libmtmd.dylib" })
            {
                var p = Path.Combine(resourcePath, lib);
                if (File.Exists(p))
                {
                    try 
                    { 
                        NativeLibrary.Load(p);
                        Console.WriteLine($"[AIAgentLocal] Loaded: {lib}");
                    }
                    catch (Exception ex) 
                    { 
                        Console.WriteLine($"[AIAgentLocal] FAILED to load {lib}: {ex.Message}");
                    }
                }
            }
            
            // Set resolver so DllImport("llama") and DllImport("mtmd") find the preloaded libraries
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(AIAgentLocal.Native.LlamaCpp).Assembly, (name, assembly, searchPath) =>
                {
                    if (name == "llama" && File.Exists(llamaLibPath))
                    {
                        NativeLibrary.TryLoad(llamaLibPath, out var h);
                        return h;
                    }
                    if (name == "mtmd")
                    {
                        var mtmdPath = Path.Combine(resourcePath, "libmtmd.dylib");
                        if (File.Exists(mtmdPath))
                        {
                            NativeLibrary.TryLoad(mtmdPath, out var h);
                            return h;
                        }
                    }
                    return IntPtr.Zero;
                });
            }
            catch (InvalidOperationException)
            {
                // Resolver already set - ignore
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AIAgentLocal] macOS native lib init failed: {ex.Message}");
        }
#endif

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
            });

#if ANDROID
        // Disable MAUI's automatic keyboard scroll which pushes entire chat up
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoKeyboardScroll", (handler, view) =>
        {
            handler.PlatformView.SetBackgroundColor(Android.Graphics.Color.Transparent);
        });
#endif

        // Register services
        builder.Services.AddTransient<ChatViewModel>();
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
