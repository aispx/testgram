// Migrate all stickers in all sticker sets to have Attributes2 with stickerset info

const db = db.getSiblingDB('tg');

const stickerSets = db['eventflow-stickersetreadmodel'].find({}).toArray();
print(`Found ${stickerSets.length} sticker sets`);

let totalUpdated = 0;
let totalSkipped = 0;
let totalErrors = 0;

for (const set of stickerSets) {
    const setId = set.StickerSetId;
    const accessHash = set.AccessHash;
    const shortName = set.ShortName || set.Slug;

    if (!set.DocumentIds || set.DocumentIds.length === 0) {
        print(`Skipping empty set: ${shortName}`);
        continue;
    }

    print(`\nProcessing set: ${shortName} (ID: ${setId}, ${set.DocumentIds.length} stickers)`);

    for (const docId of set.DocumentIds) {
        try {
            const doc = db['eventflow-documentreadmodel'].findOne({ DocumentId: docId });

            if (!doc) {
                print(`  ⚠️  Document ${docId} not found`);
                totalErrors++;
                continue;
            }

            // Check if Attributes2 already has stickerset info
            if (doc.Attributes2 && doc.Attributes2.length > 0) {
                const hasStickerAttr = doc.Attributes2.some(attr =>
                    attr._t === 'TDocumentAttributeSticker' &&
                    attr.Stickerset &&
                    attr.Stickerset._t === 'TInputStickerSetID'
                );

                if (hasStickerAttr) {
                    totalSkipped++;
                    continue;
                }
            }

            // Build Attributes2 with stickerset info
            const attributes2 = [
                {
                    _t: 'TDocumentAttributeSticker',
                    Alt: '',
                    Stickerset: {
                        _t: 'TInputStickerSetID',
                        Id: setId,
                        AccessHash: accessHash
                    },
                    Mask: false
                }
            ];

            // Preserve existing non-sticker attributes
            if (doc.Attributes2 && doc.Attributes2.length > 0) {
                for (const attr of doc.Attributes2) {
                    if (attr._t !== 'TDocumentAttributeSticker') {
                        attributes2.push(attr);
                    }
                }
            }

            // Update document
            db['eventflow-documentreadmodel'].updateOne(
                { DocumentId: docId },
                { $set: { Attributes2: attributes2 } }
            );

            totalUpdated++;

        } catch (e) {
            print(`  ❌ Error processing document ${docId}: ${e.message}`);
            totalErrors++;
        }
    }
}

print(`\n=== Migration Complete ===`);
print(`Updated: ${totalUpdated}`);
print(`Skipped (already migrated): ${totalSkipped}`);
print(`Errors: ${totalErrors}`);
