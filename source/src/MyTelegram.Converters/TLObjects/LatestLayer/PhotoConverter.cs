using MyTelegram.Services.Services;

namespace MyTelegram.Converters.TLObjects.LatestLayer;

public class PhotoConverter(IFileReferenceHelper fileReferenceHelper) : IPhotoConverter, ITransientDependency
{

    public virtual int Layer => Layers.LayerLatest;

    public virtual IChatPhoto ToChatPhoto(IPhotoReadModel? photoReadModel)
    {
        if (photoReadModel == null)
        {
            return new TChatPhotoEmpty();
        }

        return new TChatPhoto
        {
            DcId = photoReadModel.DcId,
            PhotoId = photoReadModel.PhotoId,
            HasVideo = photoReadModel.VideoSizes2?.Count > 0
        };
    }

    public virtual IPhoto ToPhoto(IPhotoReadModel? photoReadModel)
    {
        if (photoReadModel == null)
        {
            return new TPhotoEmpty();
        }

        var photo = new TPhoto
        {
            HasStickers = photoReadModel.HasStickers,
            Id = photoReadModel.PhotoId,
            AccessHash = photoReadModel.AccessHash,
            Date = photoReadModel.Date,
            DcId = photoReadModel.DcId,
            // Minted per response rather than read from the row, as documents are.
            // See https://corefork.telegram.org/api/file-references
            FileReference = fileReferenceHelper.Create(AccessHashType.Photo, photoReadModel.PhotoId)
        };

        if (photoReadModel.Sizes2 != null)
        {
            photo.Sizes = new TVector<IPhotoSize>(photoReadModel.Sizes2);
        }

        if (photoReadModel.VideoSizes2 != null)
        {
            photo.VideoSizes = new TVector<IVideoSize>(photoReadModel.VideoSizes2);
        }

        // Used for compatibility with old data, new data will only use Sizes2 and VideoSizes2
        if (photoReadModel.Sizes?.Count > 0)
        {
            photo.Sizes = new TVector<IPhotoSize>();
            foreach (var s in photoReadModel.Sizes)
            {
                //photo.Sizes.Add(new TPhotoSize
                //{
                //    H = s.H,
                //    W = s.W,
                //    Size = (int)s.Size,
                //    Type = s.Type
                //});
                IPhotoSize size;
                switch (s.Type)
                {
                    case "i":
                        size = new TPhotoStrippedSize
                        {
                            Bytes = s.StrippedThumb,
                            Type = s.Type
                        };
                        break;
                    default:
                        size = new TPhotoSize
                        {
                            H = s.H,
                            W = s.W,
                            Size = (int)s.Size,
                            Type = s.Type
                        };
                        break;
                }

                photo.Sizes.Add(size);
            }
        }

        if (photoReadModel.VideoSizes?.Count > 0)
        {
            photo.VideoSizes = new TVector<IVideoSize>();
            foreach (var s in photoReadModel.VideoSizes)
            {
                photo.VideoSizes.Add(new TVideoSize
                {
                    H = s.H,
                    W = s.W,
                    Size = (int)s.Size,
                    Type = s.Type,
                    VideoStartTs = s.VideoStartTs
                });
            }
        }

        return photo;
    }

    public virtual IUserProfilePhoto ToProfilePhoto(IPhotoReadModel? photoReadModel)
    {
        if (photoReadModel == null)
        {
            return new TUserProfilePhotoEmpty();
        }

        return new TUserProfilePhoto
        {
            DcId = photoReadModel.DcId,
            PhotoId = photoReadModel.PhotoId,
            HasVideo = photoReadModel.VideoSizes2?.Count > 0,
            StrippedThumb = GetProfileStrippedThumb(photoReadModel)
        };
    }

    private static ReadOnlyMemory<byte>? GetProfileStrippedThumb(IPhotoReadModel photoReadModel)
    {
        var strippedThumb = photoReadModel.Sizes2?
            .OfType<TPhotoStrippedSize>()
            .Select(p => (ReadOnlyMemory<byte>?)p.Bytes)
            .FirstOrDefault(IsValidStrippedThumb);

        if (strippedThumb is { Length: > 3 })
        {
            return strippedThumb;
        }

        return photoReadModel.Sizes?
            .Select(p => (ReadOnlyMemory<byte>?)p.StrippedThumb)
            .FirstOrDefault(IsValidStrippedThumb);
    }

    private static bool IsValidStrippedThumb(ReadOnlyMemory<byte>? strippedThumb)
    {
        return strippedThumb is { Length: > 3 };
    }
}
