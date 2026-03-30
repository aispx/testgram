namespace MyTelegram;

public record GlobalPrivacySettings(
    bool ArchiveAndMuteNewNoncontactPeers,
    bool KeepArchivedUnmuted,
    bool KeepArchivedFolders,
    bool HideReadMarks,
    bool NewNoncontactPeersRequirePremium,
    long? NoncontactPeersPaidStars,
    bool DisallowUnlimitedStargifts = false,
    bool DisallowLimitedStargifts = false,
    bool DisallowUniqueStargifts = false,
    bool DisallowPremiumGifts = false
    );
