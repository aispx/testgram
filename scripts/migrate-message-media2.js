// Migration script to update Media2 in messages with sticker documents
// This adds stickerset information to documents in message Media2 field
// Run with: mongosh tg < migrate-message-media2.js

print("Starting migration: Updating Media2 in messages with stickers...");

const messages = db["eventflow-messagereadmodel"].find({
    "Media2._t": "MyTelegram.Schema.TMessageMediaDocument"
}).toArray();

print(`Found ${messages.length} messages with documents`);

let updatedCount = 0;
let skippedCount = 0;
let errorCount = 0;

for (const msg of messages) {
    try {
        if (!msg.Media2 || !msg.Media2.Document) {
            skippedCount++;
            continue;
        }

        const docId = msg.Media2.Document.Id;

        // Load document from documentreadmodel to get Attributes2
        const docReadModel = db["eventflow-documentreadmodel"].findOne({ DocumentId: docId });

        if (!docReadModel || !docReadModel.Attributes2 || docReadModel.Attributes2.length === 0) {
            skippedCount++;
            continue;
        }

        // Check if document has sticker attribute with stickerset
        const hasStickerSet = docReadModel.Attributes2.some(attr =>
            attr._t === "MyTelegram.Schema.TDocumentAttributeSticker" &&
            attr.Stickerset &&
            attr.Stickerset._t === "MyTelegram.Schema.TInputStickerSetID"
        );

        if (!hasStickerSet) {
            skippedCount++;
            continue;
        }

        // Update Media2.Document.Attributes with Attributes2 from documentreadmodel
        msg.Media2.Document.Attributes = docReadModel.Attributes2;

        // Update the message
        const result = db["eventflow-messagereadmodel"].updateOne(
            { _id: msg._id },
            { $set: { "Media2": msg.Media2 } }
        );

        if (result.modifiedCount > 0) {
            updatedCount++;
            if (updatedCount % 100 === 0) {
                print(`  Updated ${updatedCount} messages...`);
            }
        }
    } catch (e) {
        print(`  Error updating message ${msg._id}: ${e}`);
        errorCount++;
    }
}

print(`\nMigration complete!`);
print(`Updated: ${updatedCount} messages`);
print(`Skipped: ${skippedCount} messages`);
print(`Errors: ${errorCount}`);
