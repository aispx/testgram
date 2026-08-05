using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Builds the <c>starGift</c> TL object for a non-upgraded gift from its stored metadata.
/// </summary>
/// <remarks>
/// <para>
/// <c>starGift#313a9547</c> reuses single flag bits for several fields:
/// bit 0 marks <c>limited</c> <em>and</em> guards <c>availability_remains</c> /
/// <c>availability_total</c>; bit 1 marks <c>sold_out</c> <em>and</em> guards
/// <c>first_sale_date</c> / <c>last_sale_date</c>. The generated serializer sets the bit from the
/// boolean but only writes the numbers when they have values, while the deserializer reads them
/// whenever the bit is set. So a gift flagged <c>limited</c> without availability counts — the
/// shape incomplete metadata produces — writes a stream the client reads at the wrong offset,
/// and every field after it is garbage.
/// </para>
/// <para>
/// Rather than patch the auto-generated schema, this normalizes the pairs before serialization:
/// a flag is only claimed when the fields it guards are actually present.
/// </para>
/// </remarks>
public static class StarGiftTlBuilder
{
    /// <summary>
    /// Maps stored gift metadata onto a wire-safe <see cref="TStarGift"/>.
    /// </summary>
    /// <param name="giftId">Gift type id.</param>
    /// <param name="stars">Price in stars.</param>
    /// <param name="convertStars">Stars the recipient may convert the gift into.</param>
    /// <param name="upgradeStars">Cost to upgrade to a collectible, when upgradable.</param>
    /// <param name="sticker">Sticker document representing the gift.</param>
    /// <param name="meta">Gift-type metadata, when the type is known.</param>
    public static TStarGift Build(
        long giftId,
        long stars,
        long convertStars,
        long? upgradeStars,
        IDocument sticker,
        StarGiftDocument? meta)
    {
        // Only claim `limited` when both availability numbers are present, since the bit also
        // guards them on the wire.
        var hasAvailability = meta?.AvailabilityRemains is not null && meta.AvailabilityTotal is not null;
        var hasSaleDates = meta?.FirstSaleDate is not null && meta.LastSaleDate is not null;

        return new TStarGift
        {
            Id = giftId,
            Stars = stars,
            ConvertStars = convertStars,
            UpgradeStars = upgradeStars,
            Title = meta?.Title,
            Limited = (meta?.Limited ?? false) && hasAvailability,
            SoldOut = (meta?.SoldOut ?? false) && hasSaleDates,
            AvailabilityRemains = hasAvailability ? meta!.AvailabilityRemains : null,
            AvailabilityTotal = hasAvailability ? meta!.AvailabilityTotal : null,
            FirstSaleDate = hasSaleDates ? meta!.FirstSaleDate : null,
            LastSaleDate = hasSaleDates ? meta!.LastSaleDate : null,
            Sticker = sticker,
        };
    }
}
