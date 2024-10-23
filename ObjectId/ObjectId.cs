using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

namespace System;

[StructLayout(LayoutKind.Explicit, Size = 12)]
[DebuggerDisplay("{ToString(),nq}")]
public readonly partial struct ObjectId : IComparable<ObjectId>, IEquatable<ObjectId>
#if NET7_0_OR_GREATER
, Numerics.IMinMaxValue<ObjectId>
#endif
{
    #region Fields

    //DateTime.UnixEpoch
    private static readonly DateTime _unixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long _unixEpochTicks = _unixEpoch.Ticks;

    private static readonly short _pid = GetPid();
    private static readonly int _pid24 = _pid << 24;
    private static readonly int _machinePid = (GetMachineXXHash() << 8) | ((_pid >> 8) & 0xff);
    private static readonly int _machinePidReverse = BinaryPrimitives.ReverseEndianness((GetMachineXXHash() << 8) | ((_pid >> 8) & 0xff));
    private static readonly long _random = CalculateRandomValue();
    private static readonly int _random24 = (int)(_random << 24);
    private static readonly int _random8Reverse = BinaryPrimitives.ReverseEndianness((int)(_random >> 8));
    internal static int _staticIncrement = new Random().Next();

    /// <summary>
    /// First 3 bytes of machine name hash
    /// </summary>
    public static readonly int MachineHash24 = GetMachineXXHash();
    public static readonly ObjectId Empty = default;
    public static readonly ObjectId Min = new(0, 0, 0);
    public static readonly ObjectId Max = new(-1, -1, -1);

    [FieldOffset(0)] private readonly byte _timestamp0;
    [FieldOffset(1)] private readonly byte _timestamp1;
    [FieldOffset(2)] private readonly byte _timestamp2;
    [FieldOffset(3)] private readonly byte _timestamp3;

    [FieldOffset(4)] private readonly byte _machine0;
    [FieldOffset(5)] private readonly byte _machine1;
    [FieldOffset(6)] private readonly byte _machine2;

    [FieldOffset(7)] private readonly byte _pid0;
    [FieldOffset(8)] private readonly byte _pid1;

    [FieldOffset(9)] private readonly byte _increment0;
    [FieldOffset(10)] private readonly byte _increment1;
    [FieldOffset(11)] private readonly byte _increment2;

    #endregion Fields

    #region Ctors

    public ObjectId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 12) ThrowArgumentException();

        this = Unsafe.ReadUnaligned<ObjectId>(ref MemoryMarshal.GetReference(bytes));

        //https://github.com/dotnet/runtime/pull/78446
        [StackTraceHidden]
        static void ThrowArgumentException() => throw new ArgumentException("The byte array must be 12 bytes long.", nameof(bytes));
    }

    public ObjectId(DateTime timestamp, int machine, short pid, int increment)
        : this(GetTimestampFromDateTime(timestamp), machine, pid, increment)
    {
    }

    public ObjectId(int timestamp, int machine, short pid, int increment)
    {
        if ((machine & 0xff000000) != 0) throw new ArgumentOutOfRangeException(nameof(machine), "The machine value must be between 0 and 16777215 (it must fit in 3 bytes).");

        if ((increment & 0xff000000) != 0) throw new ArgumentOutOfRangeException(nameof(increment), "The increment value must be between 0 and 16777215 (it must fit in 3 bytes).");

        Unsafe.WriteUnaligned(ref _timestamp0, BinaryPrimitives.ReverseEndianness(timestamp));

        _machine0 = (byte)(machine >> 16);
        _machine1 = (byte)(machine >> 8);
        _machine2 = (byte)machine;

        _pid0 = (byte)(pid >> 8);
        _pid1 = (byte)pid;

        _increment0 = (byte)(increment >> 16);
        _increment1 = (byte)(increment >> 8);
        _increment2 = (byte)increment;
    }

    public ObjectId(int timestamp, int machinePid, int pidIncrement)
    {
        Unsafe.WriteUnaligned(ref _timestamp0, BinaryPrimitives.ReverseEndianness(timestamp));
        Unsafe.WriteUnaligned(ref _machine0, BinaryPrimitives.ReverseEndianness(machinePid));
        Unsafe.WriteUnaligned(ref _pid1, BinaryPrimitives.ReverseEndianness(pidIncrement));
    }

    #endregion Ctors

    #region Props

    public int Timestamp => BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef(in _timestamp0)));

    public int Machine => (BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef(in _machine0))) >> 8) & 0xffffff;

    public short Pid => BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<short>(ref Unsafe.AsRef(in _pid0)));

    public int Increment => (BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef(in _increment0))) >> 8) & 0xffffff;

    public DateTime Created => _unixEpoch.AddSeconds((uint)Timestamp);

