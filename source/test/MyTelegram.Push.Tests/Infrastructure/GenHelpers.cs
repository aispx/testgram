using FsCheck;

namespace MyTelegram.Push.Tests.Infrastructure;

/// <summary>Small composition helpers on top of FsCheck's <see cref="Gen{T}"/> primitives.</summary>
public static class GenHelpers
{
    /// <summary>
    /// Generates a fixed-length array by chaining <paramref name="length"/> draws of
    /// <paramref name="element"/>. Built only from <c>Gen.Constant</c>/<c>Select</c>/<c>SelectMany</c>
    /// so it is independent of FsCheck-version-specific array overloads.
    /// </summary>
    public static Gen<T[]> ArrayOfLength<T>(int length, Gen<T> element)
    {
        var acc = Gen.Constant(Array.Empty<T>());
        for (var i = 0; i < length; i++)
        {
            acc = acc.SelectMany(arr => element.Select(x =>
            {
                var next = new T[arr.Length + 1];
                Array.Copy(arr, next, arr.Length);
                next[arr.Length] = x;
                return next;
            }));
        }

        return acc;
    }
}
