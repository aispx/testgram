using System.Reflection;
using Moq;

namespace MyTelegram.Messenger.Tests.AccountDeletion;

/// <summary>
/// Calls a handler's <c>HandleCoreAsync</c>, which is protected, with a request input that only
/// carries what these tests care about: the caller's user id and auth keys.
/// </summary>
internal static class HandlerInvoker
{
    public static async Task<object?> InvokeAsync(object handler, object request, long userId, long authKeyId = 777)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(userId);
        input.SetupGet(p => p.AuthKeyId).Returns(authKeyId);
        input.SetupGet(p => p.PermAuthKeyId).Returns(authKeyId);
        input.SetupGet(p => p.ConnectionId).Returns(string.Empty);

        var method = handler.GetType().GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task task;
        try
        {
            task = (Task)method.Invoke(handler, [input.Object, request])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        await task;

        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
}
