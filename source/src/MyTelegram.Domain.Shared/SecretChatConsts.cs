// ReSharper disable once CheckNamespace

namespace MyTelegram;

public static class SecretChatConsts
{
    /// <summary>
    /// Fixed positive initial qts value assigned to every Authorization_Key's
    /// secret-chat temporary update box. Identical for all Authorization_Keys.
    /// See Requirements 12.3, 13.5.
    /// </summary>
    public const int QtsInitialValue = 1;
}
