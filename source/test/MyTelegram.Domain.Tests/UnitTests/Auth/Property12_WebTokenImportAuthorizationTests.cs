// Feature: auth-methods-completion, Property 12: Web token import authorizes only valid api id + resolvable token
//
// For any import request, importWebTokenAuthorization raises 400 API_ID_INVALID when the api id is
// not valid, raises AUTH_TOKEN_INVALID when the token does not resolve to a user, and otherwise
// returns an IAuthorization for the resolved user while binding that user to the session.
//
// Validates: Requirements 6.1, 6.2, 6.3
//
// Approach: this single parametric property drives the production (internal)
// ImportWebTokenAuthorizationHandler via reflection (mirroring Property 1/2/3/4/6/7/8) with a mix
// of hand-rolled fakes and Moq mocks:
//   * FakeWebTokenAuthCacheHelper resolves the WebAuthToken to a WebTokenCacheItem for the Success
//     branch, or returns false (unresolvable) for the TokenNotResolvable branch.
//   * CapturingEventBus records every published BindUserIdToSessionEvent so the test can assert the
//     resolved user is bound to the session on success (and that NO event is published on either
//     rejection branch).
//   * IUserAppService / IPhotoAppService / IUserConverterService / ILayeredService<IAuthorizationConverter>
//     are mocked so the happy path can build a non-null IAuthorization (the mocked converter returns
//     a concrete TAuthorization; the user read model resolves to null, exercising the null-user path
//     that ImportLoginTokenHandler shares).
//
// Note: the api-id check in the handler is "obj.ApiId <= 0 -> 400 API_ID_INVALID" (there is no
// server-side registry of api ids), so the generator produces non-positive api ids for the
// ApiIdInvalid branch and positive api ids for the two positive-api-id branches.
//
// The three generated branches cover exactly the three documented outcomes:
//   * ApiIdInvalid       (api id <= 0)                -> 400 API_ID_INVALID, no event published
//   * TokenNotResolvable (api id > 0, token misses)   -> 400 AUTH_TOKEN_INVALID, no event published
//   * Success            (api id > 0, token resolves) -> non-null IAuthorization + one
//                                                        BindUserIdToSessionEvent for the resolved user

using System.Reflection;
using EventFlow.Queries;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using MyTelegram.Abstractions;
using MyTelegram.Converters.TLObjects.Interfaces;
using MyTelegram.Core;
using MyTelegram.EventBus;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Auth;
using MyTelegram.Services.Services;
using MyTelegram.Services.TLObjectConverters;

namespace MyTelegram.Domain.Tests.UnitTests.Auth;

public class Property12_WebTokenImportAuthorizationTests
{
    // Property 12: Web token import authorizes only valid api id + resolvable token
    // Validates: Requirements 6.1, 6.2, 6.3
    [Property(Arbitrary = new[] { typeof(WebTokenImportArbitraries) }, MaxTest = 100)]
    public void Web_token_import_authorizes_only_valid_api_id_and_resolvable_token(WebTokenImportCase testCase)
    {
        // Arrange: the token store resolves only for the Success branch.
        var resolvedItem = testCase.Branch == ImportBranch.Success
            ? new WebTokenCacheItem(testCase.ResolvedUserId, testCase.ApiId)
            : null;
        var cacheHelper = new FakeWebTokenAuthCacheHelper(testCase.WebAuthToken, resolvedItem);
        var eventBus = new CapturingEventBus();

        // The resolved user read model is null here (mirroring the shared null-user path in
        // ImportLoginTokenHandler); the mocked converter still yields a non-null authorization.
        var userAppService = new Mock<IUserAppService>();
        userAppService.Setup(x => x.GetAsync(It.IsAny<long>())).ReturnsAsync((IUserReadModel)null!);

        var photoAppService = new Mock<IPhotoAppService>();
        photoAppService
            .Setup(x => x.GetPhotosAsync(It.IsAny<IUserReadModel?>(), It.IsAny<IContactReadModel?>()))
            .ReturnsAsync(Array.Empty<IPhotoReadModel>());

        var userConverterService = new Mock<IUserConverterService>();

        var authorization = new MyTelegram.Schema.Auth.TAuthorization();
        var authorizationConverter = new Mock<IAuthorizationConverter>();
        authorizationConverter
            .Setup(x => x.CreateAuthorization(It.IsAny<IUser?>(), It.IsAny<bool>()))
            .Returns(authorization);

        var layeredService = new Mock<ILayeredService<IAuthorizationConverter>>();
        layeredService.Setup(x => x.GetConverter(It.IsAny<int>())).Returns(authorizationConverter.Object);

        // Constructor arg order (post-simplification, no IOptionsMonitor):
        // (IWebTokenAuthCacheHelper, IEventBus, IUserAppService,
        //  ILayeredService<IAuthorizationConverter>, IUserConverterService, IPhotoAppService)
        var handler = CreateMessengerHandler(
            "MyTelegram.Messenger.Handlers.LatestLayer.Auth.ImportWebTokenAuthorizationHandler",
            cacheHelper,
            eventBus,
            userAppService.Object,
            layeredService.Object,
            userConverterService.Object,
            photoAppService.Object);

        var request = new RequestImportWebTokenAuthorization
        {
            ApiId = testCase.ApiId,
            ApiHash = testCase.ApiHash,
            WebAuthToken = testCase.WebAuthToken
        };
        var input = CreateRequestInput();

        // Act + Assert: each branch produces its documented outcome.
        switch (testCase.Branch)
        {
            case ImportBranch.ApiIdInvalid:
                // Requirement 6.2: non-positive (invalid) api id -> 400 API_ID_INVALID, before any
                // token resolution or session binding.
                var apiEx = Should.Throw<RpcException>(() => InvokeAsync(handler, input, request));
                apiEx.RpcError.ErrorCode.ShouldBe(400);
                apiEx.RpcError.Message.ShouldBe("API_ID_INVALID");
                eventBus.Events.Count.ShouldBe(0);
                break;

            case ImportBranch.TokenNotResolvable:
                // Requirement 6.3: api id valid but token does not resolve -> 400 AUTH_TOKEN_INVALID,
                // and no user is bound to the session.
                var tokenEx = Should.Throw<RpcException>(() => InvokeAsync(handler, input, request));
                tokenEx.RpcError.ErrorCode.ShouldBe(400);
                tokenEx.RpcError.Message.ShouldBe("AUTH_TOKEN_INVALID");
                eventBus.Events.Count.ShouldBe(0);
                break;

            default:
                // Requirement 6.1: valid api id + resolvable token -> a non-null IAuthorization for the
                // resolved user, with that user bound to the session via BindUserIdToSessionEvent.
                var result = InvokeAsync(handler, input, request);
                var rpcResult = result.ShouldBeOfType<TRpcResult>();
                rpcResult.Result.ShouldNotBeNull();
                rpcResult.Result.ShouldBe(authorization);

                eventBus.Events.Count.ShouldBe(1);
                eventBus.Events[0].UserId.ShouldBe(testCase.ResolvedUserId);
                break;
        }
    }

