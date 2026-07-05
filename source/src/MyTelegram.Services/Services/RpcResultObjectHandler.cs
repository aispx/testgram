using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace MyTelegram.Services.Services;

public abstract class RpcResultObjectHandler<TInput, TOutput> : BaseObjectHandler<TInput, TOutput>
    where TInput : IRequest<TOutput>
    where TOutput : IObject
{
    //private readonly IGZipHelper _gzipHelper = new GZipHelper();
    public override async Task<IObject> HandleAsync(IRequestInput request,
        IObject obj)
    {
        var r = await base.HandleAsync(request, obj);
        if (r == null!)
        {
            return null!;
        }

        // Safety net: a TL object (document/photo/thumbnail) that goes out with a DcId
        // outside the advertised Testgram DC range points Android at a non-existent
        // datacenter. The client can never download that media and keeps hot-looping
        // help.getConfig / help.getPeerColors trying to discover the missing DC
        // (see MessagesController/tgnet updateDcSettings). Normalise any such DcId
        // to the media DC and log the culprit so the offending builder can be fixed
        // at the source.
        DcIdNormalizer.Normalize(r, GetType().Name);

        var rpcResult = new TRpcResult { ReqMsgId = request.ReqMsgId, Result = r };
        //var length = r.GetLength();
        //if (length > 500)
        //{
        //    var gzipPacked = new TGzipPacked
        //    {
        //        PackedData = _gzipHelper.Compress(r.ToBytes())
        //    };
        //    rpcResult.Result = gzipPacked;
        //}
        return rpcResult;
    }
}

internal static class DcIdNormalizer
{
    private const int MediaDcId = 2;
    private const int MinAdvertisedDcId = 1;
    private const int MaxAdvertisedDcId = 5;
    private const int MaxDepth = 32;

    // Cache of the reflection metadata we care about, per concrete type.
    private static readonly ConcurrentDictionary<Type, TypeInfoCache> Cache = new();

    private sealed class TypeInfoCache
    {
        public PropertyInfo? DcIdProperty;
        public PropertyInfo[] ObjectProperties = [];
    }

    public static void Normalize(object? root, string handlerName)
    {
        if (root == null)
        {
            return;
        }

        try
        {
            Walk(root, handlerName, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }
        catch
        {
            // Diagnostics/normalisation must never break a real response.
        }
    }

    private static void Walk(object? node, string handlerName, int depth, HashSet<object> visited)
    {
        if (node == null || depth > MaxDepth)
        {
            return;
        }

        var type = node.GetType();
        if (type.IsPrimitive || node is string || node is byte[] || type.IsEnum)
        {
            return;
        }

        if (!type.IsValueType && !visited.Add(node))
        {
            return;
        }

        // A TL object (IObject): check its DcId and recurse its object-typed properties.
        // Note: TVector<T> implements BOTH IObject and IList<T>, so we must ALSO iterate
        // its elements below - the vector items are where messages/documents/dcOptions live.
        if (node is IObject)
        {
            var info = Cache.GetOrAdd(type, BuildTypeInfo);

            if (info.DcIdProperty != null)
            {
                var value = info.DcIdProperty.GetValue(node);
                if (value is int dc && IsUnknownDcId(dc))
                {
                    info.DcIdProperty.SetValue(node, MediaDcId);
                    var id = type.GetProperty("Id")?.GetValue(node);
                    Console.WriteLine(
                        $"[DcIdNormalizer] Normalized invalid DcId ({dc}) -> {MediaDcId} on {type.Name} (Id={id}) returned by {handlerName}");
                }
            }

            foreach (var prop in info.ObjectProperties)
            {
                object? child;
                try
                {
                    child = prop.GetValue(node);
                }
                catch
                {
                    continue;
                }

                Walk(child, handlerName, depth + 1, visited);
            }
        }

        // Any enumerable (TVector<T>, List<T>, arrays, ...): walk each element.
        if (node is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    var it = item.GetType();
                    if (!it.IsPrimitive && item is not string && item is not byte[])
                    {
                        Walk(item, handlerName, depth + 1, visited);
                    }
                }
            }
        }
    }

    private static TypeInfoCache BuildTypeInfo(Type type)
    {
        var cache = new TypeInfoCache();
        var objectProps = new List<PropertyInfo>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || !prop.CanRead)
            {
                continue;
            }

            if (prop.Name == "DcId" && prop.PropertyType == typeof(int) && prop.CanWrite)
            {
                cache.DcIdProperty = prop;
                continue;
            }

            var pt = prop.PropertyType;
            if (pt.IsPrimitive || pt == typeof(string) || pt.IsEnum || pt == typeof(byte[]))
            {
                continue;
            }

            // Only recurse into TL objects and collections that may contain them.
            if (typeof(IObject).IsAssignableFrom(pt) || typeof(IEnumerable).IsAssignableFrom(pt))
            {
                objectProps.Add(prop);
            }
        }

        cache.ObjectProperties = [.. objectProps];
        return cache;
    }

    private static bool IsUnknownDcId(int dcId) =>
        dcId < MinAdvertisedDcId || dcId > MaxAdvertisedDcId;
}
