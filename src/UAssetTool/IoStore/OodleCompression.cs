using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UAssetTool.IoStore;

/// <summary>
/// Oodle compression compressor types.
/// Reference: oodle_loader/src/lib.rs
/// </summary>
public enum OodleCompressor : int
{
    None = 3,
    Kraken = 8,
    Leviathan = 13,
    Mermaid = 9,
    Selkie = 11,
    Hydra = 12,
}

/// <summary>
/// Oodle compression levels.
/// Reference: oodle_loader/src/lib.rs
/// </summary>
public enum OodleCompressionLevel : int
{
    None = 0,
    SuperFast = 1,
    VeryFast = 2,
    Fast = 3,
    Normal = 4,
    Optimal1 = 5,
    Optimal2 = 6,
    Optimal3 = 7,
    Optimal4 = 8,
    Optimal5 = 9,
    HyperFast1 = -1,
    HyperFast2 = -2,
    HyperFast3 = -3,
    HyperFast4 = -4,
}

/// <summary>
/// Oodle compression wrapper using P/Invoke.
/// Reference: oodle_loader/src/lib.rs
/// </summary>
public static class OodleCompression
{
    private const string OODLE_DLL_NAME = "oo2core_9_win64"; // logical name, resolved per-platform
    private const string OODLE_DOWNLOAD_BASE_URL = "https://github.com/new-world-tools/go-oodle/releases/download/v0.2.3-files/";
    private const long OODLE_MIN_VALID_SIZE = 500_000; // 500KB minimum

    private static volatile bool _initialized = false;
    private static volatile bool _available = false;
    private static bool _resolverRegistered = false;
    private static readonly object _initLock = new object();
    // Where the validated library was found or downloaded to; the resolver loads from here.
    private static string? _libraryPath;