    private static IObject InvokeAsync(object handler, IRequestInput input, IObject request)
    {
        var method = handler.GetType().GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;
        object taskObj;
        try
        {
            taskObj = method.Invoke(handler, new object[] { input, request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        return ((Task<IObject>)taskObj).GetAwaiter().GetResult();
    }

    private static RequestInput CreateRequestInput()
    {
        return new RequestInput(
            ConnectionId: "test-connection",
            ConnectionType: default,
            RequestId: Guid.NewGuid(),
            ObjectId: 0u,
            ReqMsgId: 1L,
            SeqNumber: 0,
            UserId: 0L,
            AuthKeyId: 123L,
            PermAuthKeyId: 456L,
            Layer: 0,
            Date: 0L,
            DeviceType: default,
            ClientIp: "127.0.0.1",
            SessionId: 1L,
            AccessHashKeyId: 789L);
    }

    /// <summary>Reflectively constructs an internal sealed handler from the Messenger assembly.</summary>
    private static object CreateMessengerHandler(string typeName, params object[] args)
    {
        var assembly = typeof(MyTelegramMessengerServerOptions).Assembly;
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return Activator.CreateInstance(type, args)!;
    }

    /// <summary>Resolves the configured WebAuthToken to a WebTokenCacheItem (Success branch) or
    /// returns false for any token (unresolvable branch), mirroring the ICacheHelper contract.</summary>
    private sealed class FakeWebTokenAuthCacheHelper(string token, WebTokenCacheItem? item)
        : IWebTokenAuthCacheHelper
    {
        public bool TryAdd(string key, WebTokenCacheItem value) => throw new NotImplementedException();

        public bool TryGetValue(string key, out WebTokenCacheItem? value)
        {
            if (item != null && key == token)
            {
                value = item;
                return true;
            }

            value = null;
            return false;
        }

        public bool TryRemove(string key, out WebTokenCacheItem? value) => throw new NotImplementedException();
    }

    /// <summary>Captures published BindUserIdToSessionEvents so the test can assert the resolved user
    /// is bound on success (and that no event is published on either rejection branch).</summary>
    private sealed class CapturingEventBus : IEventBus
    {
        public List<BindUserIdToSessionEvent> Events { get; } = new();

        public Task PublishAsync<TEventData>(TEventData eventData, string? eventType = null)
            where TEventData : class
        {
            if (eventData is BindUserIdToSessionEvent e)
            {
                Events.Add(e);
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>The branch under test: which of the three documented outcomes this case exercises.</summary>
public enum ImportBranch
{
    ApiIdInvalid,
    TokenNotResolvable,
    Success
}

/// <summary>Input case for Property 12: the branch, the request's api id / api hash / web auth token,
/// and the user id the token resolves to (used only by the Success branch).</summary>
public sealed record WebTokenImportCase(
    ImportBranch Branch,
    int ApiId,
    string ApiHash,
    string WebAuthToken,
    long ResolvedUserId);

/// <summary>FsCheck arbitrary surface for Property 12. Generates each branch paired with an
/// appropriate api id (non-positive for ApiIdInvalid; positive otherwise), a non-empty token/hash,
/// and a positive resolved user id.</summary>
public static class WebTokenImportArbitraries
{
    public static Arbitrary<WebTokenImportCase> WebTokenImportCase()
    {
        var charGen = Gen.Elements(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray());
        var stringGen =
            from len in Gen.Choose(1, 24)
            from chars in Gen.ArrayOf(len, charGen)
            select new string(chars);

        var gen =
            from branch in Gen.Elements(
                ImportBranch.ApiIdInvalid,
                ImportBranch.TokenNotResolvable,
                ImportBranch.Success)
            from apiId in branch == ImportBranch.ApiIdInvalid
                ? Gen.Choose(-2_000_000, 0)          // invalid: api id <= 0
                : Gen.Choose(1, int.MaxValue)        // valid: positive api id
            from apiHash in stringGen
            from token in stringGen
            from userId in Gen.Choose(1, int.MaxValue).Select(i => (long)i)
            select new WebTokenImportCase(branch, apiId, apiHash, token, userId);

        return Arb.From(gen);
    }
}
