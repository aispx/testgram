// Migration script to add stickerset information to document Attributes2
// Run with: mongosh tg < migrate-sticker-attributes.js

print("Starting migration: Adding stickerset info to document Attributes2...");

const stickerSets = db["eventflow-stickersetreadmodel"].find({}).toArray();
print(`Found ${stickerSets.length} sticker sets`);

let updatedCount = 0;
let errorCount = 0;

for (const stickerSet of stickerSets) {
    const setId = stickerSet.StickerSetId;
    const accessHash = stickerSet.AccessHash;
    const shortName = stickerSet.ShortName || stickerSet.Slug;
    const documentIds = stickerSet.DocumentIds || [];

    print(`Processing sticker set: ${shortName} (${setId}) with ${documentIds.length} documents`);

    for (const docId of documentIds) {
        try {
            const doc = db["eventflow-documentreadmodel"].findOne({ DocumentId: docId });

            if (!doc) {
                print(`  Warning: Document ${docId} not found`);
                continue;
            }

            // Create Attributes2 with stickerset information
            const attributes2 = [
                {
                    _t: "MyTelegram.Schema.TDocumentAttributeSticker",
                    Alt: "",
                    Stickerset: {
                        _t: "MyTelegram.Schema.TInputStickerSetID",
                        Id: setId,
                        AccessHash: accessHash
                    },
                    Mask: false,
                    MaskCoords: null
                }
            ];

            // Update the document
            const result = db["eventflow-documentreadmodel"].updateOne(
                { DocumentId: docId },
                { $set: { Attributes2: attributes2 } }
            );

            if (result.modifiedCount > 0) {
                updatedCount++;
            }
        } catch (e) {
            print(`  Error updating document ${docId}: ${e}`);
            errorCount++;
        }
    }
}

print(`\nMigration complete!`);
print(`Updated: ${updatedCount} documents`);
print(`Errors: ${errorCount}`);