    /// <summary>
    /// Get the platform-specific Oodle library filename.
    /// </summary>
    private static string GetPlatformLibraryName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "oo2core_9_win64.dll";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "liboo2corelinux64.so.9";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "liboo2coremac64.9.dylib";
        return "oo2core_9_win64.dll";
    }

    /// <summary>
    /// Get the download URL for this platform's Oodle library, or null if none is hosted.
    /// </summary>
    private static string? GetDownloadUrl()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return OODLE_DOWNLOAD_BASE_URL + GetPlatformLibraryName();
        return null;
    }

    /// <summary>
    /// Directories searched for the Oodle library, in order. Beside the executable first; the
    /// per-user data directory is the fallback for installs where that location is read-only
    /// (AppImage, /usr/bin).
    /// </summary>
    private static IEnumerable<string> GetLibraryDirectories()
    {
        yield return AppContext.BaseDirectory;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "UAssetTool");
    }

    /// <summary>
    /// Register a DllImport resolver so the runtime finds the correct platform library.
    /// </summary>
    private static void RegisterResolver()
    {
        if (_resolverRegistered) return;
        _resolverRegistered = true;

        NativeLibrary.SetDllImportResolver(typeof(OodleCompression).Assembly, (libraryName, assembly, searchPath) =>
        {
            if (libraryName == OODLE_DLL_NAME || libraryName == "oo2core_9_win64.dll")
            {
                string platformLib = GetPlatformLibraryName();
                // Try the location Initialize settled on, else beside the executable
                string fullPath = _libraryPath ?? Path.Combine(AppContext.BaseDirectory, platformLib);
                if (NativeLibrary.TryLoad(fullPath, out IntPtr handle))
                    return handle;
                // Try system paths
                if (NativeLibrary.TryLoad(platformLib, assembly, searchPath, out handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    [DllImport(OODLE_DLL_NAME, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr OodleLZ_Compress(
        OodleCompressor compressor,
        IntPtr rawBuf,
        IntPtr rawLen,
        IntPtr compBuf,
        OodleCompressionLevel level,
        IntPtr pOptions,
        IntPtr dictionaryBase,
        IntPtr lrm,
        IntPtr scratchMem,
        IntPtr scratchSize);

    [DllImport(OODLE_DLL_NAME, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr OodleLZ_Decompress(
        IntPtr compBuf,
        IntPtr compBufSize,
        IntPtr rawBuf,
        IntPtr rawLen,
        int fuzzSafe,
        int checkCRC,
        int verbosity,
        IntPtr decBufBase,
        IntPtr decBufSize,
        IntPtr fpCallback,
        IntPtr callbackUserData,
        IntPtr decoderMemory,
        IntPtr decoderMemorySize,
        int threadPhase);

    [DllImport(OODLE_DLL_NAME, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr OodleLZ_GetCompressedBufferSizeNeeded(
        OodleCompressor compressor,
        IntPtr rawSize);

    /// <summary>
    /// Check if Oodle is available.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (!_initialized)
                Initialize();
            return _available;
        }
    }

    /// <summary>
    /// Initialize Oodle - download library if needed (Windows and Linux).
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;

        // Serialize initialization. Without this lock, parallel callers (e.g. Parallel.ForEach
        // extraction with many threads) race: the old code set _initialized=true BEFORE the
        // library finished loading, so concurrent IsAvailable checks saw "initialized but not
        // available" and their Decompress calls silently returned null — making extraction flaky
        // (only the racing thread's chunks decompressed, a different subset each run). We publish
        // _initialized LAST (in finally) so other threads either wait here or read the final state.
        lock (_initLock)
        {
            if (_initialized)
                return;

            try
            {
                RegisterResolver();

                string libName = GetPlatformLibraryName();

                // Check if library exists and is valid
                string? libPath = GetLibraryDirectories()
                    .Select(dir => Path.Combine(dir, libName))
                    .FirstOrDefault(IsOodleValid);

                if (libPath == null)
                {
                    string? downloadUrl = GetDownloadUrl();
                    if (downloadUrl != null)
                    {
                        Console.Error.WriteLine($"[Oodle] Downloading Oodle library...");
                        libPath = DownloadOodle(downloadUrl, libName);
                    }
                    else
                    {
                        Console.Error.WriteLine($"[Oodle] Oodle library not found: {libName}");
                        Console.Error.WriteLine($"[Oodle] Place the Oodle library beside the executable to enable Oodle compression.");
                        return;
                    }
                }

                // Try to load and test
                if (libPath != null)
                {
                    _libraryPath = libPath;

                    // Test by calling GetCompressedBufferSizeNeeded
                    var size = OodleLZ_GetCompressedBufferSizeNeeded(OodleCompressor.Kraken, (IntPtr)1024);
                    if (size.ToInt64() > 0)
                    {
                        _available = true;
                        Console.Error.WriteLine($"[Oodle] Initialized successfully");
                    }
                }
            }
            catch (DllNotFoundException)
            {
                Console.Error.WriteLine($"[Oodle] Native library not found for this platform ({RuntimeInformation.OSDescription}).");
                Console.Error.WriteLine($"[Oodle] Expected: {GetPlatformLibraryName()}");
                _available = false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Oodle] Failed to initialize: {ex.Message}");
                _available = false;
            }
            finally
            {
                // Publish completion LAST, even on early return/exception, so we never re-init
                // and concurrent callers observe a consistent final state.
                _initialized = true;
            }
        }
    }

    private static bool IsOodleValid(string path)
    {
        if (!File.Exists(path))
            return false;

        var info = new FileInfo(path);
        if (info.Length < OODLE_MIN_VALID_SIZE)
        {
            Console.Error.WriteLine($"[Oodle] DLL at {path} is too small ({info.Length} bytes), likely corrupted");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Download the Oodle library into the first writable library directory.
    /// Returns the path it was written to, or null on failure.
    /// </summary>
    private static string? DownloadOodle(string url, string libName)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            
            var response = client.GetAsync(url).Result;
            response.EnsureSuccessStatusCode();
            
            var bytes = response.Content.ReadAsByteArrayAsync().Result;
            
            if (bytes.Length < OODLE_MIN_VALID_SIZE)
                throw new Exception($"Downloaded file is too small ({bytes.Length} bytes)");

            foreach (string dir in GetLibraryDirectories())
            {
                string targetPath = Path.Combine(dir, libName);
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(targetPath, bytes);
                    Console.Error.WriteLine($"[Oodle] Downloaded successfully ({bytes.Length} bytes) to {targetPath}");
                    return targetPath;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    Console.Error.WriteLine($"[Oodle] Cannot write to {dir}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Oodle] Download failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Compress data using Oodle.
    /// </summary>
    public static byte[]? Compress(
        byte[] input,
        OodleCompressor compressor = OodleCompressor.Kraken,
        OodleCompressionLevel level = OodleCompressionLevel.Normal)
    {
        if (!IsAvailable)
            return null;

        try
        {
            // Get required buffer size
            var bufferSize = OodleLZ_GetCompressedBufferSizeNeeded(compressor, (IntPtr)input.Length);
            if (bufferSize.ToInt64() <= 0)
                return null;

            byte[] output = new byte[bufferSize.ToInt64()];

            // Pin arrays for P/Invoke
            var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);

            try
            {
                var compressedSize = OodleLZ_Compress(
                    compressor,
                    inputHandle.AddrOfPinnedObject(),
                    (IntPtr)input.Length,
                    outputHandle.AddrOfPinnedObject(),
                    level,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (compressedSize.ToInt64() <= 0)
                    return null;

                // Truncate to actual size
                Array.Resize(ref output, (int)compressedSize.ToInt64());
                return output;
            }
            finally
            {
                inputHandle.Free();
                outputHandle.Free();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Oodle] Compression failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Decompress data using Oodle.
    /// </summary>
    public static byte[]? Decompress(byte[] input, int uncompressedSize)
    {
        if (!IsAvailable)
            return null;

        try
        {
            byte[] output = new byte[uncompressedSize];

            var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);

            try
            {
                var decompressedSize = OodleLZ_Decompress(
                    inputHandle.AddrOfPinnedObject(),
                    (IntPtr)input.Length,
                    outputHandle.AddrOfPinnedObject(),
                    (IntPtr)uncompressedSize,
                    1, // fuzzSafe
                    0, // checkCRC - disable CRC check
                    0, // verbosity
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0); // threadPhase

                long actualSize = decompressedSize.ToInt64();
                if (actualSize <= 0)
                {
                    Console.Error.WriteLine($"[Oodle] Decompression returned {actualSize}, expected {uncompressedSize}");
                    return null;
                }
                
                // If size matches, return as-is
                if (actualSize == uncompressedSize)
                    return output;
                
                // If size is different but positive, resize and return
                if (actualSize > 0 && actualSize <= uncompressedSize)
                {
                    Array.Resize(ref output, (int)actualSize);
                    return output;
                }

                Console.Error.WriteLine($"[Oodle] Size mismatch: got {actualSize}, expected {uncompressedSize}");
                return null;
            }
            finally
            {
                inputHandle.Free();
                outputHandle.Free();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Oodle] Decompression failed: {ex.Message}");
            return null;
        }
    }
}
