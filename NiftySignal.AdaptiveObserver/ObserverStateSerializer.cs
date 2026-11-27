using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace NiftySignal.AdaptiveObserver;

/// <summary>The stored state cannot be restored by this build (different format, a type whose fields changed, or an unsupported value).</summary>
public sealed class ObserverStateIncompatibleException(string message) : Exception(message);

/// <summary>
/// Writes and restores the Observer's in-memory state as an object graph: every instance field, private ones included, of every object reachable from the root, with shared references preserved. It works from the
/// runtime types themselves, so a state field added to any tracker is captured without changing this class. Each type's field list is stored with the data and must match exactly on restore. Only Observer, domain
/// and standard collection types are accepted; delegates, handles and custom collection comparers are refused (a database context or logger can never be captured by accident).
/// </summary>
public static class ObserverStateSerializer
{
    public const int FormatVersion = 1;
    const string AllowedPrefix = "NiftySignal.";

    enum Tag : byte { Null, Reference, Value }

    public static byte[] Serialize(object root)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);
            new Writer(writer).Write(root, typeof(object));
        }

        return stream.ToArray();
    }

    public static T Deserialize<T>(byte[] data) where T : class
    {
        using var reader = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
        var version = reader.ReadInt32();
        if (version != FormatVersion) throw new ObserverStateIncompatibleException($"State format {version} is not {FormatVersion}.");
        return new Reader(reader).Read(typeof(object)) as T ?? throw new ObserverStateIncompatibleException($"The stored state is not a {typeof(T).Name}.");
    }

    static readonly Dictionary<Type, FieldInfo[]> FieldCache = new();

    static FieldInfo[] Fields(Type type)
    {
        lock (FieldCache)
        {
            if (FieldCache.TryGetValue(type, out var cached)) return cached;
            var fields = new List<FieldInfo>();
            for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
                fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(f => f.Name, StringComparer.Ordinal));
            return FieldCache[type] = fields.ToArray();
        }
    }

    static string Signature(Type type) => string.Join(';', Fields(type).Select(f => $"{f.Name}:{f.FieldType}"));

    static bool IsScalar(Type t) => t.IsPrimitive && t != typeof(IntPtr) && t != typeof(UIntPtr) || t.IsEnum || t == typeof(string) || t == typeof(decimal)
        || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(TimeSpan) || t == typeof(Guid);

    static bool IsCollection(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() is var d
        && (d == typeof(List<>) || d == typeof(Dictionary<,>) || d == typeof(HashSet<>) || d == typeof(SortedSet<>) || d == typeof(SortedDictionary<,>) || d == typeof(Queue<>) || d == typeof(PriorityQueue<,>));

    /// <summary>The type actually stored for a value: compiler-made read-only wrappers and LINQ results behind a list interface are stored as arrays.</summary>
    static Type StoredType(object value, Type declared)
    {
        var type = value.GetType();
        if (type.IsArray || IsScalar(type) || IsCollection(type) || type.Assembly.GetName().Name!.StartsWith(AllowedPrefix, StringComparison.Ordinal) && value is not IEnumerable) return type;
        if (declared.IsInterface && value is IEnumerable)
        {
            var element = (declared.IsGenericType ? declared : type).GetGenericArguments().FirstOrDefault();
            if (element is not null && declared.IsAssignableFrom(element.MakeArrayType())) return element.MakeArrayType();
        }

        if (type.Assembly.GetName().Name!.StartsWith(AllowedPrefix, StringComparison.Ordinal) || type.IsValueType && (type.Namespace?.StartsWith("System", StringComparison.Ordinal) ?? false)) return type;
        throw new ObserverStateIncompatibleException($"State of type {type} is not supported.");
    }

    static void RequireDefaultComparer(object collection, Type type)
    {
        var property = type.GetProperty("Comparer") ?? type.GetProperty("UnorderedItems")?.DeclaringType?.GetProperty("Comparer");
        if (property?.GetValue(collection) is not { } comparer) return;
        var element = type.GetGenericArguments()[type.GetGenericTypeDefinition() == typeof(PriorityQueue<,>) ? 1 : 0];
        var generic = type.GetGenericTypeDefinition() == typeof(Dictionary<,>) || type.GetGenericTypeDefinition() == typeof(HashSet<>) ? typeof(EqualityComparer<>) : typeof(Comparer<>);
        var standard = generic.MakeGenericType(element).GetProperty("Default")!.GetValue(null);
        if (ReferenceEquals(comparer, standard) || element == typeof(string) && ReferenceEquals(comparer, StringComparer.Ordinal)) return;
        throw new ObserverStateIncompatibleException($"{type} uses a custom comparer, which cannot be restored.");
    }

    sealed class Writer(BinaryWriter w)
    {
        readonly Dictionary<object, int> _references = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<Type, int> _types = new();

        public void Write(object? value, Type declared)
        {
            if (value is null) { w.Write((byte)Tag.Null); return; }
            var tracked = !value.GetType().IsValueType && value is not string;
            if (tracked && _references.TryGetValue(value, out var existing)) { w.Write((byte)Tag.Reference); w.Write7BitEncodedInt(existing); return; }
            w.Write((byte)Tag.Value);
            var type = StoredType(value, declared);
            WriteType(type);
            if (tracked) _references.Add(value, _references.Count);
            if (IsScalar(type)) Scalar(value, type);
            else if (type.IsArray)
            {
                if (type.GetArrayRank() != 1) throw new ObserverStateIncompatibleException($"{type} is not a one-dimensional array.");
                Items((IEnumerable)value, type.GetElementType()!);
            }
            else if (IsCollection(type)) Collection(value, type);
            else
            {
                if (value is Delegate) throw new ObserverStateIncompatibleException($"A delegate ({type}) cannot be stored.");
                foreach (var field in Fields(type)) Write(field.GetValue(value), field.FieldType);
            }
        }

        void WriteType(Type type)
        {
            if (_types.TryGetValue(type, out var id)) { w.Write7BitEncodedInt(id); return; }
            id = _types.Count;
            _types.Add(type, id);
            w.Write7BitEncodedInt(id);
            w.Write(type.AssemblyQualifiedName!);
            w.Write(IsScalar(type) || type.IsArray || IsCollection(type) || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true ? "" : Signature(type));
        }

        void Items(IEnumerable items, Type element)
        {
            var list = items.Cast<object?>().ToList();
            w.Write7BitEncodedInt(list.Count);
            foreach (var item in list) Write(item, element);
        }

        void Collection(object value, Type type)
        {
            RequireDefaultComparer(value, type);
            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();
            if (definition == typeof(Dictionary<,>) || definition == typeof(SortedDictionary<,>))
            {
                var map = (IDictionary)value;
                w.Write7BitEncodedInt(map.Count);
                foreach (DictionaryEntry entry in map) { Write(entry.Key, arguments[0]); Write(entry.Value, arguments[1]); }
            }
            else if (definition == typeof(PriorityQueue<,>))
            {
                var items = ((IEnumerable)type.GetProperty("UnorderedItems")!.GetValue(value)!).Cast<object>().ToList();
                w.Write7BitEncodedInt(items.Count);
                foreach (ITuple item in items) { Write(item[0], arguments[0]); Write(item[1], arguments[1]); }
            }
            else Items((IEnumerable)value, arguments[0]);
        }

        void Scalar(object value, Type type)
        {
            if (type.IsEnum) { w.Write(Convert.ToInt64(value)); return; }
            switch (value)
            {
                case bool v: w.Write(v); break;
                case byte v: w.Write(v); break;
                case sbyte v: w.Write(v); break;
                case short v: w.Write(v); break;
                case ushort v: w.Write(v); break;
                case int v: w.Write(v); break;
                case uint v: w.Write(v); break;
                case long v: w.Write(v); break;
                case ulong v: w.Write(v); break;
                case float v: w.Write(v); break;
                case double v: w.Write(v); break;
                case decimal v: w.Write(v); break;
                case char v: w.Write(v); break;
                case string v: w.Write(v); break;
                case DateTime v: w.Write(v.ToBinary()); break;
                case DateTimeOffset v: w.Write(v.Ticks); w.Write(v.Offset.Ticks); break;
                case DateOnly v: w.Write(v.DayNumber); break;
                case TimeOnly v: w.Write(v.Ticks); break;
                case TimeSpan v: w.Write(v.Ticks); break;
                case Guid v: w.Write(v.ToByteArray()); break;
                default: throw new ObserverStateIncompatibleException($"Scalar {type} is not supported.");
            }
        }
    }

    sealed class Reader(BinaryReader r)
    {
        readonly List<object> _references = [];
        readonly List<Type> _types = [];

        public object? Read(Type declared)
        {
            var tag = (Tag)r.ReadByte();
            if (tag == Tag.Null) return null;
            if (tag == Tag.Reference) return _references[r.Read7BitEncodedInt()];
            var type = ReadType();
            if (IsScalar(type)) return Scalar(type);
            if (type.IsArray)
            {
                var element = type.GetElementType()!;
                var slot = Reserve(type);
                var count = r.Read7BitEncodedInt();
                var array = Array.CreateInstance(element, count);
                Set(slot, array);
                for (var i = 0; i < count; i++) array.SetValue(Read(element), i);
                return array;
            }

            if (IsCollection(type)) return Collection(type);
            var instance = RuntimeHelpers.GetUninitializedObject(type);
            if (!type.IsValueType) _references.Add(instance);
            foreach (var field in Fields(type)) field.SetValue(instance, Read(field.FieldType));
            return instance;
        }

        // A reference id is taken when the writer took it (before the children), and filled once the instance exists.
        int Reserve(Type type)
        {
            if (type.IsValueType) return -1;
            _references.Add(type);
            return _references.Count - 1;
        }

        void Set(int slot, object instance)
        {
            if (slot >= 0) _references[slot] = instance;
        }

        Type ReadType()
        {
            var id = r.Read7BitEncodedInt();
            if (id < _types.Count) return _types[id];
            var name = r.ReadString();
            var signature = r.ReadString();
            var type = Type.GetType(name, throwOnError: false) ?? throw new ObserverStateIncompatibleException($"Unknown state type {name}.");
            Require(type);
            if (signature.Length > 0 && signature != Signature(type)) throw new ObserverStateIncompatibleException($"The fields of {type} changed since the state was stored.");
            _types.Add(type);
            return type;
        }

        static void Require(Type type)
        {
            if (type.IsArray) { Require(type.GetElementType()!); return; }
            var assembly = type.Assembly.GetName().Name!;
            var standard = type.Assembly == typeof(object).Assembly && (IsScalar(type) || type.IsValueType) || IsCollection(type) || type == typeof(object);
            if (!standard && !assembly.StartsWith(AllowedPrefix, StringComparison.Ordinal)) throw new ObserverStateIncompatibleException($"State type {type} is not allowed.");
            foreach (var argument in type.GetGenericArguments()) Require(argument);
        }

        object Collection(Type type)
        {
            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();
            // Restored with the default comparer; string keys use ordinal comparison, which the writer verified is what the original did.
            var comparer = arguments[0] == typeof(string) && (definition == typeof(Dictionary<,>) || definition == typeof(HashSet<>)) ? StringComparer.Ordinal : null;
            var instance = comparer is null ? Activator.CreateInstance(type)! : Activator.CreateInstance(type, comparer)!;
            _references.Add(instance);
            var count = r.Read7BitEncodedInt();
            if (definition == typeof(Dictionary<,>) || definition == typeof(SortedDictionary<,>))
            {
                var map = (IDictionary)instance;
                for (var i = 0; i < count; i++) { var key = Read(arguments[0])!; map.Add(key, Read(arguments[1])); }
                return instance;
            }

            var add = definition == typeof(Queue<>) ? type.GetMethod("Enqueue")! : definition == typeof(PriorityQueue<,>) ? type.GetMethod("Enqueue")! : type.GetMethod("Add", [arguments[0]])!;
            for (var i = 0; i < count; i++)
                add.Invoke(instance, definition == typeof(PriorityQueue<,>) ? [Read(arguments[0]), Read(arguments[1])] : [Read(arguments[0])]);
            return instance;
        }

        object Scalar(Type type)
        {
            if (type.IsEnum) return Enum.ToObject(type, r.ReadInt64());
            if (type == typeof(bool)) return r.ReadBoolean();
            if (type == typeof(byte)) return r.ReadByte();
            if (type == typeof(sbyte)) return r.ReadSByte();
            if (type == typeof(short)) return r.ReadInt16();
            if (type == typeof(ushort)) return r.ReadUInt16();
            if (type == typeof(int)) return r.ReadInt32();
            if (type == typeof(uint)) return r.ReadUInt32();
            if (type == typeof(long)) return r.ReadInt64();
            if (type == typeof(ulong)) return r.ReadUInt64();
            if (type == typeof(float)) return r.ReadSingle();
            if (type == typeof(double)) return r.ReadDouble();
            if (type == typeof(decimal)) return r.ReadDecimal();
            if (type == typeof(char)) return r.ReadChar();
            if (type == typeof(string)) return r.ReadString();
            if (type == typeof(DateTime)) return DateTime.FromBinary(r.ReadInt64());
            if (type == typeof(DateTimeOffset)) { var ticks = r.ReadInt64(); return new DateTimeOffset(ticks, new TimeSpan(r.ReadInt64())); }
            if (type == typeof(DateOnly)) return DateOnly.FromDayNumber(r.ReadInt32());
            if (type == typeof(TimeOnly)) return new TimeOnly(r.ReadInt64());
            if (type == typeof(TimeSpan)) return new TimeSpan(r.ReadInt64());
            if (type == typeof(Guid)) return new Guid(r.ReadBytes(16));
            throw new ObserverStateIncompatibleException($"Scalar {type} is not supported.");
        }
    }
}
