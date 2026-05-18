using System.Diagnostics.CodeAnalysis;

namespace MyTelegram.Schema;

public static class SerializerObjectMappings
{
    private const uint VectorConstructorId = 0x1cb5c415;
    private static readonly ConcurrentDictionary<Type, Func<IObject>> GenericTypeOfTConstructors = new();
    private static readonly ConcurrentDictionary<uint, Type> TypeMappingDict = new();
    private static readonly ConcurrentDictionary<uint, Func<IObject>> TypeToConstructors = new();

    static SerializerObjectMappings()
    {
        InitTypeMappings();
    }

    public static void CreateConstructIdToTypeMappingsFromAssembly(Assembly tlObjectInThisAssembly)
    {
        var types = tlObjectInThisAssembly.GetTypes();

        foreach (var type in types)
        {
            var attr = type.GetCustomAttribute<TlObjectAttribute>();
            if (attr != null)
            {
                TypeMappingDict.TryAdd(attr.ConstructorId, type);

                // TVector need process using other ways
                if (attr.ConstructorId != VectorConstructorId)
                {
                    TypeToConstructors.TryAdd(attr.ConstructorId,
                        MyReflectionHelper.CompileConstructor<IObject>(type));

                    //TypeToConstructors2.TryAdd(attr.ConstructorId,
                    //    MyReflectionHelper.CompileConstructor<IObject2>(type));
                }
            }
        }
    }

    private static void InitTypeMappings()
    {
        CreateConstructIdToTypeMappingsFromAssembly(typeof(IObject).Assembly);
        RegisterLegacyConstructorAliases();
    }

    /// <summary>
    /// Some persisted blobs (channel admin log events, saved messages, etc.) were
    /// serialized under an older layer constructor id. New layers add fields only
    /// behind unused flag bits, so the legacy payload is bit-compatible with the
    /// current Deserialize routine — we just need to teach the dispatcher to
    /// route the old id to the current type.
    /// </summary>
    private static void RegisterLegacyConstructorAliases()
    {
        // message#9cb490e9 (layer 221-222) → message#3ae56482 (layer 223+).
        // The only diff is the addition of from_rank:flags2.12?string, which is
        // never set in old payloads, so TMessage.Deserialize reads them as-is.
        AliasConstructor(0x9cb490e9, typeof(TMessage));
    }

    private static void AliasConstructor(uint legacyConstructorId, Type currentType)
    {
        if (TypeMappingDict.ContainsKey(legacyConstructorId)) return;
        TypeMappingDict.TryAdd(legacyConstructorId, currentType);
        TypeToConstructors.TryAdd(legacyConstructorId,
            MyReflectionHelper.CompileConstructor<IObject>(currentType));
    }

    public static void TryAddTlObjectFuncToCache(Type typeOfT,
        Func<IObject> func)
    {
        GenericTypeOfTConstructors.TryAdd(typeOfT, func);
    }

    public static bool TryGetTlObject(Type typeOfT,
        [NotNullWhen(true)] out Func<IObject>? func)
    {
        return GenericTypeOfTConstructors.TryGetValue(typeOfT, out func);
    }

    public static bool TryGetTlObject(uint constructorId,
        out Func<IObject>? func)
    {
        return TypeToConstructors.TryGetValue(constructorId, out func);
    }

    public static bool TryGetTlObjectType(uint constructorId, [NotNullWhen(true)] out Type? type)
    {
        return TypeMappingDict.TryGetValue(constructorId, out type);
    }
}
