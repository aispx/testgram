using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MyTelegram.ReadModel.MongoDB;

/// <summary>
/// Provides DiscriminatedInterfaceSerializer for all IObject-derived interfaces,
/// so MongoDB can resolve _t discriminators when deserializing interface-typed fields.
/// </summary>
internal sealed class IObjectInterfaceSerializationProvider(Type baseType) : IBsonSerializationProvider
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IBsonSerializer?> _cache = new();

    public IBsonSerializer? GetSerializer(Type type)
    {
        if (!type.IsInterface || !type.IsAssignableTo(baseType) || type == baseType)
            return null;

        return _cache.GetOrAdd(type, t =>
        {
            var serializerType = typeof(DiscriminatedInterfaceSerializer<>).MakeGenericType(t);
            return (IBsonSerializer)Activator.CreateInstance(serializerType)!;
        });
    }
}