#if NET7_0_OR_GREATER

    static ObjectId Numerics.IMinMaxValue<ObjectId>.MaxValue => Max;

    static ObjectId Numerics.IMinMaxValue<ObjectId>.MinValue => Min;

#endif

    #endregion Props

    #region Operators

    public static bool operator <(ObjectId left, ObjectId right) => left.CompareTo(right) < 0;

    public static bool operator <=(ObjectId left, ObjectId right) => left.CompareTo(right) <= 0;

    public static bool operator ==(ObjectId left, ObjectId right) => EqualsCore(in left, in right);

    public static bool operator !=(ObjectId left, ObjectId right) => !EqualsCore(in left, in right);

    public static bool operator >=(ObjectId left, ObjectId right) => left.CompareTo(right) >= 0;

    public static bool operator >(ObjectId left, ObjectId right) => left.CompareTo(right) > 0;

    #endregion Operators

    #region Public Methods

    #region New

    //https://github.com/dotnet/runtime/blob/e66a6a319cc372f30c3dad9f491ac636c0ce03e4/src/libraries/Common/src/Interop/Unix/System.Native/Interop.GetSystemTimeAsTicks.cs#L12

    //[DllImport("libSystem.Native", EntryPoint = "SystemNative_GetSystemTimeAsTicks")]
    //internal static extern long GetSystemTimeAsTicks();

    //[LibraryImport("libSystem.Native", EntryPoint = "SystemNative_GetSystemTimeAsTicks")]
    //internal static partial long GetSystemTimeAsTicks();

    public static ObjectId New()
    {
        ObjectId id = default;

        ref var b = ref Unsafe.As<ObjectId, byte>(ref id);

        Unsafe.WriteUnaligned(ref b, BinaryPrimitives.ReverseEndianness((int)(uint)(long)Math.Floor((double)(DateTime.UtcNow.Ticks - _unixEpochTicks) / TimeSpan.TicksPerSecond)));

        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, 4), _machinePidReverse);

        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, 8), BinaryPrimitives.ReverseEndianness(_pid24 | (Interlocked.Increment(ref _staticIncrement) & 0x00ffffff)));

        return id;
    }

    public static ObjectId New(DateTime timestamp) => New(GetTimestampFromDateTime(timestamp));

    public static ObjectId New(Int32 timestamp)
    {
        // only use low order 3 bytes
        int increment = Interlocked.Increment(ref _staticIncrement) & 0x00ffffff;

        var pidIncrement = (_pid << 24) | increment;

        return new ObjectId(timestamp, _machinePid, pidIncrement);
    }

    #endregion New

    #region NewObjectId

    /// <summary>
    /// https://www.mongodb.com/docs/manual/reference/method/ObjectId/
    /// </summary>
    public static ObjectId NewObjectId()
    {
        ObjectId id = default;

        ref var b = ref Unsafe.As<ObjectId, byte>(ref id);

        Unsafe.WriteUnaligned(ref b, BinaryPrimitives.ReverseEndianness((int)(uint)(long)Math.Floor((double)(DateTime.UtcNow.Ticks - _unixEpochTicks) / TimeSpan.TicksPerSecond)));

        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, 4), _random8Reverse);

        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, 8), BinaryPrimitives.ReverseEndianness(_random24 | (Interlocked.Increment(ref _staticIncrement) & 0x00ffffff)));

        return id;
    }

    internal static ObjectId NewObjectIdOld()
    {
        // only use low order 3 bytes
        int increment = Interlocked.Increment(ref _staticIncrement) & 0x00ffffff;

        var random = _random;

        var b = (int)(random >> 8); // first 4 bytes of random
        var c = (int)(random << 24) | increment; // 5th byte of random and 3 byte increment

        return new ObjectId(GetTimestamp(), b, c);
    }

    public static ObjectId NewObjectId(DateTime timestamp) => NewObjectId(GetTimestampFromDateTime(timestamp));

    public static ObjectId NewObjectId(Int32 timestamp)
    {
        // only use low order 3 bytes
        int increment = Interlocked.Increment(ref _staticIncrement) & 0x00ffffff;
        return Create(timestamp, _random, increment);
    }

    #endregion NewObjectId

    public byte[] ToByteArray()
    {
        var bytes = new byte[12];

        Unsafe.WriteUnaligned(ref bytes[0], this);

        return bytes;
    }

    public bool TryWrite(Span<byte> bytes)
    {
        if (bytes.Length < 12) return false;

        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(bytes), this);

        return true;
    }

    public int CompareTo(ObjectId id)
    {
        if (_timestamp0 != id._timestamp0) return _timestamp0 < id._timestamp0 ? -1 : 1;
        if (_timestamp1 != id._timestamp1) return _timestamp1 < id._timestamp1 ? -1 : 1;
        if (_timestamp2 != id._timestamp2) return _timestamp2 < id._timestamp2 ? -1 : 1;
        if (_timestamp3 != id._timestamp3) return _timestamp3 < id._timestamp3 ? -1 : 1;

        if (_machine0 != id._machine0) return _machine0 < id._machine0 ? -1 : 1;
        if (_machine1 != id._machine1) return _machine1 < id._machine1 ? -1 : 1;
        if (_machine2 != id._machine2) return _machine2 < id._machine2 ? -1 : 1;
        
        if (_pid0 != id._pid0) return _pid0 < id._pid0 ? -1 : 1;
        if (_pid1 != id._pid1) return _pid1 < id._pid1 ? -1 : 1;

        if (_increment0 != id._increment0) return _increment0 < id._increment0 ? -1 : 1;
        if (_increment1 != id._increment1) return _increment1 < id._increment1 ? -1 : 1;
        if (_increment2 != id._increment2) return _increment2 < id._increment2 ? -1 : 1;

        return 0;
    }

    public bool Equals(ObjectId id)
    {
        ref int l = ref Unsafe.As<byte, int>(ref Unsafe.AsRef(in _timestamp0));
        ref int r = ref Unsafe.As<byte, int>(ref Unsafe.AsRef(in id._timestamp0));

        return l == r && Unsafe.Add(ref l, 1) == Unsafe.Add(ref r, 1) && Unsafe.Add(ref l, 2) == Unsafe.Add(ref r, 2);
    }

    //internal Boolean Equals2(Id id)
    //{
    //    ref byte bl = ref Unsafe.AsRef(in _timestamp0);
    //    ref byte br = ref Unsafe.AsRef(in id._timestamp0);

    //    return Unsafe.As<byte, long>(ref bl) == Unsafe.As<byte, long>(ref br) &&
    //           Unsafe.As<byte, int>(ref Unsafe.Add(ref bl, 8)) == Unsafe.As<byte, int>(ref Unsafe.Add(ref br, 8));
    //}

    public override string ToString() => Convert.ToHexString(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _timestamp0), 12));

    public override bool Equals(object? obj) => obj is ObjectId id && EqualsCore(in this, in id);

    public override int GetHashCode()
    {
        ref int r = ref Unsafe.As<byte, int>(ref Unsafe.AsRef(in _timestamp0));
        return r ^ Unsafe.Add(ref r, 1) ^ Unsafe.Add(ref r, 2);
    }

    #endregion Public Methods

    #region Private Methods
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EqualsCore(in ObjectId left, in ObjectId right)
    {
        ref int l = ref Unsafe.As<byte, int>(ref Unsafe.AsRef(in left._timestamp0));
        ref int r = ref Unsafe.As<byte, int>(ref Unsafe.AsRef(in right._timestamp0));

        return l == r && Unsafe.Add(ref l, 1) == Unsafe.Add(ref r, 1) && Unsafe.Add(ref l, 2) == Unsafe.Add(ref r, 2);
    }

    private static long CalculateRandomValue()
    {
        var seed = (int)DateTime.UtcNow.Ticks ^ GetMachineHash() ^ GetPid();
        var random = new Random(seed);
        var high = random.Next();
        var low = random.Next();
        var combined = (long)((ulong)(uint)high << 32 | (ulong)(uint)low);
        return combined & 0xffffffffff; // low order 5 bytes
    }

    private static ObjectId Create(int timestamp, long random, int increment)
    {
        if (random < 0 || random > 0xffffffffff) throw new ArgumentOutOfRangeException(nameof(random), "The random value must be between 0 and 1099511627775 (it must fit in 5 bytes).");

        if (increment < 0 || increment > 0xffffff) throw new ArgumentOutOfRangeException(nameof(increment), "The increment value must be between 0 and 16777215 (it must fit in 3 bytes).");

        var b = (int)(random >> 8); // first 4 bytes of random
        var c = (int)(random << 24) | increment; // 5th byte of random and 3 byte increment
        return new ObjectId(timestamp, b, c);
    }

    /// <summary>
    /// Gets the current process id.  This method exists because of how CAS operates on the call stack, checking
    /// for permissions before executing the method.  Hence, if we inlined this call, the calling method would not execute
    /// before throwing an exception requiring the try/catch at an even higher level that we don't necessarily control.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int GetCurrentProcessId()
    {
#if NET6_0_OR_GREATER
        return Environment.ProcessId;
#else
        return Process.GetCurrentProcess().Id;
#endif
    }

    private static int GetMachineHash()
    {
        var machineName = Environment.MachineName;
        return 0x00ffffff & machineName.GetHashCode(); // use first 3 bytes of hash
    }

    private static int GetMachineXXHash() => XXHash24(Environment.MachineName);

    private static int XXHash24(string machineName)
    {
        var hash = HashCode.Combine(machineName);
        return 0x00ffffff & hash; // use first 3 bytes of hash
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetTimestamp()
    {
        var totalSeconds = (double)(DateTime.UtcNow.Ticks - _unixEpochTicks) / TimeSpan.TicksPerSecond;

        return (int)(uint)(long)Math.Floor(totalSeconds);
    }

    private static short GetPid()
    {
        try
        {
            return (short)GetCurrentProcessId(); // use low order two bytes only
        }
        catch (SecurityException)
        {
            return 0;
        }
    }

    private static int GetTimestampFromDateTime(DateTime timestamp)
    {
        var secondsSinceEpoch = (long)Math.Floor((ToUniversalTime(timestamp) - _unixEpoch).TotalSeconds);

        if (secondsSinceEpoch < uint.MinValue || secondsSinceEpoch > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(timestamp));

        return (int)(uint)secondsSinceEpoch;
    }

    private static DateTime ToUniversalTime(DateTime dateTime)
    {
        if (dateTime == DateTime.MinValue) return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

        if (dateTime == DateTime.MaxValue) return DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

        return dateTime.ToUniversalTime();
    }

    #endregion Private Methods
}