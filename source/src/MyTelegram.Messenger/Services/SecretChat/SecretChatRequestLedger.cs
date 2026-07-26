using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.SecretChat;

public class SecretChatRequestLedger(IMongoDatabase mongoDatabase) : ISecretChatRequestLedger, ISingletonDependency
{
    private const string CollectionName = "secret_chat_random_ids";

    private IMongoCollection<SecretChatRequestDocument> Collection =>
        mongoDatabase.GetCollection<SecretChatRequestDocument>(CollectionName);

    public async Task<SecretChatRequestDocument?> FindAsync(long adminId, int randomId)
    {
        var id = SecretChatRequestDocument.BuildId(adminId, randomId);

        return await Collection.Find(d => d.Id == id).FirstOrDefaultAsync();
    }

    public async Task<SecretChatRequestDocument> ReserveAsync(SecretChatRequestDocument document)
    {
        try
        {
            await Collection.InsertOneAsync(document);

            return document;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Known edge: a crash between this insert and CreateEncryptedChatCommand leaves a ledger
            // row without a chat; a retry then returns encryptedChatWaiting for a chat the participant
            // never learned about. Acceptable — the client can discard and retry with a fresh random_id.
            return await Collection.Find(d => d.Id == document.Id).FirstAsync();
        }
    }
}
