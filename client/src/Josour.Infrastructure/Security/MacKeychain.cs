using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Josour.Infrastructure.Security;

/// <summary>Raised when the Keychain refuses an operation; <see cref="Status"/> is the OSStatus.</summary>
public sealed class MacKeychainException : Exception
{
    public MacKeychainException(string message, int status)
        : base($"{message} (OSStatus {status})") => Status = status;

    public int Status { get; }
}

/// <summary>
/// The macOS Keychain, as much of it as Josour needs: read, write and delete one generic password per key.
/// <para>
/// This is the macOS answer to what DPAPI does on Windows (<see cref="DpapiSecretStore"/>), and it is deliberately the
/// real API rather than the <c>security</c> command-line tool. Shelling out would put the refresh token on a command
/// line, where it is visible to every process that can read the process table for as long as the call takes.
/// </para>
/// <para>
/// Items are stored with <c>kSecAttrAccessibleAfterFirstUnlock</c>: readable once the user has logged in after a boot,
/// including while the screen is locked, which is what a background agent that reconnects on its own needs. They are
/// not synchronised to iCloud — a device secret identifies <em>this</em> device and must not follow the user to another.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacKeychain
{
    private const string CoreFoundationPath =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const string SecurityPath =
        "/System/Library/Frameworks/Security.framework/Security";

    internal const int ErrSecSuccess = 0;
    internal const int ErrSecItemNotFound = -25300;
    internal const int ErrSecDuplicateItem = -25299;

    // ---------------------------------------------------------------- native

    [DllImport(CoreFoundationPath)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundationPath, CharSet = CharSet.Unicode)]
    private static extern IntPtr CFStringCreateWithCharacters(IntPtr alloc, string chars, nint numChars);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, nint length);

    [DllImport(CoreFoundationPath)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(SecurityPath)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityPath)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityPath)]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport(SecurityPath)]
    private static extern int SecItemDelete(IntPtr query);

    // ---------------------------------------------------------------- framework constants

    private static readonly IntPtr CoreFoundationHandle = NativeLibrary.Load(CoreFoundationPath);
    private static readonly IntPtr SecurityHandle = NativeLibrary.Load(SecurityPath);

    // Pointers to the callback structs, passed to CFDictionaryCreate as-is (it wants the address, not a value).
    private static readonly IntPtr TypeDictionaryKeyCallBacks =
        NativeLibrary.GetExport(CoreFoundationHandle, "kCFTypeDictionaryKeyCallBacks");

    private static readonly IntPtr TypeDictionaryValueCallBacks =
        NativeLibrary.GetExport(CoreFoundationHandle, "kCFTypeDictionaryValueCallBacks");

    // These exports are *variables* holding CFStringRef, so the value has to be read through the address.
    private static readonly IntPtr SecClass = ReadSecurityConstant("kSecClass");
    private static readonly IntPtr SecClassGenericPassword = ReadSecurityConstant("kSecClassGenericPassword");
    private static readonly IntPtr SecAttrService = ReadSecurityConstant("kSecAttrService");
    private static readonly IntPtr SecAttrAccount = ReadSecurityConstant("kSecAttrAccount");
    private static readonly IntPtr SecAttrAccessible = ReadSecurityConstant("kSecAttrAccessible");
    private static readonly IntPtr SecAttrAccessibleAfterFirstUnlock = ReadSecurityConstant("kSecAttrAccessibleAfterFirstUnlock");
    private static readonly IntPtr SecValueData = ReadSecurityConstant("kSecValueData");
    private static readonly IntPtr SecReturnData = ReadSecurityConstant("kSecReturnData");
    private static readonly IntPtr SecMatchLimit = ReadSecurityConstant("kSecMatchLimit");
    private static readonly IntPtr SecMatchLimitOne = ReadSecurityConstant("kSecMatchLimitOne");
    private static readonly IntPtr CFBooleanTrue = Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundationHandle, "kCFBooleanTrue"));

    private static IntPtr ReadSecurityConstant(string name) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle, name));

    // ---------------------------------------------------------------- operations

    /// <summary>The stored value, or null when the Keychain has no item for this service and account.</summary>
    public static string? Find(string service, string account)
    {
        var query = new CfDictionary();
        try
        {
            query.Add(SecClass, SecClassGenericPassword);
            query.Add(SecAttrService, query.Track(CfString(service)));
            query.Add(SecAttrAccount, query.Track(CfString(account)));
            query.Add(SecReturnData, CFBooleanTrue);
            query.Add(SecMatchLimit, SecMatchLimitOne);

            var status = SecItemCopyMatching(query.Build(), out var result);
            if (status == ErrSecItemNotFound)
            {
                return null;
            }

            Check(status, "Could not read the item");
            try
            {
                return ReadUtf8(result);
            }
            finally
            {
                if (result != IntPtr.Zero)
                {
                    CFRelease(result);
                }
            }
        }
        finally
        {
            query.Dispose();
        }
    }

    /// <summary>Adds the item, or replaces the value of the one that is already there.</summary>
    public static void Save(string service, string account, string value)
    {
        var attributes = new CfDictionary();
        try
        {
            var data = attributes.Track(CfData(Encoding.UTF8.GetBytes(value)));
            attributes.Add(SecClass, SecClassGenericPassword);
            attributes.Add(SecAttrService, attributes.Track(CfString(service)));
            attributes.Add(SecAttrAccount, attributes.Track(CfString(account)));
            attributes.Add(SecAttrAccessible, SecAttrAccessibleAfterFirstUnlock);
            attributes.Add(SecValueData, data);

            var status = SecItemAdd(attributes.Build(), IntPtr.Zero);
            if (status != ErrSecDuplicateItem)
            {
                Check(status, "Could not store the item");
                return;
            }
        }
        finally
        {
            attributes.Dispose();
        }

        Update(service, account, value);
    }

    private static void Update(string service, string account, string value)
    {
        var query = new CfDictionary();
        var update = new CfDictionary();
        try
        {
            query.Add(SecClass, SecClassGenericPassword);
            query.Add(SecAttrService, query.Track(CfString(service)));
            query.Add(SecAttrAccount, query.Track(CfString(account)));
            update.Add(SecValueData, update.Track(CfData(Encoding.UTF8.GetBytes(value))));

            Check(SecItemUpdate(query.Build(), update.Build()), "Could not update the item");
        }
        finally
        {
            query.Dispose();
            update.Dispose();
        }
    }

    /// <summary>Deletes the item. Deleting one that is not there is success, not an error.</summary>
    public static void Delete(string service, string account)
    {
        var query = new CfDictionary();
        try
        {
            query.Add(SecClass, SecClassGenericPassword);
            query.Add(SecAttrService, query.Track(CfString(service)));
            query.Add(SecAttrAccount, query.Track(CfString(account)));

            var status = SecItemDelete(query.Build());
            if (status != ErrSecItemNotFound)
            {
                Check(status, "Could not delete the item");
            }
        }
        finally
        {
            query.Dispose();
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void Check(int status, string what)
    {
        if (status != ErrSecSuccess)
        {
            throw new MacKeychainException(what, status);
        }
    }

    private static IntPtr CfString(string value)
    {
        var handle = CFStringCreateWithCharacters(IntPtr.Zero, value, value.Length);
        return handle != IntPtr.Zero ? handle : throw new MacKeychainException("Could not create a CFString", 0);
    }

    private static IntPtr CfData(byte[] bytes)
    {
        var handle = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        return handle != IntPtr.Zero ? handle : throw new MacKeychainException("Could not create a CFData", 0);
    }

    private static string ReadUtf8(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = (int)CFDataGetLength(data);
        if (length <= 0)
        {
            return string.Empty;
        }

        var bytes = new byte[length];
        Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            // The secret is not left lying in a managed array waiting for a collection that may never come.
            Array.Clear(bytes);
        }
    }

    /// <summary>
    /// Builds one CFDictionary and owns every CoreFoundation handle that went into it. Without this the interop above
    /// is a list of paired create/release calls that an early return quietly turns into a leak.
    /// </summary>
    private sealed class CfDictionary : IDisposable
    {
        private readonly List<IntPtr> _keys = new();
        private readonly List<IntPtr> _values = new();
        private readonly List<IntPtr> _owned = new();
        private IntPtr _dictionary;

        /// <summary>Takes ownership of a handle this dictionary created, so Dispose releases it.</summary>
        public IntPtr Track(IntPtr handle)
        {
            _owned.Add(handle);
            return handle;
        }

        public void Add(IntPtr key, IntPtr value)
        {
            _keys.Add(key);
            _values.Add(value);
        }

        public IntPtr Build()
        {
            if (_dictionary == IntPtr.Zero)
            {
                _dictionary = CFDictionaryCreate(
                    IntPtr.Zero,
                    _keys.ToArray(),
                    _values.ToArray(),
                    _keys.Count,
                    TypeDictionaryKeyCallBacks,
                    TypeDictionaryValueCallBacks);

                if (_dictionary == IntPtr.Zero)
                {
                    throw new MacKeychainException("Could not create the query dictionary", 0);
                }
            }

            return _dictionary;
        }

        public void Dispose()
        {
            if (_dictionary != IntPtr.Zero)
            {
                CFRelease(_dictionary);
                _dictionary = IntPtr.Zero;
            }

            foreach (var handle in _owned)
            {
                if (handle != IntPtr.Zero)
                {
                    CFRelease(handle);
                }
            }

            _owned.Clear();
        }
    }
}
