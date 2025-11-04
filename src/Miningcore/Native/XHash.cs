using System.Runtime.InteropServices;

// ReSharper disable InconsistentNaming

namespace Miningcore.Native;

public static unsafe class XHash
{
    [StructLayout(LayoutKind.Sequential)]
    public struct xhash_hash256
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct xhash_result
    {
        public xhash_hash256 final_hash;
        public xhash_hash256 mix_hash;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct xhash_epoch_context
    {
        public int epoch_number;
        public int light_cache_num_items;
        public IntPtr light_cache; // const union xhash_hash512*
        public int full_dataset_num_items;
    }

    /// <summary>
    /// Allocate and initialize a new XHash epoch context (light cache only)
    /// </summary>
    /// <param name="epoch_number">The epoch number (block_number / 720)</param>
    /// <returns>Newly allocated epoch context or NULL</returns>
    [DllImport("libxhash", EntryPoint = "xhash_create_epoch_context", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr xhash_create_epoch_context(int epoch_number);

    /// <summary>
    /// Frees a previously allocated XHash epoch context
    /// </summary>
    /// <param name="context">The context to free</param>
    [DllImport("libxhash", EntryPoint = "xhash_destroy_epoch_context", CallingConvention = CallingConvention.Cdecl)]
    public static extern void xhash_destroy_epoch_context(IntPtr context);

    /// <summary>
    /// Calculate XHash result for a header hash and nonce
    /// </summary>
    /// <param name="context">The epoch context</param>
    /// <param name="header_hash">The 32-Byte header hash</param>
    /// <param name="nonce">The nonce to pack into the mix</param>
    /// <returns>XHash result containing final_hash and mix_hash</returns>
    [DllImport("libxhash", EntryPoint = "xhash_hash", CallingConvention = CallingConvention.Cdecl)]
    public static extern xhash_result xhash_hash(IntPtr context, ref xhash_hash256 header_hash, ulong nonce);

    /// <summary>
    /// Verify XHash validity against difficulty
    /// </summary>
    /// <param name="context">The epoch context</param>
    /// <param name="header_hash">The header hash</param>
    /// <param name="mix_hash">The mix hash</param>
    /// <param name="nonce">The nonce</param>
    /// <param name="difficulty">The difficulty as big-endian 256-bit value</param>
    /// <returns>0 if valid, 1 if invalid final hash, 2 if invalid mix hash</returns>
    [DllImport("libxhash", EntryPoint = "xhash_verify_against_difficulty", CallingConvention = CallingConvention.Cdecl)]
    public static extern int xhash_verify_against_difficulty(IntPtr context, ref xhash_hash256 header_hash, ref xhash_hash256 mix_hash, ulong nonce, ref xhash_hash256 difficulty);

    /// <summary>
    /// Calculate the epoch seed hash
    /// </summary>
    /// <param name="epoch_number">The epoch number</param>
    /// <returns>The epoch seed hash</returns>
    [DllImport("libxhash", EntryPoint = "xhash_calculate_epoch_seed", CallingConvention = CallingConvention.Cdecl)]
    public static extern xhash_hash256 xhash_calculate_epoch_seed(int epoch_number);
}

