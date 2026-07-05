using System.Buffers.Text;

namespace MyTelegram.Domain;

public class ChatInviteLinkHelper : IChatInviteLinkHelper
{
    public string GenerateInviteLink()
    {
        var bytes = new byte[12];
        Random.Shared.NextBytes(bytes);

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
