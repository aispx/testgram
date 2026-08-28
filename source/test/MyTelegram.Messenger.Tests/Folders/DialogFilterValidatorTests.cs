using MyTelegram.Messenger.Services.Folders;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Folders;

/// <summary>
/// Feature: what <c>messages.updateDialogFilter</c> refuses.
///
/// <para>Nothing was validated before, so a folder with no title, no chats, or one carrying the id of
/// <c>dialogFilterDefault</c> was stored and then served to every client, and a
/// <c>dialogFilterChatlist</c> threw <c>NotImplementedException</c>.</para>
/// See https://corefork.telegram.org/api/folders
/// </summary>
public class DialogFilterValidatorTests
{
    private readonly DialogFilterValidator _validator = new();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void Ids_below_two_are_reserved(int filterId)
    {
        // 0 is dialogFilterDefault; clients allocate their own ids from 2 upwards.
        Should.Throw<RpcException>(() => _validator.Validate(Context(Filter(), filterId: filterId)))
            .RpcError.Message.ShouldBe("FILTER_ID_INVALID");
    }

    [Fact]
    public void An_empty_title_is_refused()
    {
        var filter = Filter();
        filter.Title = new TTextWithEntities { Text = "   ", Entities = [] };

        Should.Throw<RpcException>(() => _validator.Validate(Context(filter)))
            .RpcError.Message.ShouldBe("FILTER_TITLE_EMPTY");
    }

    [Fact]
    public void A_title_longer_than_the_client_limit_is_refused()
    {
        var filter = Filter();
        filter.Title = new TTextWithEntities { Text = new string('x', 13), Entities = [] };

        Should.Throw<RpcException>(() => _validator.Validate(Context(filter)))
            .RpcError.Message.ShouldBe("MESSAGE_TOO_LONG");
    }

    [Fact]
    public void A_folder_that_matches_nothing_is_refused()
    {
        // No type flag and no explicit chat: the folder could never contain anything.
        var filter = Filter();

        Should.Throw<RpcException>(() => _validator.Validate(Context(filter)))
            .RpcError.Message.ShouldBe("FILTER_INCLUDE_EMPTY");
    }

    [Fact]
    public void A_type_flag_alone_is_a_valid_folder()
    {
        var filter = Filter();
        filter.Groups = true;

        _validator.Validate(Context(filter));
    }

    [Fact]
    public void Too_many_chats_hits_the_limit_the_client_knows()
    {
        var filter = Filter();
        filter.IncludePeers = [.. Enumerable.Range(1, 3).Select(IInputPeer (id) => new TInputPeerUser { UserId = id })];

        Should.Throw<RpcException>(() => _validator.Validate(Context(filter, chatsLimit: 2)))
            .RpcError.Message.ShouldBe("FILTER_INCLUDE_TOO_MUCH");
    }

    [Fact]
    public void Too_many_pinned_chats_hits_the_pinned_limit()
    {
        var filter = Filter();
        filter.PinnedPeers = [.. Enumerable.Range(1, 3).Select(IInputPeer (id) => new TInputPeerUser { UserId = id })];

        Should.Throw<RpcException>(() => _validator.Validate(Context(filter, pinnedLimit: 2)))
            .RpcError.Message.ShouldBe("PINNED_DIALOGS_TOO_MUCH");
    }

    [Fact]
    public void A_new_folder_past_the_folder_limit_is_refused()
    {
        var filter = Filter();
        filter.Groups = true;

        Should.Throw<RpcException>(() =>
                _validator.Validate(Context(filter, isNewFilter: true, existingFilterCount: 10, filterLimit: 10)))
            .RpcError.Message.ShouldBe("DIALOG_FILTERS_TOO_MUCH");
    }

    [Fact]
    public void Editing_an_existing_folder_ignores_the_folder_limit()
    {
        var filter = Filter();
        filter.Groups = true;

        _validator.Validate(Context(filter, isNewFilter: false, existingFilterCount: 10, filterLimit: 10));
    }

    [Fact]
    public void A_shared_folder_cannot_be_turned_into_a_plain_one()
    {
        var filter = Filter();
        filter.Groups = true;

        Should.Throw<RpcException>(() => _validator.Validate(Context(filter, existingIsChatlist: true)))
            .RpcError.Message.ShouldBe("FILTER_NOT_SUPPORTED");
    }

    [Fact]
    public void Excluding_peers_from_a_shared_folder_is_refused_with_its_own_error()
    {
        // dialogFilterChatlist has no exclude_peers field at all, so this can only arrive as a plain
        // dialogFilter aimed at a stored shared folder.
        var filter = Filter();
        filter.ExcludePeers = [new TInputPeerUser { UserId = 7 }];

        Should.Throw<RpcException>(() => _validator.Validate(Context(filter, existingIsChatlist: true)))
            .RpcError.Message.ShouldBe("CHATLIST_EXCLUDE_INVALID");
    }

    [Fact]
    public void A_shared_folder_is_edited_with_its_own_constructor()
    {
        var chatlist = new TDialogFilterChatlist
        {
            Id = 5,
            Title = new TTextWithEntities { Text = "Shared", Entities = [] },
            PinnedPeers = [],
            IncludePeers = [new TInputPeerUser { UserId = 11 }]
        };

        _validator.Validate(Context(chatlist, filterId: 5, existingIsChatlist: true));
    }

    [Fact]
    public void A_shared_folder_with_no_chats_is_refused()
    {
        var chatlist = new TDialogFilterChatlist
        {
            Id = 5,
            Title = new TTextWithEntities { Text = "Shared", Entities = [] },
            PinnedPeers = [],
            IncludePeers = []
        };

        Should.Throw<RpcException>(() => _validator.Validate(Context(chatlist, filterId: 5,
                existingIsChatlist: true)))
            .RpcError.Message.ShouldBe("FILTER_INCLUDE_EMPTY");
    }

    [Fact]
    public void The_default_folder_cannot_be_written()
    {
        Should.Throw<RpcException>(() =>
                _validator.Validate(Context(new TDialogFilterDefault(), filterId: 4)))
            .RpcError.Message.ShouldBe("FILTER_NOT_SUPPORTED");
    }

    private static TDialogFilter Filter()
    {
        return new TDialogFilter
        {
            Id = 2,
            Title = new TTextWithEntities { Text = "Folder", Entities = [] },
            PinnedPeers = [],
            IncludePeers = [],
            ExcludePeers = []
        };
    }

    private static DialogFilterValidationContext Context(IDialogFilter filter,
        int filterId = 2,
        bool isNewFilter = false,
        bool existingIsChatlist = false,
        int existingFilterCount = 1,
        int filterLimit = 10,
        int chatsLimit = 100,
        int pinnedLimit = 100)
    {
        return new DialogFilterValidationContext(filterId, filter, isNewFilter, existingIsChatlist,
            existingFilterCount, filterLimit, chatsLimit, pinnedLimit);
    }
}
