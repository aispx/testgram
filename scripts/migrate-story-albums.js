// Migrates story albums from the old per-story representation to the story_albums collection.
//
// Before: each story carried a single AlbumId plus a duplicated AlbumTitle.
// After:  albums are documents in story_albums, and each story lists its albums in AlbumIds.
//
// Safe to re-run: albums already present are left alone and stories already migrated are skipped.
//
// Usage:
//   docker compose -p mytelegram exec -T mongodb mongosh tg < scripts/migrate-story-albums.js

const stories = db.getCollection("stories");
const albums = db.getCollection("story_albums");
const counters = db.getCollection("counters");

const legacy = stories.find({ AlbumId: { $exists: true, $ne: null } }).toArray();

if (legacy.length === 0) {
    print("No legacy albums found, nothing to migrate.");
} else {
    print(`Found ${legacy.length} stories with a legacy AlbumId.`);

    // Group the legacy stories by their owning peer and album.
    const grouped = new Map();
    for (const story of legacy) {
        const key = `${story.OwnerPeerType}-${story.OwnerPeerId}-${story.AlbumId}`;
        if (!grouped.has(key)) {
            grouped.set(key, {
                ownerPeerId: story.OwnerPeerId,
                ownerPeerType: story.OwnerPeerType,
                albumId: story.AlbumId,
                title: story.AlbumTitle || `Album ${story.AlbumId}`,
                stories: []
            });
        }
        grouped.get(key).stories.push(story);
    }

    let order = 0;
    let createdAlbums = 0;

    for (const group of grouped.values()) {
        const id = `album-${group.ownerPeerType}-${group.ownerPeerId}-${group.albumId}`;

        if (albums.countDocuments({ _id: id }) === 0) {
            // The newest story with usable media becomes the cover, matching StoryAlbumService.
            const cover = group.stories
                .filter(s => !s.Deleted && s.MediaFileId && s.MediaFileId !== 0)
                .sort((a, b) => b.StoryId - a.StoryId)[0];

            albums.insertOne({
                _id: id,
                OwnerPeerId: group.ownerPeerId,
                OwnerPeerType: group.ownerPeerType,
                AlbumId: group.albumId,
                Title: group.title,
                Order: order,
                IconStoryId: cover ? cover.StoryId : 0,
                StoryOrder: [],
                Date: Math.floor(Date.now() / 1000)
            });

            createdAlbums++;
        }

        order++;

        // Keep the album id as the counter high-water mark so new albums do not collide.
        const counterId = `story_album_id_${group.ownerPeerType}_${group.ownerPeerId}`;
        const counter = counters.findOne({ _id: counterId });
        if (!counter || counter.seq < group.albumId) {
            counters.updateOne(
                { _id: counterId },
                { $set: { seq: group.albumId } },
                { upsert: true });
        }
    }

    const migrated = stories.updateMany(
        { AlbumId: { $exists: true, $ne: null } },
        [
            { $set: { AlbumIds: { $ifNull: ["$AlbumIds", ["$AlbumId"]] } } },
            { $unset: ["AlbumId", "AlbumTitle"] }
        ]);

    print(`Created ${createdAlbums} albums, migrated ${migrated.modifiedCount} stories.`);
}

// Stories that never had an album still need the new field so filters behave.
const backfilled = stories.updateMany(
    { AlbumIds: { $exists: false } },
    { $set: { AlbumIds: [] } });

print(`Backfilled AlbumIds on ${backfilled.modifiedCount} stories.`);
