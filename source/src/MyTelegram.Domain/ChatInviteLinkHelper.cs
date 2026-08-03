using System.Buffers.Text;
using System.Security.Cryptography;

namespace MyTelegram.Domain;

public class ChatInviteLinkHelper : IChatInviteLinkHelper
{
    public string GenerateInviteLink()
    {
        // The hash is the whole secret of a "+"-style invite link - anyone holding it can join the chat -
        // so it must be unguessable. Random.Shared is xoshiro256**, whose state is recoverable from a
        // handful of previously issued links, which would let an attacker predict later invite hashes.
        var bytes = RandomNumberGenerator.GetBytes(12);

        return Base64Url.EncodeToString(bytes);
    }

    public string GetHashFromLink(string link)
    {
        if (link.StartsWith("tg://join?", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(link, UriKind.Absolute, out var tgUri))
        {
            var invite = tgUri.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(part => part.Length == 2 && part[0].Equals("invite", StringComparison.OrdinalIgnoreCase));

            if (invite != null)
            {
                return Uri.UnescapeDataString(invite[1]);
            }
        }

        var index = link.LastIndexOf("/", StringComparison.OrdinalIgnoreCase);

        var newLink = link[(index + 1)..];
        var queryOrFragmentIndex = newLink.IndexOfAny(['?', '#']);
        if (queryOrFragmentIndex >= 0)
        {
            newLink = newLink[..queryOrFragmentIndex];
        }

        if (newLink.StartsWith("+"))
        {
            return newLink[1..];
        }

        return newLink;
    }

    public string GetChatlistFullLink(string domain, string link)
    {
        return GetFullLinkCore(domain, "addlist/", link);
    }

    public string GetFullLink(string domain, string link)
    {
        return GetFullLinkCore(domain, "+", link);
    }

    private string GetFullLinkCore(string domain, string type, string link)
    {
        var newDomain = domain;
        if (!newDomain.EndsWith("/"))
        {
            newDomain = $"{domain}/";
        }

        return $"{newDomain}{type}{link}";
    }
}
